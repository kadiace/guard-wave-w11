using System;
using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>스테이지별 스냅샷을 저장하고 마지막 편집을 되돌리는 이력을 관리한다.</summary>
    internal sealed class StageEditHistory<T>
    {
        private readonly List<T> _states = new List<T>();
        public int Count => _states.Count;
        public T this[int index] { get => _states[index]; set => _states[index] = value; }

        /// <summary>이력에 스냅샷을 추가한다.</summary>
        public void Add(T state) => _states.Add(state);

        /// <summary>모든 이력을 비운다.</summary>
        public void Clear() => _states.Clear();

        /// <summary>스냅샷의 위치를 반환한다.</summary>
        public int IndexOf(T state) => _states.IndexOf(state);

        /// <summary>지정한 범위의 이력을 제거한다.</summary>
        public void RemoveRange(int index, int count) => _states.RemoveRange(index, count);

        /// <summary>현재 상태 저장 방식에 맞춰 되돌릴 스냅샷을 반환하고 이력을 갱신한다.</summary>
        public bool TryUndo(bool includesCurrentState, out T state)
        {
            state = default;
            if (_states.Count <= (includesCurrentState ? 1 : 0)) return false;
            if (includesCurrentState) _states.RemoveAt(_states.Count - 1);
            state = _states[_states.Count - 1];
            if (!includesCurrentState) _states.RemoveAt(_states.Count - 1);
            return true;
        }
    }

    /// <summary>배치 오브젝트를 지정한 자세로 생성하고 물리 위치를 동기화한다.</summary>
    internal static class StagePlacement
    {
        /// <summary>배치 미리보기를 지정한 위치와 회전에 표시한다.</summary>
        public static void ShowPreview(GameObject preview, Vector3 position, Quaternion rotation)
        {
            preview.transform.SetPositionAndRotation(position, rotation);
            if (!preview.activeSelf) preview.SetActive(true);
        }

        /// <summary>배치 미리보기를 숨긴다.</summary>
        public static void HidePreview(GameObject preview)
        {
            if (preview != null && preview.activeSelf) preview.SetActive(false);
        }

        /// <summary>프리팹을 미리보기 위치와 회전으로 생성한다.</summary>
        public static GameObject CreatePrefab(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            return UnityEngine.Object.Instantiate(prefab, position, rotation);
        }

        /// <summary>기본 큐브를 부모 아래의 지정한 위치와 회전으로 생성한다.</summary>
        public static GameObject CreateCube(Transform parent, Vector3 position, Quaternion rotation)
        {
            GameObject instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            instance.transform.SetParent(parent, false);
            instance.transform.SetPositionAndRotation(position, rotation);
            return instance;
        }

        /// <summary>생성된 오브젝트의 위치와 콜라이더를 다음 배치 판정에 반영한다.</summary>
        public static void SyncPhysics() => Physics.SyncTransforms();
    }

    /// <summary>스테이지 접합의 공통 FixedJoint 설정을 생성한다.</summary>
    internal static class StageJoint
    {
        /// <summary>로컬 연결점, 파손 한계와 충돌 설정을 적용한 조인트를 반환한다.</summary>
        public static FixedJoint Create(GameObject owner, Rigidbody connectedBody, Vector3 anchor,
            Vector3 connectedAnchor, float breakForce, float breakTorque, bool enableCollision)
        {
            FixedJoint joint = owner.AddComponent<FixedJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody = connectedBody;
            joint.anchor = anchor;
            joint.connectedAnchor = connectedAnchor;
            joint.breakForce = breakForce;
            joint.breakTorque = breakTorque;
            joint.enableCollision = enableCollision;
            return joint;
        }
    }

    /// <summary>
    /// Records the projected footprints of pieces placed in one left-button stroke.
    /// Computes the first clear point along a mouse segment using swept 2D SAT,
    /// rather than a timer or an arbitrary center-to-center spacing threshold.
    /// </summary>
    internal sealed class GuardWavePlacementFootprint
    {
        private const float Clearance = 0.005f;
        private const float ContactStep = 0.001f;
        private readonly Vector2[] _localHull;
        private readonly List<Vector2[]> _placed = new List<Vector2[]>();
        private readonly List<BlockedInterval> _blocked = new List<BlockedInterval>();

        private readonly struct BlockedInterval
        {
            public readonly float Start;
            public readonly float End;

            public BlockedInterval(float start, float end)
            {
                Start = start;
                End = end;
            }
        }

        private GuardWavePlacementFootprint(Vector2[] localHull)
        {
            _localHull = localHull;
        }

        public static GuardWavePlacementFootprint FromPrefab(GameObject prefab)
        {
            List<Vector2> points = new List<Vector2>();
            Transform root = prefab.transform;
            foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled || collider.isTrigger) continue;
                if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
                {
                    AddMesh(meshCollider.sharedMesh, collider.transform, root, points);
                }
                else if (collider is BoxCollider box)
                {
                    AddBounds(new Bounds(box.center, box.size), box.transform, root, points);
                }
                else if (collider is SphereCollider sphere)
                {
                    AddBounds(new Bounds(sphere.center,
                        Vector3.one * (sphere.radius * 2f)), sphere.transform, root, points);
                }
                else if (collider is CapsuleCollider capsule)
                {
                    Vector3 size = Vector3.one * (capsule.radius * 2f);
                    size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
                    AddBounds(new Bounds(capsule.center, size), capsule.transform, root, points);
                }
            }

            // A non-physics decorative prefab can still be placed at sensible intervals.
            if (points.Count == 0)
                foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null)
                        AddMesh(filter.sharedMesh, filter.transform, root, points);

            if (points.Count == 0)
            {
                points.Add(new Vector2(-0.25f, -0.25f));
                points.Add(new Vector2(0.25f, 0.25f));
            }

            // SAT requires a non-degenerate polygon, including for flat meshes.
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (Vector2 point in points)
            {
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minZ = Mathf.Min(minZ, point.y);
                maxZ = Mathf.Max(maxZ, point.y);
            }
            if (maxX - minX < 0.02f || maxZ - minZ < 0.02f)
            {
                float cx = (minX + maxX) * 0.5f;
                float cz = (minZ + maxZ) * 0.5f;
                float hx = Mathf.Max(0.01f, (maxX - minX) * 0.5f);
                float hz = Mathf.Max(0.01f, (maxZ - minZ) * 0.5f);
                points.Add(new Vector2(cx - hx, cz - hz));
                points.Add(new Vector2(cx - hx, cz + hz));
                points.Add(new Vector2(cx + hx, cz - hz));
                points.Add(new Vector2(cx + hx, cz + hz));
            }

            Vector2[] hull = ConvexHull(points);
            if (hull.Length < 3)
            {
                // Also handle long, perfectly collinear projected meshes.
                points.Add(new Vector2(minX - 0.01f, minZ - 0.01f));
                points.Add(new Vector2(minX - 0.01f, maxZ + 0.01f));
                points.Add(new Vector2(maxX + 0.01f, minZ - 0.01f));
                points.Add(new Vector2(maxX + 0.01f, maxZ + 0.01f));
                hull = ConvexHull(points);
            }
            return new GuardWavePlacementFootprint(hull);
        }

        private static void AddMesh(Mesh mesh, Transform transform, Transform root,
            List<Vector2> points)
        {
            if (mesh.isReadable)
            {
                foreach (Vector3 vertex in mesh.vertices)
                    AddPoint(vertex, transform, root, points);
            }
            else
            {
                // Imported Unity meshes may have Read/Write disabled.
                // Their oriented local bounds conservatively avoid visual overlaps.
                AddBounds(mesh.bounds, transform, root, points);
            }
        }

        private static void AddBounds(Bounds bounds, Transform transform, Transform root,
            List<Vector2> points)
        {
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        AddPoint(bounds.center + Vector3.Scale(bounds.extents,
                            new Vector3(x, y, z)), transform, root, points);
        }

        private static void AddPoint(Vector3 point, Transform source, Transform root,
            List<Vector2> points)
        {
            // Instantiation overrides the root position/yaw but keeps its scale.
            Vector3 local = root.InverseTransformPoint(source.TransformPoint(point));
            local = Vector3.Scale(local, root.localScale);
            points.Add(new Vector2(local.x, local.z));
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 ab = b - a;
            Vector2 ac = c - a;
            return ab.x * ac.y - ab.y * ac.x;
        }

        private static Vector2[] ConvexHull(List<Vector2> points)
        {
            points.Sort((a, b) =>
            {
                int x = a.x.CompareTo(b.x);
                return x != 0 ? x : a.y.CompareTo(b.y);
            });
            List<Vector2> hull = new List<Vector2>();
            foreach (Vector2 p in points)
            {
                while (hull.Count >= 2 &&
                    Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lowerCount = hull.Count;
            for (int i = points.Count - 2; i >= 0; i--)
            {
                Vector2 p = points[i];
                while (hull.Count > lowerCount &&
                    Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
            return hull.ToArray();
        }

        private Vector2[] At(Vector2 center, float yaw)
        {
            float rad = yaw * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            Vector2[] shape = new Vector2[_localHull.Length];
            for (int i = 0; i < shape.Length; i++)
            {
                Vector2 p = _localHull[i];
                shape[i] = center + new Vector2(c * p.x + s * p.y,
                    -s * p.x + c * p.y);
            }
            return shape;
        }

        public void Record(Vector2 center, float yaw)
        {
            _placed.Add(At(center, yaw));
        }

        public bool Overlaps(Vector2 center, float yaw)
        {
            Vector2[] candidate = At(center, yaw);
            foreach (Vector2[] previous in _placed)
                if (IntersectDuringTravel(candidate, previous, Vector2.zero,
                    out _, out _)) return true;
            return false;
        }

        /// <summary>
        /// Returns the earliest free point on the whole XZ cursor segment.
        /// Each existing placement blocks a continuous interval along that ray;
        /// their union is skipped without imposing an extra width-based delay.
        /// </summary>
        public bool TryFindFirstFree(Vector2 from, Vector2 to, float yaw,
            out Vector2 point)
        {
            point = default;
            Vector2 travel = to - from;
            float length = travel.magnitude;
            if (length < 0.0001f) return false;

            Vector2[] moving = At(from, yaw);
            _blocked.Clear();
            foreach (Vector2[] previous in _placed)
                if (IntersectDuringTravel(moving, previous, travel,
                    out float first, out float last))
                    _blocked.Add(new BlockedInterval(first, last));

            _blocked.Sort((a, b) => a.Start.CompareTo(b.Start));
            float t = 0f;
            float advance = ContactStep / length;
            foreach (BlockedInterval interval in _blocked)
            {
                if (interval.Start > t) break;
                t = Mathf.Max(t, interval.End + advance);
                if (t > 1f) return false;
            }

            // Contact tolerance handles floating-point and mesh-bound edge cases.
            for (int retry = 0; retry < 4 && t <= 1f; retry++, t += advance)
            {
                Vector2 candidate = Vector2.LerpUnclamped(from, to, t);
                if (!Overlaps(candidate, yaw))
                {
                    point = candidate;
                    return true;
                }
            }
            return false;
        }

        private static bool IntersectDuringTravel(Vector2[] moving, Vector2[] fixedShape,
            Vector2 delta, out float first, out float last)
        {
            first = 0f;
            last = 1f;
            return ClipAxes(moving, fixedShape, delta, moving, ref first, ref last)
                && ClipAxes(moving, fixedShape, delta, fixedShape, ref first, ref last);
        }

        private static bool ClipAxes(Vector2[] moving, Vector2[] fixedShape,
            Vector2 delta, Vector2[] edges, ref float first, ref float last)
        {
            for (int i = 0; i < edges.Length; i++)
            {
                Vector2 edge = edges[(i + 1) % edges.Length] - edges[i];
                Vector2 axis = new Vector2(-edge.y, edge.x);
                if (axis.sqrMagnitude < 1e-12f) continue;
                axis.Normalize();
                Project(moving, axis, out float minMoving, out float maxMoving);
                Project(fixedShape, axis, out float minFixed, out float maxFixed);
                float speed = Vector2.Dot(delta, axis);
                if (Mathf.Abs(speed) < 1e-7f)
                {
                    if (maxMoving < minFixed - Clearance ||
                        minMoving > maxFixed + Clearance)
                        return false;
                    continue;
                }

                float start = (minFixed - Clearance - maxMoving) / speed;
                float end = (maxFixed + Clearance - minMoving) / speed;
                if (start > end)
                {
                    float temp = start;
                    start = end;
                    end = temp;
                }
                first = Mathf.Max(first, start);
                last = Mathf.Min(last, end);
                if (first > last) return false;
            }
            return true;
        }

        private static void Project(Vector2[] polygon, Vector2 axis,
            out float min, out float max)
        {
            min = max = Vector2.Dot(polygon[0], axis);
            for (int i = 1; i < polygon.Length; i++)
            {
                float p = Vector2.Dot(polygon[i], axis);
                min = Mathf.Min(min, p);
                max = Mathf.Max(max, p);
            }
        }
    }
}
