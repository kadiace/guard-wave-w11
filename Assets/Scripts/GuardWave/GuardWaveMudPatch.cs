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
        private const float ColliderSpacing = 0.5f;
        private const float SurfaceOffset = 0.012f;
        private const int CrossSections = 9;
        private static Texture2D s_impastoTexture;
        private static int s_impastoTextureUsers;

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
        private bool _isDry;

        public Collider SurfaceCollider => _surfaceCollider;
        public bool IsFull => _points.Count >= MaxSamples;
        public bool IsDry => _isDry;

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
            var vertices = new List<Vector3>(rows * CrossSections);
            var triangles = new List<int>((rows - 1) * (CrossSections - 1) * 6);
            var uv = new List<Vector2>(rows * CrossSections);
            float[] lengths = new float[count];
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
            renderer.sharedMaterial = CreateMudMaterial(true);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            preview.hideFlags = HideFlags.HideInHierarchy;
            return preview;
        }

        private static Material CreateMudMaterial(bool preview)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader);
            material.name = preview ? "Impasto Brush Preview" : "Wet Impasto Adhesive";
            Color tint = preview
                ? new Color(0.75f, 0.51f, 0.29f, 1f)
                : new Color(0.42f, 0.27f, 0.14f, 1f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", preview ? 0.77f : 0.83f);
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", preview ? 0f : 0.08f);
            if (material.HasProperty("_SpecColor"))
                material.SetColor("_SpecColor", new Color(0.30f, 0.24f, 0.17f, 1f));

            // The texture has directional pigment/binder streaks, not splat
            // circles. Share one readable texture among all paint strokes.
            if (!preview)
            {
                if (s_impastoTexture == null)
                    s_impastoTexture = CreateImpastoTexture();
                s_impastoTextureUsers++;
                if (material.HasProperty("_BaseMap"))
                    material.SetTexture("_BaseMap", s_impastoTexture);
                if (material.HasProperty("_MainTex"))
                    material.SetTexture("_MainTex", s_impastoTexture);
            }
            return material;
        }

        private static Texture2D CreateImpastoTexture()
        {
            const int size = 96;
            Texture2D texture = new Texture2D(
                size, size, TextureFormat.RGBA32, true);
            texture.name = "Impasto Pigment & Binder Striations";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;

            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)(size - 1);
                    float lengthwise = Mathf.PerlinNoise(u * 10.0f, v * 1.8f);
                    float fine = Mathf.PerlinNoise(u * 34f + 2f, v * 5f);
                    float streak = Mathf.Sin(u * 61f + v * 2.7f) * 0.036f;
                    float pigment = Mathf.Clamp(
                        0.69f + 0.19f * lengthwise + 0.13f * fine + streak,
                        0.48f, 1f);
                    pixels[y * size + x] = new Color(
                        pigment, pigment * 0.94f, pigment * 0.87f, 1f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>
        /// Dry exactly this painted stroke. Disable new adhesion immediately
        /// and double only the FixedJoints that this stroke already created.
        /// </summary>
        public void Dry()
        {
            if (_isDry) return;
            _isDry = true;
            foreach (FixedJoint joint in _bonds.Values)
            {
                if (joint == null) continue;
                joint.breakForce *= 2f;
                joint.breakTorque *= 2f;
            }
            if (_material != null)
                StartCoroutine(AnimateDryMaterial());
        }

        private IEnumerator AnimateDryMaterial()
        {
            const float duration = 0.1f;
            bool smoothness = _material.HasProperty("_Smoothness");
            bool metallic = _material.HasProperty("_Metallic");
            float initialSmoothness = smoothness ? _material.GetFloat("_Smoothness") : 0f;
            float initialMetallic = metallic ? _material.GetFloat("_Metallic") : 0f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed = Mathf.Min(duration, elapsed + Time.unscaledDeltaTime);
                float t = elapsed / duration;
                if (smoothness) _material.SetFloat("_Smoothness",
                    Mathf.Lerp(initialSmoothness, 0.025f, t));
                if (metallic) _material.SetFloat("_Metallic",
                    Mathf.Lerp(initialMetallic, 0f, t));
                yield return null;
            }
        }

        private void OnTriggerEnter(Collider other) => TryBond(other);
        private void OnTriggerStay(Collider other) => TryBond(other);

        private void TryBond(Collider other)
        {
            if (_game == null || _isDry) return;
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
            // Wave snapshot restoration recreates joints through this method.
            // Keep cured adhesive twice as strong after restoring a failed wave.
            float multiplier = _isDry ? 2f : 1f;
            joint.breakForce = _breakForce * multiplier;
            joint.breakTorque = _breakTorque * multiplier;
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
            if (s_impastoTextureUsers > 0 && --s_impastoTextureUsers == 0)
            {
                if (s_impastoTexture != null) Destroy(s_impastoTexture);
                s_impastoTexture = null;
            }
        }
    }
}
