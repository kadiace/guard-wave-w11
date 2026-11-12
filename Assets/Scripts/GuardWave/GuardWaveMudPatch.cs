using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// One continuous, thick impasto-like adhesive paint stroke.
    /// Tapered ends, asymmetric raised ridges and lengthwise pigmentation
    /// replace circular stamps. Physics adhesion remains on the painted surface.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveMudPatch : MonoBehaviour
    {
        public const float BrushRadius = 0.46f;
        public const float SampleSpacing = 0.16f;

        private const int MaxSamples = 512;
        // Runtime paint uses long, simplified trigger capsules; authored paint keeps its few existing spheres.
        private const float MaxTriggerSegmentLength = 2.4f;
        private const float MaxTriggerDeviation = 0.12f;
        private const float MinTriggerNormalDot = 0.9f;
        private const float SampleCellSize = 0.6f;
        private const float MeshRefreshInterval = 0.065f;
        private const float SurfaceOffset = 0.012f;
        private const int CrossSections = 9;
        private static Material s_mudMaterial;

        private readonly List<Vector3> _points = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly Dictionary<GuardWavePiece, FixedJoint> _bonds =
            new Dictionary<GuardWavePiece, FixedJoint>();
        private readonly Dictionary<Collider, float> _lastContactCheck =
            new Dictionary<Collider, float>();
        private readonly Dictionary<Vector2Int, List<int>> _sampleGrid =
            new Dictionary<Vector2Int, List<int>>();
        // Reuse mesh buffers: a long stroke should not allocate three large lists every update.
        private readonly List<Vector3> _meshVertices = new List<Vector3>(256);
        private readonly List<int> _meshTriangles = new List<int>(512);
        private readonly List<Vector2> _meshUvs = new List<Vector2>(256);
        private readonly float[] _lengths = new float[MaxSamples];

        private GuardWaveGame _game;
        private Collider _surfaceCollider;
        private Rigidbody _surfaceBody;
        private float _breakForce;
        private float _breakTorque;
        private int _triggerSegmentFirstSample;
        private GameObject _triggerSegment;
        private CapsuleCollider _triggerCapsule;
        private float _lastMeshRefresh = float.NegativeInfinity;
        private Mesh _mesh;
        private bool _meshDirty;
        private bool _authored;

        public Collider SurfaceCollider => _surfaceCollider;
        public bool IsFull => _points.Count >= MaxSamples;

        /// <summary>
        /// Only connect samples on the same nearby plane. Splitting on a sharp
        /// normal change prevents paint from bridging between a log's top
        /// and the creek floor, or across two faces of the same mesh.
        /// </summary>
        public bool CanContinueAt(Vector3 worldPoint, Vector3 worldNormal)
        {
            if (_points.Count == 0) return true;
            int last = _points.Count - 1;
            Vector3 previousPoint = transform.TransformPoint(_points[last]);
            Vector3 previousNormal =
                transform.TransformDirection(_normals[last]).normalized;
            // Faceted logs have abruptly changing normals. Keep one painted
            // smear through gentle bends, but still split at true surface breaks.
            return Vector3.Distance(previousPoint, worldPoint) <= BrushRadius * 2.4f &&
                   Vector3.Dot(previousNormal, worldNormal.normalized) > 0.45f;
        }

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
            renderer.sharedMaterial = MudMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>씬에 배치된 진흙의 트리거 중심을 접착 표면 샘플로 등록한다.</summary>
        public void InitializeAuthored(GuardWaveGame game, Collider surfaceCollider,
            Rigidbody surfaceBody, float breakForce, float breakTorque)
        {
            _game = game;
            _surfaceCollider = surfaceCollider;
            _surfaceBody = surfaceBody;
            _breakForce = breakForce;
            _breakTorque = breakTorque;
            _authored = true;

            foreach (SphereCollider trigger in GetComponents<SphereCollider>())
            {
                Vector3 point = trigger.center - Vector3.up * 0.24f;
                int index = _points.Count;
                _points.Add(point);
                _normals.Add(Vector3.up);
                IndexSample(point, index);
            }
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

            int index = _points.Count;
            _points.Add(point);
            _normals.Add(normal);
            IndexSample(point, index);
            _meshDirty = true;
            UpdateTriggerSegment(index);
        }

        /// <summary>Throttle full mesh uploads while painting; always flush the final brush position.</summary>
        private void LateUpdate()
        {
            if (!_meshDirty || Time.unscaledTime - _lastMeshRefresh < MeshRefreshInterval)
                return;
            FlushStrokeMesh();
        }

        public void FlushStrokeMesh()
        {
            if (!_meshDirty) return;
            _meshDirty = false;
            _lastMeshRefresh = Time.unscaledTime;
            RebuildStrokeMesh();
        }

        /// <summary>
        /// Build one continuous raised paint ribbon: nine cross-stroke samples
        /// for brush ridges, irregular edges and four tapered end rows. The
        /// surface normals follow the hit collider rather than a flat plane.
        /// Nothing is instanced per sample, so no stacked-circle appearance.
        /// </summary>
        private void RebuildStrokeMesh()
        {
            if (_mesh == null || _points.Count == 0) return;

            int count = _points.Count;
            int rows = count + 4;
            _meshVertices.Clear();
            _meshTriangles.Clear();
            _meshUvs.Clear();
            List<Vector3> vertices = _meshVertices;
            List<int> triangles = _meshTriangles;
            List<Vector2> uv = _meshUvs;
            float[] lengths = _lengths;
            lengths[0] = 0f;
            for (int i = 1; i < count; i++)
                lengths[i] = lengths[i - 1] +
                    Vector3.Distance(_points[i - 1], _points[i]);

            for (int row = 0; row < rows; row++)
            {
                int index = Mathf.Clamp(row - 2, 0, count - 1);
                Vector3 normal = _normals[index];
                Vector3 tangent = TangentAt(index);
                Vector3 side = Vector3.Cross(normal, tangent).normalized;

                // The brush picks up its deposited paint at the start and
                // releases it at the tail, rather than making round end caps.
                float offset = 0f;
                float taper = 1f;
                if (row == 0) { offset = -BrushRadius * 0.48f; taper = 0.06f; }
                else if (row == 1) { offset = -BrushRadius * 0.22f; taper = 0.68f; }
                else if (row == rows - 2)
                {
                    offset = BrushRadius * 0.22f;
                    taper = 0.72f;
                }
                else if (row == rows - 1)
                {
                    offset = BrushRadius * 0.47f;
                    taper = 0.07f;
                }

                float distance = lengths[index] + offset;
                float variation = Mathf.PerlinNoise(distance * 1.9f + 2.1f, 1.71f);
                float width = BrushRadius * taper *
                    Mathf.Lerp(0.80f, 1.14f, variation);

                Vector3 center = _points[index] + tangent * offset;
                for (int j = 0; j < CrossSections; j++)
                {
                    float x = (j / (float)(CrossSections - 1)) * 2f - 1f;
                    float edgeNoise = Mathf.Sin(distance * 8.7f + x * 3.2f) *
                        Mathf.Sin(distance * 3.4f + 1.9f) * 0.047f;
                    float lateral = x * width + x * edgeNoise * taper;

                    // Broad, thick body with a pair of longitudinal knife/
                    // bristle ridges. Height varies with deposited paint flow,
                    // but falls to a thin feathered film at both edges.
                    float core = Mathf.Pow(Mathf.Max(0f, 1f - x * x), 1.3f);
                    float ridgeA = Mathf.Exp(-Mathf.Pow((x + 0.36f) * 6.2f, 2f));
                    float ridgeB = Mathf.Exp(-Mathf.Pow((x - 0.28f) * 6.5f, 2f));
                    float ridgeNoise = Mathf.PerlinNoise(
                        distance * 4.1f + 0.7f, x * 2.6f + 3.1f);
                    float thickness = core *
                        (0.052f + ridgeA * 0.057f + ridgeB * 0.036f +
                         ridgeNoise * 0.027f) *
                        Mathf.Lerp(0.26f, 1f, taper);
                    float edgeFilm = Mathf.Lerp(0.002f, 0.008f, core);

                    vertices.Add(center + side * lateral +
                        normal * (SurfaceOffset + edgeFilm + thickness));
                    uv.Add(new Vector2(j / (float)(CrossSections - 1),
                        distance / (BrushRadius * 1.15f)));
                }

                if (row == 0) continue;
                for (int j = 0; j < CrossSections - 1; j++)
                {
                    int a = (row - 1) * CrossSections + j;
                    int b = row * CrossSections + j;
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(a + 1);
                    triangles.Add(a + 1);
                    triangles.Add(b);
                    triangles.Add(b + 1);
                }
            }

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

        public static GameObject CreatePreview()
        {
            // A tiny tapered paint swipe, not a flat circular cursor stamp.
            // All parts are render-only to avoid transient inspector targets.
            GameObject preview = new GameObject("Impasto Brush Cursor");
            MeshFilter filter = preview.AddComponent<MeshFilter>();
            MeshRenderer renderer = preview.AddComponent<MeshRenderer>();
            const int rows = 5;
            const int across = 9;
            float[] lengths = { -0.43f, -0.23f, 0f, 0.21f, 0.44f };
            float[] widths = { 0.07f, 0.73f, 1f, 0.78f, 0.07f };
            Vector3[] vertices = new Vector3[rows * across];
            int[] triangles = new int[(rows - 1) * (across - 1) * 6];
            Vector2[] uv = new Vector2[vertices.Length];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < across; c++)
                {
                    float x = c / (float)(across - 1) * 2f - 1f;
                    float body = Mathf.Max(0f, 1f - x * x);
                    float ridge = Mathf.Exp(-Mathf.Pow((x + 0.32f) * 5.3f, 2f));
                    float height = (body * (0.040f + ridge * 0.045f)) *
                        Mathf.Lerp(0.25f, 1f, widths[r]);
                    vertices[r * across + c] = new Vector3(
                        x * BrushRadius * widths[r],
                        height + 0.005f, lengths[r]);
                    uv[r * across + c] = new Vector2(
                        c / (float)(across - 1), lengths[r] / BrushRadius);
                    if (r == 0 || c == across - 1) continue;
                    int a = (r - 1) * across + c;
                    int b = r * across + c;
                    int at = ((r - 1) * (across - 1) + c) * 6;
                    triangles[at] = a;
                    triangles[at + 1] = b;
                    triangles[at + 2] = a + 1;
                    triangles[at + 3] = a + 1;
                    triangles[at + 4] = b;
                    triangles[at + 5] = b + 1;
                }
            }
            Mesh mesh = new Mesh { name = "Impasto Brush Tip" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uv;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = MudMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            preview.hideFlags = HideFlags.HideInHierarchy;
            return preview;
        }

        private static Material MudMaterial => s_mudMaterial != null
            ? s_mudMaterial
            : s_mudMaterial = Resources.Load<Material>("Materials/Mud");

        // Index brush samples on the paint's local tangent plane. A collision queries only
        // cells overlapped by the body's AABB, avoiding ClosestPoint on every stroke sample.
        private void IndexSample(Vector3 point, int index)
        {
            Vector2Int key = new Vector2Int(
                Mathf.FloorToInt(point.x / SampleCellSize),
                Mathf.FloorToInt(point.z / SampleCellSize));
            if (!_sampleGrid.TryGetValue(key, out List<int> bucket))
            {
                bucket = new List<int>(4);
                _sampleGrid.Add(key, bucket);
            }
            bucket.Add(index);
        }

        private Vector3 TriggerCenter(int index) =>
            _points[index] + _normals[index] * 0.24f;

        private void UpdateTriggerSegment(int sampleIndex)
        {
            if (_triggerCapsule == null)
                CreateTriggerSegment(sampleIndex);

            Vector3 start = TriggerCenter(_triggerSegmentFirstSample);
            Vector3 end = TriggerCenter(sampleIndex);
            Vector3 axis = end - start;
            bool canExtend = axis.magnitude <= MaxTriggerSegmentLength;
            if (canExtend && axis.sqrMagnitude > 0.000001f)
            {
                for (int i = _triggerSegmentFirstSample; i <= sampleIndex; i++)
                {
                    Vector3 current = TriggerCenter(i);
                    float t = Mathf.Clamp01(Vector3.Dot(current - start, axis) /
                        axis.sqrMagnitude);
                    if (Vector3.Distance(current, start + axis * t) > MaxTriggerDeviation ||
                        Vector3.Dot(_normals[i], _normals[_triggerSegmentFirstSample]) <
                        MinTriggerNormalDot)
                    {
                        canExtend = false;
                        break;
                    }
                }
            }
            if (!canExtend)
            {
                // Adjacent segments share an endpoint, leaving no unpainted collision gap.
                CreateTriggerSegment(Mathf.Max(0, sampleIndex - 1));
                start = TriggerCenter(_triggerSegmentFirstSample);
                end = TriggerCenter(sampleIndex);
                axis = end - start;
            }
            _triggerSegment.transform.localPosition = (start + end) * 0.5f;
            _triggerSegment.transform.localRotation = axis.sqrMagnitude < 0.000001f
                ? Quaternion.identity
                : Quaternion.FromToRotation(Vector3.up, axis.normalized);
            _triggerCapsule.height = axis.magnitude + BrushRadius * 2f;
        }

        private void CreateTriggerSegment(int firstSample)
        {
            _triggerSegmentFirstSample = firstSample;
            _triggerSegment = new GameObject("Mud Trigger Segment");
            _triggerSegment.transform.SetParent(transform, false);
            _triggerCapsule = _triggerSegment.AddComponent<CapsuleCollider>();
            _triggerCapsule.isTrigger = true;
            _triggerCapsule.radius = BrushRadius;
            _triggerCapsule.direction = 1;
            _triggerSegment.AddComponent<GuardWaveMudTriggerRelay>().Initialize(this);
        }

        private void OnTriggerEnter(Collider other) => TryBond(other);
        private void OnTriggerStay(Collider other) => TryBond(other);
        internal void ProcessTrigger(Collider other) => TryBond(other);

        private void TryBond(Collider other)
        {
            if (_game == null || other == null) return;
            GuardWavePiece piece = other.GetComponentInParent<GuardWavePiece>();
            if (piece == null || !_game.IsPlacedPiece(piece) ||
                (_authored && _game.IsScenePiece(piece)) ||
                piece.Body == _surfaceBody || piece.Body.isKinematic ||
                _game.HasMudBond(piece) || _game.WasEverMudBonded(piece))
                return;

            if (_lastContactCheck.TryGetValue(other, out float checkedAt) &&
                checkedAt == Time.fixedTime)
                return;
            _lastContactCheck[other] = Time.fixedTime;

            // Convert the expanded collider AABB into the brush's local plane.
            // This also handles strokes attached to moving/rotating logs.
            Bounds bounds = other.bounds;
            Vector3 pad = Vector3.one * (BrushRadius + 0.2f);
            Vector3 low = bounds.min - pad;
            Vector3 high = bounds.max + pad;
            Vector3 minimum = new Vector3(float.PositiveInfinity, 0f, float.PositiveInfinity);
            Vector3 maximum = new Vector3(float.NegativeInfinity, 0f, float.NegativeInfinity);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = new Vector3((corner & 1) == 0 ? low.x : high.x,
                    (corner & 2) == 0 ? low.y : high.y,
                    (corner & 4) == 0 ? low.z : high.z);
                Vector3 local = transform.InverseTransformPoint(world);
                minimum.x = Mathf.Min(minimum.x, local.x);
                minimum.z = Mathf.Min(minimum.z, local.z);
                maximum.x = Mathf.Max(maximum.x, local.x);
                maximum.z = Mathf.Max(maximum.z, local.z);
            }

            float nearestDistance = float.PositiveInfinity;
            Vector3 anchorPoint = default;
            Vector3 centerOfMass = piece.Body.worldCenterOfMass;
            Vector3 velocity = piece.Body.linearVelocity;
            int x0 = Mathf.FloorToInt(minimum.x / SampleCellSize);
            int x1 = Mathf.FloorToInt(maximum.x / SampleCellSize);
            int z0 = Mathf.FloorToInt(minimum.z / SampleCellSize);
            int z1 = Mathf.FloorToInt(maximum.z / SampleCellSize);
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                if (!_sampleGrid.TryGetValue(new Vector2Int(x, z),
                    out List<int> indices)) continue;
                foreach (int index in indices)
                {
                    Vector3 worldPoint = transform.TransformPoint(_points[index]);
                    Vector3 worldNormal =
                        transform.TransformDirection(_normals[index]).normalized;
                    Vector3 closest = other.ClosestPoint(worldPoint);
                    Vector3 difference = closest - worldPoint;
                    float height = Vector3.Dot(difference, worldNormal);
                    float lateral = Vector3.ProjectOnPlane(difference, worldNormal).magnitude;
                    float distance = difference.sqrMagnitude;

                    if (height < -0.16f || height > 0.085f ||
                        lateral > BrushRadius * 1.05f ||
                        Vector3.Dot(centerOfMass - worldPoint, worldNormal) < -0.12f ||
                        Vector3.Dot(velocity, worldNormal) > 0.2f ||
                        distance >= nearestDistance)
                        continue;

                    nearestDistance = distance;
                    anchorPoint = worldPoint;
                }
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
            _game?.RegisterMudBond(piece, joint);
        }

        public bool HasBond(GuardWavePiece piece)
        {
            if (piece == null || !_bonds.TryGetValue(piece, out FixedJoint joint))
                return false;
            if (joint != null) return true;
            _game?.ReleaseMudBond(piece, joint);
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
            _game?.ReleaseMudBond(piece, joint);
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
            foreach (KeyValuePair<GuardWavePiece, FixedJoint> bond in _bonds)
            {
                _game?.ReleaseMudBond(bond.Key, bond.Value);
                if (bond.Value != null) Destroy(bond.Value);
            }
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
            if (_mesh != null) Destroy(_mesh);
        }
    }

    /// <summary>Forwards child capsule trigger events to the single owning mud patch.</summary>
    public sealed class GuardWaveMudTriggerRelay : MonoBehaviour
    {
        private GuardWaveMudPatch _owner;

        public void Initialize(GuardWaveMudPatch owner) => _owner = owner;
        private void OnTriggerEnter(Collider other) => _owner?.ProcessTrigger(other);
        private void OnTriggerStay(Collider other) => _owner?.ProcessTrigger(other);
    }
}
