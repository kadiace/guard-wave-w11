using System.Collections;
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
        // Contact offsets and curved meshes leave a small gap even when the
        // Rigidbody appears to rest on the painted surface.
        private const float MaxBondNormalGap = 0.30f;
        private const float MaxBondInsideSurface = 0.18f;
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
        private readonly List<Collider> _adhesionTriggers = new List<Collider>();
        // Brush-time queries catch pieces already in contact before a new
        // trigger is installed, without performing a whole-scene scan.
        private readonly Collider[] _nearbyColliders = new Collider[64];
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
        private GuardWavePiece _surfacePiece;
        private GuardWaveMudJointSettings _jointSettings;
        private int _triggerSegmentFirstSample;
        private GameObject _triggerSegment;
        private CapsuleCollider _triggerCapsule;
        private float _lastMeshRefresh = float.NegativeInfinity;
        private Mesh _mesh;
        private bool _meshDirty;
        private bool _isDry;
        private Material _instanceMaterial;
        private float _wetSmoothness;
        private float _wetMetallic;

        public Collider SurfaceCollider => _surfaceCollider;
        public bool IsFull => _points.Count >= MaxSamples;
        public bool IsDry => _isDry;

        // Painting on a piece must be indexed even before another body bonds to it.
        private void TrackSurfacePiece()
        {
            if (_surfaceBody == null) return;
            GuardWavePiece piece = _surfaceBody.GetComponent<GuardWavePiece>();
            if (piece != null) _game?.TrackMudPatch(piece, this);
        }

        /// <summary>
        /// Mud is a render mesh over trigger colliders, not a raycastable solid.
        /// Hit-test the actual painted samples against the underlying surface
        /// hit by the cursor, without making the mud obstruct physics rays.
        /// </summary>
        public bool TryGetDryingDistance(Collider collider, Vector3 hitPoint,
            Vector3 hitNormal, out float distanceSqr)
        {
            distanceSqr = float.PositiveInfinity;
            if (_isDry || collider == null || collider != _surfaceCollider ||
                _points.Count == 0) return false;

            for (int i = 0; i < _points.Count; i++)
            {
                Vector3 normal = transform.TransformDirection(_normals[i]).normalized;
                if (Vector3.Dot(normal, hitNormal) < 0.45f) continue;
                Vector3 delta = hitPoint - transform.TransformPoint(_points[i]);
                float normalDistance = Vector3.Dot(delta, normal);
                if (Mathf.Abs(normalDistance) > 0.18f) continue;
                float lateralSqr = (delta - normal * normalDistance).sqrMagnitude;
                if (lateralSqr > BrushRadius * BrushRadius) continue;
                distanceSqr = Mathf.Min(distanceSqr, lateralSqr);
            }
            return !float.IsPositiveInfinity(distanceSqr);
        }




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
            public readonly Rigidbody ConnectedBody;

            public MudBondState(GuardWaveMudPatch patch, GuardWavePiece piece, FixedJoint joint)
            {
                Patch = patch;
                Piece = piece;
                Anchor = joint.anchor;
                ConnectedAnchor = joint.connectedAnchor;
                ConnectedBody = joint.connectedBody;
            }
        }

        /// <summary>All mud bonds share the Inspector settings on mud.prefab.</summary>
        private void LoadJointSettings()
        {
            _jointSettings = GetComponent<GuardWaveMudJointSettings>();
            if (_jointSettings == null)
                _jointSettings = GuardWaveMudJointSettings.LoadPrefabSettings();
            if (_jointSettings == null)
                throw new MissingReferenceException(
                    "Missing GuardWaveMudJointSettings on Resources/Prefabs/Objects/mud.prefab");
        }

        public void Initialize(GuardWaveGame game, Collider surfaceCollider,
            Rigidbody surfaceBody)
        {
            _game = game;
            _surfaceCollider = surfaceCollider;
            _surfaceBody = surfaceBody;
            _surfacePiece = surfaceCollider.GetComponentInParent<GuardWavePiece>();
            LoadJointSettings();
            TrackSurfacePiece();
        }

        /// <summary>Whether this patch belongs to the support being removed.</summary>
        public bool IsOnSurface(GuardWavePiece piece) =>
            piece != null && _surfacePiece == piece;

        /// <summary>Join two touching bodies with one 3D ellipsoidal mud lump.</summary>
        public bool ConnectContact(GuardWavePiece piece, Vector3 point, Vector3 normal)
        {
            if (_game == null || piece == null || piece.Body == null ||
                piece.Body == _surfaceBody ||
                _game.HasMudBondBetween(piece.Body, _surfaceBody))
                return false;
            Vector3 anchor = piece.transform.InverseTransformPoint(point);
            Vector3 connectedAnchor = _surfaceBody == null ? point
                : _surfaceBody.transform.InverseTransformPoint(point);
            CreateBond(piece, anchor, connectedAnchor, _surfaceBody);
            if (!HasBond(piece)) return false;

            // The instantiated mud prefab already provides the oval renderer.
            return true;
        }

        /// <summary>씬에 배치된 진흙의 트리거 중심을 접착 표면 샘플로 등록한다.</summary>
        public void InitializeAuthored(GuardWaveGame game, Collider surfaceCollider,
            Rigidbody surfaceBody)
        {
            _game = game;
            _surfaceCollider = surfaceCollider;
            _surfaceBody = surfaceBody;
            _surfacePiece = surfaceCollider.GetComponentInParent<GuardWavePiece>();
            LoadJointSettings();
            TrackSurfacePiece();
            // Scene-authored mud uses many rendered child meshes under one patch.
            // Cure all together without modifying the shared Mud.mat asset.
            MeshRenderer[] renderers = GetComponentsInChildren<MeshRenderer>(true);
            Material source = renderers.Length > 0 && renderers[0].sharedMaterial != null
                ? renderers[0].sharedMaterial : MudMaterial;
            if (source != null && renderers.Length > 0)
            {
                Material privateMaterial = CreateInstanceMaterial(source);
                foreach (MeshRenderer renderer in renderers)
                    renderer.sharedMaterial = privateMaterial;
            }

            foreach (SphereCollider trigger in GetComponents<SphereCollider>())
            {
                if (trigger.isTrigger)
                {
                    _adhesionTriggers.Add(trigger);
                    trigger.enabled = false;
                }
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
            // Do not wait for OnTriggerEnter/Stay: painting over two surfaces
            // that already touch must glue them immediately.
            // Explicit contact clicks exclusively create new bonds.
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

        private Material CreateInstanceMaterial(Material source)
        {
            if (source == null) return null;
            _instanceMaterial = new Material(source) { name = source.name + " (Mud Patch)" };
            _wetSmoothness = _instanceMaterial.HasProperty("_Smoothness")
                ? _instanceMaterial.GetFloat("_Smoothness") : 0f;
            _wetMetallic = _instanceMaterial.HasProperty("_Metallic")
                ? _instanceMaterial.GetFloat("_Metallic") : 0f;
            return _instanceMaterial;
        }

        /// <summary>Restores curing, trigger and contact-cache state without multiplying joint forces again.</summary>
        public void RestoreDryness(bool dry)
        {
            StopAllCoroutines();
            _isDry = dry;
            RefreshAdhesionTriggers();
            if (_instanceMaterial == null) return;
            if (_instanceMaterial.HasProperty("_Smoothness"))
                _instanceMaterial.SetFloat("_Smoothness", dry ? 0.025f : _wetSmoothness);
            if (_instanceMaterial.HasProperty("_Metallic"))
                _instanceMaterial.SetFloat("_Metallic", dry ? 0f : _wetMetallic);
        }

        public void ResetDryness() => RestoreDryness(false);

        /// <summary>
        /// Dry exactly this painted stroke. Disable new adhesion immediately
        /// and double only the FixedJoints that this stroke already created.
        /// </summary>
        public void Dry()
        {
            if (_isDry) return;
            _isDry = true;
            RefreshAdhesionTriggers();
            foreach (FixedJoint joint in _bonds.Values)
            {
                if (joint == null) continue;
                GuardWaveInitialLogJoint initialLog = joint.GetComponent<GuardWaveInitialLogJoint>();
                if (initialLog != null)
                    initialLog.SetDryness(true);
                else
                    joint.breakForce *= 2f;
                joint.breakTorque *= 2f;
            }
            if (_instanceMaterial != null)
                StartCoroutine(AnimateDryMaterial());
        }

        /// <summary>Enables adhesion triggers only while wet and invalidates checks from the previous state.</summary>
        private void RefreshAdhesionTriggers()
        {
            _lastContactCheck.Clear();
            foreach (Collider trigger in _adhesionTriggers)
                if (trigger != null) trigger.enabled = false;
        }

        private IEnumerator AnimateDryMaterial()
        {
            const float duration = 0.1f;
            bool smoothness = _instanceMaterial.HasProperty("_Smoothness");
            bool metallic = _instanceMaterial.HasProperty("_Metallic");
            float initialSmoothness = smoothness ? _instanceMaterial.GetFloat("_Smoothness") : 0f;
            float initialMetallic = metallic ? _instanceMaterial.GetFloat("_Metallic") : 0f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed = Mathf.Min(duration, elapsed + Time.unscaledDeltaTime);
                float t = elapsed / duration;
                if (smoothness) _instanceMaterial.SetFloat("_Smoothness",
                    Mathf.Lerp(initialSmoothness, 0.025f, t));
                if (metallic) _instanceMaterial.SetFloat("_Metallic",
                    Mathf.Lerp(initialMetallic, 0f, t));
                yield return null;
            }
        }



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

        /// <summary>Creates an adhesion capsule beginning at the given sample and registers its curing state.</summary>
        private void CreateTriggerSegment(int firstSample)
        {
            _triggerSegmentFirstSample = firstSample;
            _triggerSegment = new GameObject("Mud Trigger Segment");
            _triggerSegment.transform.SetParent(transform, false);
            _triggerCapsule = _triggerSegment.AddComponent<CapsuleCollider>();
            _triggerCapsule.isTrigger = true;
            _triggerCapsule.radius = BrushRadius;
            _triggerCapsule.direction = 1;
            _adhesionTriggers.Add(_triggerCapsule);
            _triggerCapsule.enabled = !_isDry;
            _triggerSegment.AddComponent<GuardWaveMudTriggerRelay>().Initialize(this);
        }

        /// <summary>
        /// An existing joint is the result of a piece having already entered
        /// painted mud. Adopt it into the same bond registry as runtime bonds,
        /// instead of creating a second FixedJoint or a separate curing rule.
        /// Matching is done on the mud's tangent plane, so authoring a log
        /// slightly above the creek does not prevent registration.
        /// </summary>
        public bool TryAdoptExistingBond(GuardWavePiece piece, FixedJoint joint)
        {
            if (_game == null || _isDry || piece == null || joint == null ||
                !piece.gameObject.activeInHierarchy || _points.Count == 0 ||
                !_game.IsPlacedPiece(piece) ||
                joint.connectedBody != _surfaceBody ||
                _game.HasMudBondBetween(piece.Body, joint.connectedBody) ||
                HasBond(piece))
                return false;

            Vector3 worldAnchor = piece.transform.TransformPoint(joint.anchor);
            float radiusSqr = BrushRadius * BrushRadius;
            bool touchesPaint = false;
            for (int i = 0; i < _points.Count; i++)
            {
                Vector3 sample = transform.TransformPoint(_points[i]);
                Vector3 normal = transform.TransformDirection(_normals[i]).normalized;
                Vector3 lateral = Vector3.ProjectOnPlane(worldAnchor - sample, normal);
                if (lateral.sqrMagnitude > radiusSqr) continue;
                touchesPaint = true;
                break;
            }

            if (!touchesPaint) return false;
            _jointSettings.ApplyTo(joint);
            _bonds.Add(piece, joint);
            _game.TrackMudPatch(piece, this);
            _game.RegisterMudBond(piece, joint,
                _jointSettings.SolverIterations, _jointSettings.SolverVelocityIterations);
            GuardWaveInitialLogJoint initialLog = piece.GetComponent<GuardWaveInitialLogJoint>();
            if (initialLog != null)
                initialLog.Restart(joint, _isDry, _jointSettings.BreakForce);
            return true;
        }

        private void OnTriggerEnter(Collider other) => TryBond(other);
        private void OnTriggerStay(Collider other) => TryBond(other);

        /// <summary>Checks contact forwarded by a child adhesion capsule.</summary>
        internal void ProcessTrigger(Collider other) => TryBond(other);

        /// <summary>
        /// Resolves a currently eligible target and its connection before searching painted samples.
        /// Creek contact targets the painted piece; other contacts target the incoming piece.
        /// </summary>
        private bool TryResolveBondTarget(Collider other, out GuardWavePiece piece,
            out Rigidbody connectedBody)
        {
            piece = null;
            connectedBody = null;
            if (_game == null || _isDry || other == null ||
                other == _surfaceCollider || other.isTrigger) return false;
            if (_surfaceBody != null && other.attachedRigidbody == _surfaceBody)
                return false;

            bool bindSourceToGround = other == _game.CreekBedCollider &&
                _surfacePiece != null && _game.IsPlacedPiece(_surfacePiece);
            piece = bindSourceToGround ? _surfacePiece
                : other.GetComponentInParent<GuardWavePiece>();

            if (piece == null || !_game.IsPlacedPiece(piece) ||
                piece.Body == null || piece.Body.isKinematic ||
                (!bindSourceToGround && piece.Body == _surfaceBody) ||
                _game.HasMudBondBetween(piece.Body,
                    bindSourceToGround ? null : _surfaceBody))
                return false;

            connectedBody = bindSourceToGround ? null : _surfaceBody;
            return true;
        }

        /// <summary>Attempts immediate adhesion at a brush point after resolving its eligible target.</summary>
        private bool TryBondAt(Collider other, Vector3 paintedPoint, Vector3 paintedNormal)
        {
            return TryResolveBondTarget(other, out GuardWavePiece piece,
                       out Rigidbody connectedBody) &&
                   TryCreateBondAt(other, piece, connectedBody, paintedPoint, paintedNormal);
        }

        /// <summary>Checks the painted contact gap and creates a joint for an already resolved target.</summary>
        private bool TryCreateBondAt(Collider other, GuardWavePiece piece,
            Rigidbody connectedBody, Vector3 paintedPoint, Vector3 paintedNormal)
        {
            Vector3 normal = paintedNormal.normalized;
            Vector3 closest = other.ClosestPoint(paintedPoint);
            Vector3 offset = closest - paintedPoint;
            float normalGap = Vector3.Dot(offset, normal);
            float lateral = Vector3.ProjectOnPlane(offset, normal).magnitude;
            if (normalGap < -MaxBondInsideSurface || normalGap > MaxBondNormalGap ||
                lateral > BrushRadius * 1.05f)
                return false;

            // Midpoint anchors prevent a large solver impulse when convex
            // meshes rest slightly above the painted point.
            Vector3 anchor = (paintedPoint + closest) * 0.5f;
            Vector3 localAnchor = piece.transform.InverseTransformPoint(anchor);
            Vector3 connectedAnchor = connectedBody == null ? anchor
                : connectedBody.transform.InverseTransformPoint(anchor);
            // Remove the incoming fall/rotation momentum before fixing the piece.
            // Only new trigger-based bonds pass here; restoring checkpoint bonds
            // must preserve their saved Rigidbody velocities.
            Rigidbody pieceBody = piece.Body;
            pieceBody.linearVelocity = Vector3.zero;
            pieceBody.angularVelocity = Vector3.zero;
            CreateBond(piece, localAnchor, connectedAnchor, connectedBody);
            return HasBond(piece);
        }

        private void TryBondNearSample(Vector3 worldPoint, Vector3 worldNormal)
        {
            if (_game == null || _isDry) return;
            int count = Physics.OverlapSphereNonAlloc(worldPoint,
                BrushRadius + MaxBondNormalGap, _nearbyColliders,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                TryBondAt(_nearbyColliders[i], worldPoint, worldNormal);
        }

        /// <summary>
        /// A continuous brush gesture may cross from creek to an already
        /// placed log (or the reverse). Each mesh is kept on its own moving
        /// surface, but the joint is formed across the shared paint boundary.
        /// </summary>
        public void TryBridgeTo(Collider nextSurface, Vector3 worldPoint, Vector3 worldNormal)
        {
            TryBondAt(nextSurface, worldPoint, worldNormal);
        }

        public bool TryGetLastSurfaceSample(out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.up;
            if (_points.Count == 0) return false;
            int last = _points.Count - 1;
            point = transform.TransformPoint(_points[last]);
            normal = transform.TransformDirection(_normals[last]).normalized;
            return true;
        }


        /// <summary>Searches an eligible contact once per physics tick, including contact with the creek bed.</summary>
        private void TryBond(Collider other)
        {
            // Click-to-bond is the only way to create a new mud connection.
            // Keep callbacks harmless for old authored trigger components.
        }

        /// <summary>Creates and registers one configured mud bond while preventing duplicate body pairs.</summary>
        private void CreateBond(GuardWavePiece piece, Vector3 anchor,
            Vector3 connectedAnchor, Rigidbody connectedBody)
        {
            if (piece == null || HasBond(piece) ||
                (_game != null && _game.HasMudBondBetween(piece.Body, connectedBody)))
                return;
            FixedJoint joint = StageJoint.Create(piece.gameObject, connectedBody, anchor,
                connectedAnchor, _jointSettings.BreakForce, _jointSettings.BreakTorque, false);
            _jointSettings.ApplyTo(joint);
            if (_isDry)
            {
                joint.breakForce *= 2f;
                joint.breakTorque *= 2f;
            }
            _bonds[piece] = joint;
            _game?.TrackMudPatch(piece, this);
            _game?.RegisterMudBond(piece, joint,
                _jointSettings.SolverIterations, _jointSettings.SolverVelocityIterations);
        }

        public bool HasBond(GuardWavePiece piece)
        {
            if (piece == null || !_bonds.TryGetValue(piece, out FixedJoint joint))
                return false;
            if (joint != null) return true;
            _game?.ReleaseMudBond(piece, joint);
            _bonds.Remove(piece);
            _game?.UntrackMudPatch(piece, this);
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
            _game?.UntrackMudPatch(piece, this);
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
                _game?.UntrackMudPatch(bond.Key, this);
                if (bond.Value != null) Destroy(bond.Value);
            }
            _bonds.Clear();
            _lastContactCheck.Clear();
        }

        public void RestoreBond(MudBondState state, bool fromInitialDefinition = false)
        {
            if (state.Patch != this || state.Piece == null ||
                !_game.IsPlacedPiece(state.Piece)) return;
            CreateBond(state.Piece, state.Anchor, state.ConnectedAnchor,
                state.ConnectedBody);
            if (_bonds.TryGetValue(state.Piece, out FixedJoint joint) && joint != null)
            {
                // No stale snapshot may override prefab-authored joint physics.
                _jointSettings.ApplyTo(joint);
                GuardWaveInitialLogJoint initialLog =
                    state.Piece.GetComponent<GuardWaveInitialLogJoint>();
                if (initialLog != null)
                    initialLog.Restart(joint, _isDry, _jointSettings.BreakForce);
            }
        }

        private void OnDestroy()
        {
            ClearBonds();
            if (_surfaceBody != null)
            {
                GuardWavePiece surfacePiece = _surfaceBody.GetComponent<GuardWavePiece>();
                if (surfacePiece != null) _game?.UntrackMudPatch(surfacePiece, this);
            }
            if (_instanceMaterial != null) Destroy(_instanceMaterial);
            if (_mesh != null) Destroy(_mesh);
        }
    }

}
