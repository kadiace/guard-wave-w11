using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// One continuous brush stroke of adhesive mud. A slightly raised mesh
    /// forms an uneven wet ribbon, while trigger samples along that ribbon
    /// attach falling Rigidbodies to the painted surface via FixedJoint.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveMudPatch : MonoBehaviour
    {
        public const float BrushRadius = 0.46f;
        public const float SampleSpacing = 0.16f;

        private const int MaxSamples = 512;
        private const float ColliderSpacing = 0.5f;
        private const float SurfaceOffset = 0.018f;
        private const int CapSides = 12;

        private readonly List<Vector3> _points = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly Dictionary<GuardWavePiece, FixedJoint> _bonds =
            new Dictionary<GuardWavePiece, FixedJoint>();
        private readonly Dictionary<Collider, float> _lastContactCheck =
            new Dictionary<Collider, float>();

        private GuardWaveGame _game;
        private Collider _surfaceCollider;
        private Rigidbody _surfaceBody;
        private float _breakForce;
        private float _breakTorque;
        private Vector3 _lastTriggerPosition;
        private Material _material;
        private Mesh _mesh;
        private bool _meshDirty;

        public Collider SurfaceCollider => _surfaceCollider;
        public bool IsFull => _points.Count >= MaxSamples;

        public readonly struct MudBondState
        {
            public readonly GuardWaveMudPatch Patch;
            public readonly GuardWavePiece Piece;
            public readonly Vector3 Anchor;
            public readonly Vector3 ConnectedAnchor;

            public MudBondState(GuardWaveMudPatch patch, GuardWavePiece piece, FixedJoint joint)
            {
                Patch = patch;
                Piece = piece;
                Anchor = joint.anchor;
                ConnectedAnchor = joint.connectedAnchor;
            }
        }

        public void Initialize(GuardWaveGame game, Collider surfaceCollider,
            Rigidbody surfaceBody, float breakForce, float breakTorque)
        {
            _game = game;
            _surfaceCollider = surfaceCollider;
            _surfaceBody = surfaceBody;
            _breakForce = breakForce;
            _breakTorque = breakTorque;

            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "Wet Mud Brush Stroke" };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;
            _material = CreateMudMaterial(false);
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>Appends a brush sample in the parented surface's local space.</summary>
        public void AddPoint(Vector3 worldPoint, Vector3 worldNormal)
        {
            if (IsFull) return;
            Vector3 point = transform.InverseTransformPoint(worldPoint);
            Vector3 normal = transform.InverseTransformDirection(worldNormal).normalized;
            if (_points.Count > 0 &&
                Vector3.Distance(_points[_points.Count - 1], point) < SampleSpacing * 0.65f)
                return;

            _points.Add(point);
            _normals.Add(normal);
            _meshDirty = true;

            if (_points.Count == 1 ||
                Vector3.Distance(_lastTriggerPosition, point) >= ColliderSpacing)
            {
                SphereCollider trigger = gameObject.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = BrushRadius;
                trigger.center = point + normal * 0.24f;
                _lastTriggerPosition = point;
            }
        }

        /// <summary>Rebuild only once per rendered frame, not for every interpolated point.</summary>
        private void LateUpdate()
        {
            if (!_meshDirty) return;
            _meshDirty = false;
            RebuildStrokeMesh();
        }

        private void RebuildStrokeMesh()
        {
            if (_mesh == null || _points.Count == 0) return;

            int count = _points.Count;
            var vertices = new List<Vector3>(count * 3 + CapSides * 4);
            var triangles = new List<int>(count * 12 + CapSides * 6);
            var uv = new List<Vector2>(count * 3 + CapSides * 4);

            float traveled = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 normal = _normals[i];
                Vector3 tangent = TangentAt(i);
                Vector3 side = Vector3.Cross(normal, tangent).normalized;
                float noise = Mathf.Sin(i * 2.31f + 0.7f) * 0.045f
                    + Mathf.Sin(i * 0.91f) * 0.045f;
                float width = BrushRadius * (0.91f + noise);
                Vector3 center = _points[i] + normal * (SurfaceOffset + 0.019f);
                Vector3 left = _points[i] - side * width + normal * SurfaceOffset;
                Vector3 right = _points[i] + side * width + normal * SurfaceOffset;
                if (i > 0) traveled += Vector3.Distance(_points[i - 1], _points[i]);
                vertices.Add(left);
                vertices.Add(center);
                vertices.Add(right);
                uv.Add(new Vector2(0f, traveled));
                uv.Add(new Vector2(0.5f, traveled));
                uv.Add(new Vector2(1f, traveled));

                if (i == 0) continue;
                int a = (i - 1) * 3;
                int b = i * 3;
                // Winding points upward along the surface, with a narrow
                // raised center ridge to create a fresh, wet brush highlight.
                triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                triangles.Add(a + 1); triangles.Add(b + 1); triangles.Add(a + 2);
                triangles.Add(a + 2); triangles.Add(b + 1); triangles.Add(b + 2);
            }

            AddRoundedEnd(0, vertices, uv, triangles);
            if (count > 1) AddRoundedEnd(count - 1, vertices, uv, triangles);

            _mesh.Clear();
            _mesh.SetVertices(vertices);
            _mesh.SetTriangles(triangles, 0);
            _mesh.SetUVs(0, uv);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }

        private Vector3 TangentAt(int index)
        {
            Vector3 delta = _points.Count == 1
                ? Vector3.forward
                : index == 0
                    ? _points[1] - _points[0]
                    : index == _points.Count - 1
                        ? _points[index] - _points[index - 1]
                        : _points[index + 1] - _points[index - 1];
            delta = Vector3.ProjectOnPlane(delta, _normals[index]);
            if (delta.sqrMagnitude < 0.0001f)
                delta = Vector3.ProjectOnPlane(Vector3.forward, _normals[index]);
            if (delta.sqrMagnitude < 0.0001f)
                delta = Vector3.ProjectOnPlane(Vector3.right, _normals[index]);
            return delta.normalized;
        }

        private void AddRoundedEnd(int pointIndex, List<Vector3> vertices,
            List<Vector2> uv, List<int> triangles)
        {
            Vector3 normal = _normals[pointIndex];
            Vector3 forward = TangentAt(pointIndex);
            Vector3 side = Vector3.Cross(normal, forward).normalized;
            Vector3 center = _points[pointIndex] + normal * (SurfaceOffset + 0.018f);
            int centerIndex = vertices.Count;
            vertices.Add(center);
            uv.Add(new Vector2(0.5f, 0.5f));
            for (int i = 0; i < CapSides; i++)
            {
                float angle = i * Mathf.PI * 2f / CapSides;
                float radius = BrushRadius * (0.89f + 0.045f * Mathf.Sin(i * 3.23f + pointIndex));
                Vector3 radial = side * Mathf.Cos(angle) + forward * Mathf.Sin(angle);
                vertices.Add(_points[pointIndex] + radial * radius + normal * SurfaceOffset);
                uv.Add(new Vector2(0.5f + Mathf.Cos(angle) * 0.5f,
                    0.5f + Mathf.Sin(angle) * 0.5f));
            }
            for (int i = 0; i < CapSides; i++)
            {
                triangles.Add(centerIndex);
                triangles.Add(centerIndex + 1 + (i + 1) % CapSides);
                triangles.Add(centerIndex + 1 + i);
            }
        }

        public static GameObject CreatePreview()
        {
            GameObject preview = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            preview.name = "Mud Brush Cursor";
            preview.transform.localScale = new Vector3(
                BrushRadius * 2f, 0.008f, BrushRadius * 2f);
            Collider collider = preview.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            preview.GetComponent<Renderer>().sharedMaterial = CreateMudMaterial(true);
            return preview;
        }

        private static Material CreateMudMaterial(bool preview)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader);
            material.name = preview ? "Wet Glue Brush Cursor" : "Wet Adhesive Mud";
            Color tint = preview
                ? new Color(0.78f, 0.60f, 0.33f, 1f)
                : new Color(0.51f, 0.35f, 0.19f, 1f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", preview ? 0.55f : 0.79f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            return material;
        }

        private void OnTriggerEnter(Collider other) => TryBond(other);
        private void OnTriggerStay(Collider other) => TryBond(other);

        private void TryBond(Collider other)
        {
            if (_game == null) return;
            GuardWavePiece piece = other.GetComponentInParent<GuardWavePiece>();
            if (piece == null || !_game.IsPlacedPiece(piece) ||
                piece.Body == _surfaceBody || piece.Body.isKinematic ||
                _game.HasMudBond(piece))
                return;

            // A stroke has multiple overlapping trigger segments. Scan the
            // actual paint only once per collider per physics step, not once
            // per trigger, even on a long stroke.
            if (_lastContactCheck.TryGetValue(other, out float checkedAt) &&
                checkedAt == Time.fixedTime)
                return;
            _lastContactCheck[other] = Time.fixedTime;

            // Attach only when the actual object boundary reaches the paint,
            // never while the object is still falling through the trigger volume.
            float nearestDistance = float.PositiveInfinity;
            Vector3 anchorPoint = default;
            for (int i = 0; i < _points.Count; i++)
            {
                Vector3 worldPoint = transform.TransformPoint(_points[i]);
                Vector3 worldNormal = transform.TransformDirection(_normals[i]);
                Vector3 closest = other.ClosestPoint(worldPoint);
                Vector3 difference = closest - worldPoint;
                float height = Vector3.Dot(difference, worldNormal);
                float lateral = Vector3.ProjectOnPlane(difference, worldNormal).magnitude;
                float distance = difference.sqrMagnitude;

                if (height < -0.16f || height > 0.085f ||
                    lateral > BrushRadius * 1.05f ||
                    Vector3.Dot(piece.Body.worldCenterOfMass - worldPoint, worldNormal) < -0.12f ||
                    Vector3.Dot(piece.Body.linearVelocity, worldNormal) > 0.2f ||
                    distance >= nearestDistance)
                    continue;

                nearestDistance = distance;
                anchorPoint = worldPoint;
            }

            if (float.IsPositiveInfinity(nearestDistance)) return;
            Vector3 anchor = piece.transform.InverseTransformPoint(anchorPoint);
            Vector3 connectedAnchor = _surfaceBody == null ? anchorPoint
                : _surfaceBody.transform.InverseTransformPoint(anchorPoint);
            CreateBond(piece, anchor, connectedAnchor);
        }

        private void CreateBond(GuardWavePiece piece, Vector3 anchor,
            Vector3 connectedAnchor)
        {
            if (piece == null || HasBond(piece)) return;
            FixedJoint joint = piece.gameObject.AddComponent<FixedJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody = _surfaceBody;
            joint.anchor = anchor;
            joint.connectedAnchor = connectedAnchor;
            joint.breakForce = _breakForce;
            joint.breakTorque = _breakTorque;
            joint.enableCollision = false;
            _bonds[piece] = joint;
        }

        public bool HasBond(GuardWavePiece piece)
        {
            if (piece == null || !_bonds.TryGetValue(piece, out FixedJoint joint))
                return false;
            if (joint != null) return true;
            _bonds.Remove(piece);
            return false;
        }

        public void DetachPiece(GuardWavePiece piece)
        {
            if (piece == null) return;
            if (_surfaceBody == piece.Body)
            {
                ClearBonds();
                return;
            }
            if (!_bonds.TryGetValue(piece, out FixedJoint joint)) return;
            if (joint != null) Destroy(joint);
            _bonds.Remove(piece);
        }

        public void CaptureBonds(List<MudBondState> destination)
        {
            foreach (KeyValuePair<GuardWavePiece, FixedJoint> bond in _bonds)
                if (bond.Key != null && bond.Value != null)
                    destination.Add(new MudBondState(this, bond.Key, bond.Value));
        }

        public void ClearBonds()
        {
            foreach (FixedJoint joint in _bonds.Values)
                if (joint != null) Destroy(joint);
            _bonds.Clear();
            _lastContactCheck.Clear();
        }

        public void RestoreBond(MudBondState state)
        {
            if (state.Patch != this || state.Piece == null ||
                !_game.IsPlacedPiece(state.Piece)) return;
            CreateBond(state.Piece, state.Anchor, state.ConnectedAnchor);
        }

        private void OnDestroy()
        {
            ClearBonds();
            if (_material != null) Destroy(_material);
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
