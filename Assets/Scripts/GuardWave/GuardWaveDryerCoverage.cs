using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Render-only surface disc showing the area currently being blow-dried.
    /// The brighter radial sector fills during the required one-second hold.
    /// It has no Collider and never affects mud/creek physics or raycasts.
    /// </summary>
    public sealed class GuardWaveDryerCoverage
    {
        private const int Segments = 48;
        private readonly GameObject _root;
        private readonly Mesh _mesh;
        private readonly Material _material;
        private readonly List<Vector3> _vertices = new List<Vector3>(220);
        private readonly List<Color> _colors = new List<Color>(220);
        private readonly List<int> _triangles = new List<int>(650);

        public GuardWaveDryerCoverage()
        {
            _root = new GameObject("Hair Dryer Coverage");
            _root.hideFlags = HideFlags.HideInHierarchy;
            MeshFilter filter = _root.AddComponent<MeshFilter>();
            MeshRenderer renderer = _root.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "Hair Dryer Surface Coverage" };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;

            // The dedicated URP shader reads vertex alpha and is included
            // through Resources, avoiding runtime shader stripping.
            Shader shader = Resources.Load<Shader>("Textures/GuardWaveDryerCoverage");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            _material = new Material(shader) { name = "Hair Dryer Coverage (Runtime)" };
            if (_material.HasProperty("_Color"))
                _material.SetColor("_Color", Color.white);
            if (_material.HasProperty("_BaseColor"))
                _material.SetColor("_BaseColor", Color.white);
            _material.renderQueue = (int)RenderQueue.Transparent + 10;
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = 50;
            _root.SetActive(false);
        }

        /// <summary>Follow the actual hit point and show the current hold progress.</summary>
        public void Show(Vector3 surfacePoint, Vector3 normal, float progress)
        {
            _root.transform.SetPositionAndRotation(
                surfacePoint + normal * 0.055f,
                Quaternion.FromToRotation(Vector3.up, normal.normalized));
            DrawCoverage(Mathf.Clamp01(progress));
            if (!_root.activeSelf) _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
        }

        private void DrawCoverage(float progress)
        {
            _vertices.Clear();
            _colors.Clear();
            _triangles.Clear();
            float radius = GuardWaveMudPatch.BrushRadius;
            AddFan(radius, 1f, 0f, new Color(1f, 0.68f, 0.18f, 0.22f));
            if (progress > 0f)
                AddFan(radius * 0.96f, progress, 0.002f,
                    new Color(1f, 0.86f, 0.36f, 0.62f));
            AddRim(radius, radius * 0.93f, 0.004f,
                new Color(1f, 0.76f, 0.25f, 0.80f));

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }

        private void AddFan(float radius, float portion, float elevation, Color color)
        {
            int start = _vertices.Count;
            _vertices.Add(new Vector3(0f, elevation, 0f));
            _colors.Add(color);
            int count = Mathf.Max(1, Mathf.CeilToInt(Segments * portion));
            for (int i = 0; i <= count; i++)
            {
                float angle = Mathf.PI * 2f * portion * i / count;
                _vertices.Add(new Vector3(Mathf.Cos(angle) * radius,
                    elevation, Mathf.Sin(angle) * radius));
                _colors.Add(color);
                if (i == 0) continue;
                // Counter-clockwise when viewed along the surface normal.
                _triangles.Add(start);
                _triangles.Add(start + i + 1);
                _triangles.Add(start + i);
            }
        }

        private void AddRim(float outer, float inner, float elevation, Color color)
        {
            int start = _vertices.Count;
            for (int i = 0; i <= Segments; i++)
            {
                float angle = Mathf.PI * 2f * i / Segments;
                float c = Mathf.Cos(angle);
                float s = Mathf.Sin(angle);
                _vertices.Add(new Vector3(c * outer, elevation, s * outer));
                _vertices.Add(new Vector3(c * inner, elevation, s * inner));
                _colors.Add(color);
                _colors.Add(color);
                if (i == 0) continue;
                int current = start + i * 2;
                _triangles.Add(current - 2);
                _triangles.Add(current - 1);
                _triangles.Add(current);
                _triangles.Add(current);
                _triangles.Add(current - 1);
                _triangles.Add(current + 1);
            }
        }

        public void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
            if (_mesh != null) Object.Destroy(_mesh);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
