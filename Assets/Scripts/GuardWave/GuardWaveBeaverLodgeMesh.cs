using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Draws a detailed, faceted beaver lodge as one mesh with a small material palette.
    /// Geometry is generated for the authored Flood target in edit mode and in play mode.
    /// Branches are visual only: the target has a single BoxCollider for fluid/physics.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class GuardWaveBeaverLodgeMesh : MonoBehaviour
    {
        // Preserve the original flag's ground footprint. Its root was 2.08m above the base.
        private const float BASE_Y = -2.08f;
        private const float TAU = Mathf.PI * 2f;
        private const int SHADE_COUNT = 7;

        private static readonly Color[] _shades =
        {
            new Color(0.095f, 0.068f, 0.047f), // hollow / dark interior
            new Color(0.20f, 0.115f, 0.064f),  // dark, weathered bark
            new Color(0.31f, 0.174f, 0.091f),  // chestnut bark
            new Color(0.40f, 0.245f, 0.135f),  // lighter branches
            new Color(0.51f, 0.345f, 0.205f),  // dry twigs
            new Color(0.65f, 0.475f, 0.285f),  // pale cut ends
            new Color(0.30f, 0.35f, 0.155f),   // sparse moss
        };

        private static readonly float[] _levels =
            { 0.12f, 0.58f, 1.08f, 1.56f, 1.98f, 2.36f, 2.68f, 2.88f };
        private static readonly float[] _radii =
            { 1.30f, 1.37f, 1.30f, 1.16f, 0.94f, 0.67f, 0.33f, 0.06f };

        private Mesh _mesh;
        private Material[] _materials;

        private void OnEnable()
        {
            Rebuild();
        }

        private void OnDisable()
        {
            Release();
        }

        /// <summary>
        /// Rebuilds the woven surface, overlapping diagonal timbers, sticks and doorway
        /// as one mesh, so hundreds of branches do not produce hundreds of renderers.
        /// </summary>
        private void Rebuild()
        {
            Release();
            Geometry geometry = new Geometry();
            AddCore(geometry);
            AddFoundation(geometry);
            AddRibs(geometry);
            AddWovenBranches(geometry);
            AddLooseTwigs(geometry);
            AddEntrance(geometry);
            AddMoss(geometry);

            _mesh = new Mesh
            {
                name = "Beaver Lodge - Low Poly Woven Branches",
                indexFormat = IndexFormat.UInt32,
                hideFlags = HideFlags.HideAndDontSave
            };
            _mesh.SetVertices(geometry.Vertices);
            _mesh.subMeshCount = SHADE_COUNT;
            for (int i = 0; i < SHADE_COUNT; i++)
                _mesh.SetTriangles(geometry.Triangles[i], i);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            _materials = new Material[SHADE_COUNT];
            for (int i = 0; i < SHADE_COUNT; i++)
            {
                Material material = new Material(shader)
                {
                    name = "Beaver Lodge / " + i,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", _shades[i]);
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", _shades[i]);
                if (material.HasProperty("_Smoothness"))
                    material.SetFloat("_Smoothness", 0f);
                _materials[i] = material;
            }

            GetComponent<MeshFilter>().sharedMesh = _mesh;
            GetComponent<MeshRenderer>().sharedMaterials = _materials;
        }

        /// <summary>Releases edit-time and runtime geometry without modifying authored scene objects.</summary>
        private void Release()
        {
            MeshFilter filter = GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh == _mesh) filter.sharedMesh = null;
            MeshRenderer renderer = GetComponent<MeshRenderer>();
            if (renderer != null && _materials != null) renderer.sharedMaterials = new Material[0];
            if (_mesh != null) DestroyGenerated(_mesh);
            _mesh = null;
            if (_materials != null)
                foreach (Material material in _materials)
                    if (material != null) DestroyGenerated(material);
            _materials = null;
        }

        /// <summary>Uses delayed destruction in play mode and immediate destruction in the editor.</summary>
        private static void DestroyGenerated(Object item)
        {
            if (Application.isPlaying) Destroy(item);
            else DestroyImmediate(item);
        }

        /// <summary>Returns the irregular rounded lodge profile at the given height above its base.</summary>
        private static float RadiusAt(float height)
        {
            for (int i = 0; i < _levels.Length - 1; i++)
            {
                if (height > _levels[i + 1]) continue;
                float t = Mathf.Clamp01((height - _levels[i]) / (_levels[i + 1] - _levels[i]));
                return Mathf.Lerp(_radii[i], _radii[i + 1], t);
            }
            return _radii[_radii.Length - 1];
        }

        /// <summary>Returns a point on the rounded lodge in its local coordinate system.</summary>
        private static Vector3 Point(float height, float radius, float angle)
        {
            return new Vector3(Mathf.Sin(angle) * radius, BASE_Y + height,
                Mathf.Cos(angle) * radius);
        }

        /// <summary>Stable hand-crafted-looking perturbation that does not change between play sessions.</summary>
        private static float Noise(int seed)
        {
            float n = Mathf.Sin(seed * 12.9898f + 78.233f) * 43758.5453f;
            return n - Mathf.Floor(n);
        }

        /// <summary>True only for the low, open doorway on the downriver-facing side.</summary>
        private static bool IsDoorway(float height, float angle)
        {
            return height < 1.05f &&
                Mathf.Abs(Mathf.DeltaAngle(angle * Mathf.Rad2Deg, 180f)) < 22f;
        }

        /// <summary>A faceted inner mass fills the gaps without hiding the outer woven timbers.</summary>
        private static void AddCore(Geometry mesh)
        {
            const int sectors = 24;
            for (int level = 0; level < _levels.Length - 1; level++)
            {
                for (int i = 0; i < sectors; i++)
                {
                    float a = TAU * i / sectors;
                    float b = TAU * (i + 1) / sectors;
                    if (level < 2 && IsDoorway(_levels[level], (a + b) * 0.5f)) continue;
                    float wa = (Noise(i * 31 + level * 47) - 0.5f) * 0.08f;
                    float wb = (Noise((i + 1) * 31 + level * 47) - 0.5f) * 0.08f;
                    Vector3 p0 = Point(_levels[level], _radii[level] + wa, a);
                    Vector3 p1 = Point(_levels[level], _radii[level] + wb, b);
                    Vector3 p2 = Point(_levels[level + 1], _radii[level + 1] + wb, b);
                    Vector3 p3 = Point(_levels[level + 1], _radii[level + 1] + wa, a);
                    mesh.Quad(p0, p1, p2, p3, 1 + (i + level) % 2);
                }
            }
        }

        /// <summary>Thick overlapping foundation beams establish the 3m-wide silhouette.</summary>
        private static void AddFoundation(Geometry mesh)
        {
            for (int i = 0; i < 24; i++)
            {
                float a = TAU * (i + 0.1f) / 24f;
                if (IsDoorway(0.25f, a)) continue;
                float r = 1.17f + 0.06f * Noise(i + 219);
                Vector3 mid = Point(0.19f + 0.06f * Noise(i + 37), r, a);
                Vector3 tangent = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
                float half = 0.30f + Noise(i + 103) * 0.16f;
                mesh.Branch(mid - tangent * half, mid + tangent * half,
                    0.12f + Noise(i + 58) * 0.045f, 0.10f, 2 + i % 3, 6);
            }
        }

        /// <summary>Leaning timbers form a visible dome with irregular criss-crossed joints.</summary>
        private static void AddRibs(Geometry mesh)
        {
            for (int i = 0; i < 36; i++)
            {
                float angle = TAU * (i + 0.30f * Noise(i + 89)) / 36f;
                for (int j = 0; j < _levels.Length - 1; j++)
                {
                    float startH = _levels[j] + 0.03f;
                    float endH = _levels[j + 1] + 0.03f;
                    if (IsDoorway(startH, angle) && j < 2) continue;
                    float bend = (Noise(i * 17 + j * 13) - .5f) * .10f;
                    float a = angle + bend;
                    float b = angle + (Noise(i * 13 + (j + 1) * 17) - .5f) * .10f;
                    Vector3 start = Point(startH, Mathf.Min(1.37f, RadiusAt(startH) + .085f), a);
                    Vector3 end = Point(endH, Mathf.Min(1.37f, RadiusAt(endH) + .085f), b);
                    float thickness = Mathf.Lerp(.115f, .045f, endH / 2.95f);
                    mesh.Branch(start, end, thickness, thickness * .82f,
                        2 + (i + j) % 3, 5);
                }
            }
        }

        /// <summary>Staggered overlapping branch courses create a wicker-like crossed weave.</summary>
        private static void AddWovenBranches(Geometry mesh)
        {
            for (int level = 0; level < 10; level++)
            {
                float height = .34f + .235f * level;
                float radius = RadiusAt(height) + .13f;
                for (int i = 0; i < 32; i++)
                {
                    float a = TAU * (i + (level % 2) * .5f) / 32f;
                    float direction = (level % 2 == 0) ? 1f : -1f;
                    float b = a + direction * (.31f + .06f * Noise(i * 17 + level * 23));
                    if (IsDoorway(height, a) || IsDoorway(height, b)) continue;
                    Vector3 p0 = Point(height + (Noise(i * 3 + level) - .5f) * .10f,
                        radius, a);
                    Vector3 p1 = Point(height + (Noise(i * 7 + level) - .5f) * .14f,
                        radius, b);
                    mesh.Branch(p0, p1, .055f + .014f * Noise(i + level * 4),
                        .036f, 2 + (i + level) % 3, 5);
                }
            }
            // Long oblique strands visibly cross both the vertical ribs and ring courses.
            for (int i = 0; i < 110; i++)
            {
                float height = .48f + 2.0f * Noise(i * 41 + 3);
                float a = TAU * Noise(i * 31 + 15);
                float b = a + (i % 2 == 0 ? .20f : -.20f);
                float endHeight = Mathf.Clamp(height + (i % 2 == 0 ? .25f : -.25f),
                    .28f, 2.80f);
                if (IsDoorway(Mathf.Min(height, endHeight), a)) continue;
                mesh.Branch(Point(height, RadiusAt(height) + .16f, a),
                    Point(endHeight, RadiusAt(endHeight) + .16f, b),
                    .042f, .024f, 2 + i % 3, 5);
            }
        }

        /// <summary>Broken twig ends, diagonal sprigs and pale exposed cuts make the lodge organic.</summary>
        private static void AddLooseTwigs(Geometry mesh)
        {
            for (int i = 0; i < 140; i++)
            {
                float height = .35f + 2.20f * Noise(i * 61 + 12);
                float a = TAU * Noise(i * 53 + 6);
                if (IsDoorway(height, a)) continue;
                float radius = RadiusAt(height) + .11f;
                float extra = .11f + .12f * Noise(i * 7 + 9);
                Vector3 p0 = Point(height, radius, a);
                Vector3 p1 = Point(Mathf.Min(2.84f,
                    height + (Noise(i * 19 + 5) - .45f) * .35f),
                    Mathf.Min(1.43f, radius + extra), a + (Noise(i * 23 + 6) - .5f) * .14f);
                mesh.Branch(p0, p1, .025f + .018f * Noise(i + 56),
                    .009f, 2 + i % 3, 5);
            }
            // At the crown, many bent pieces finish in a ragged peak rather than a perfect cone.
            for (int i = 0; i < 19; i++)
            {
                float a = TAU * i / 19f;
                mesh.Branch(Point(2.48f, .55f, a),
                    Point(2.87f + .055f * Noise(i + 508),
                        .055f + .13f * Noise(i + 409), a + .22f),
                    .052f, .019f, 2 + i % 3, 5);
            }
        }

        /// <summary>Frames the dark recessed entrance with split logs instead of covering it with twigs.</summary>
        private static void AddEntrance(Geometry mesh)
        {
            Vector3 leftBottom = new Vector3(-.46f, BASE_Y + .13f, -1.27f);
            Vector3 rightBottom = new Vector3(.46f, BASE_Y + .13f, -1.27f);
            Vector3 leftTop = new Vector3(-.43f, BASE_Y + .85f, -1.28f);
            Vector3 rightTop = new Vector3(.43f, BASE_Y + .85f, -1.28f);
            Vector3 peak = new Vector3(0f, BASE_Y + 1.09f, -1.27f);
            Vector3 innerLeft = new Vector3(-.39f, BASE_Y + .16f, -1.08f);
            Vector3 innerRight = new Vector3(.39f, BASE_Y + .16f, -1.08f);
            Vector3 innerTopLeft = new Vector3(-.39f, BASE_Y + .78f, -1.08f);
            Vector3 innerTopRight = new Vector3(.39f, BASE_Y + .78f, -1.08f);
            mesh.Quad(innerLeft, innerTopLeft, innerTopRight, innerRight, 0);
            mesh.Branch(leftBottom, leftTop, .13f, .11f, 2, 6);
            mesh.Branch(rightBottom, rightTop, .13f, .11f, 2, 6);
            mesh.Branch(leftTop, peak, .12f, .095f, 3, 6);
            mesh.Branch(peak, rightTop, .095f, .12f, 3, 6);
            // Overlapping driftwood just outside the doorway adds depth.
            mesh.Branch(new Vector3(-.72f, BASE_Y + .18f, -1.30f),
                new Vector3(-.51f, BASE_Y + 1.26f, -1.05f), .09f, .045f, 1, 6);
            mesh.Branch(new Vector3(.69f, BASE_Y + .18f, -1.31f),
                new Vector3(.50f, BASE_Y + 1.18f, -1.05f), .09f, .045f, 1, 6);
        }

        /// <summary>Small angular moss patches soften the wood silhouette without adding objects.</summary>
        private static void AddMoss(Geometry mesh)
        {
            for (int i = 0; i < 27; i++)
            {
                float height = .35f + 1.82f * Noise(i * 29 + 9);
                float angle = TAU * Noise(i * 33 + 7);
                if (IsDoorway(height, angle)) continue;
                Vector3 center = Point(height, RadiusAt(height) + .18f, angle);
                Vector3 radial = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Vector3 tangent = new Vector3(Mathf.Cos(angle), 0f, -Mathf.Sin(angle));
                float size = .07f + .07f * Noise(i + 91);
                Vector3 tip = center + radial * size;
                Vector3 a = center + tangent * size + Vector3.up * size * .3f;
                Vector3 b = center - tangent * size + Vector3.up * size * .4f;
                Vector3 c = center + Vector3.up * size * 1.5f;
                mesh.Triangle(tip, a, c, 6);
                mesh.Triangle(tip, c, b, 6);
                mesh.Triangle(tip, b, a, 6);
            }
        }

        /// <summary>Single-mesh faceted geometry buffer, with seven earthy submesh colors.</summary>
        private sealed class Geometry
        {
            internal readonly List<Vector3> Vertices = new List<Vector3>(18000);
            internal readonly List<int>[] Triangles = new List<int>[SHADE_COUNT];

            internal Geometry()
            {
                for (int i = 0; i < SHADE_COUNT; i++)
                    Triangles[i] = new List<int>(4000);
            }

            /// <summary>Adds one flat-shaded triangular face to the requested submesh.</summary>
            internal void Triangle(Vector3 a, Vector3 b, Vector3 c, int shade)
            {
                int first = Vertices.Count;
                Vertices.Add(a);
                Vertices.Add(b);
                Vertices.Add(c);
                Triangles[shade].Add(first);
                Triangles[shade].Add(first + 1);
                Triangles[shade].Add(first + 2);
            }

            /// <summary>Adds a flat-shaded two-triangle quadrilateral.</summary>
            internal void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int shade)
            {
                Triangle(a, b, c, shade);
                Triangle(a, c, d, shade);
            }

            /// <summary>Adds one capped five- or six-sided tapered log with alternating bark faces.</summary>
            internal void Branch(Vector3 from, Vector3 to, float startRadius,
                float endRadius, int shade, int sides)
            {
                Vector3 axis = to - from;
                if (axis.sqrMagnitude < .00001f) return;
                axis.Normalize();
                Vector3 tangent = Vector3.Cross(axis, Vector3.up);
                if (tangent.sqrMagnitude < .01f)
                    tangent = Vector3.Cross(axis, Vector3.forward);
                tangent.Normalize();
                Vector3 bitangent = Vector3.Cross(axis, tangent).normalized;

                for (int i = 0; i < sides; i++)
                {
                    float a = TAU * i / sides;
                    float b = TAU * (i + 1) / sides;
                    Vector3 r0 = tangent * Mathf.Cos(a) + bitangent * Mathf.Sin(a);
                    Vector3 r1 = tangent * Mathf.Cos(b) + bitangent * Mathf.Sin(b);
                    Quad(from + r0 * startRadius, from + r1 * startRadius,
                        to + r1 * endRadius, to + r0 * endRadius,
                        Mathf.Clamp(shade + (i % 3 == 0 ? -1 : 0), 1, 4));
                    Triangle(from, from + r1 * startRadius,
                        from + r0 * startRadius, 1);
                    Triangle(to, to + r0 * endRadius, to + r1 * endRadius, 5);
                }
            }
        }
    }
}
