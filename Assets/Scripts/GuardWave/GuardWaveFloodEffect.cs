using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>물 격자의 현재 수심을 반투명 수면과 노출된 물의 측면 메시로 표시한다.</summary>
    internal sealed class GuardWaveFloodEffect
    {
        private const float MINIMUM_VISIBLE_DEPTH = 0.03f;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();
        private readonly List<GameObject> _shallowSurfaces = new List<GameObject>();
        private readonly List<Mesh> _shallowMeshes = new List<Mesh>();
        private GameObject _water;
        private Mesh _mesh;
        private Material _material;

        /// <summary>위아래 강바닥의 얕은 수면과 절벽 앞의 낙수면을 게임 시작부터 표시한다.</summary>
        public void ShowShallow(Bounds upperBed, Bounds lowerBed, float upperY, float lowerY)
        {
            EnsureWater();
            if (_shallowSurfaces.Count > 0) return;
            CreateSurface("Upper River Water", new[]
            {
                new Vector3(upperBed.min.x, upperY, upperBed.min.z),
                new Vector3(upperBed.min.x, upperY, upperBed.max.z),
                new Vector3(upperBed.max.x, upperY, upperBed.max.z),
                new Vector3(upperBed.max.x, upperY, upperBed.min.z)
            });
            CreateSurface("Lower River Water", new[]
            {
                new Vector3(lowerBed.min.x, lowerY, lowerBed.min.z),
                new Vector3(lowerBed.min.x, lowerY, lowerBed.max.z),
                new Vector3(lowerBed.max.x, lowerY, lowerBed.max.z),
                new Vector3(lowerBed.max.x, lowerY, lowerBed.min.z)
            });
            CreateSurface("Creek Cliff Waterfall", new[]
            {
                new Vector3(upperBed.min.x, upperY, upperBed.min.z - 0.1f),
                new Vector3(upperBed.min.x, lowerY, lowerBed.max.z - 0.25f),
                new Vector3(upperBed.max.x, lowerY, lowerBed.max.z - 0.25f),
                new Vector3(upperBed.max.x, upperY, upperBed.min.z - 0.1f)
            });
        }

        /// <summary>네 꼭짓점으로 고정된 물 표면을 만들고 종료 시 해제할 메시를 보관한다.</summary>
        private void CreateSurface(string name, Vector3[] corners)
        {
            GameObject surface = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            surface.layer = 2;
            Mesh mesh = new Mesh { name = name + " Mesh" };
            mesh.vertices = corners;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = surface.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _shallowSurfaces.Add(surface);
            _shallowMeshes.Add(mesh);
        }

        /// <summary>한 스테이지의 물 표시를 활성화하고 현재 격자 상태로 메시를 채운다.</summary>
        public void Begin(GuardWaveFloodFlow flow)
        {
            EnsureWater();
            _water.SetActive(true);
            UpdateSurface(flow);
        }

        /// <summary>젖은 격자의 수면과 이웃보다 높은 물의 경계면을 메시로 갱신한다.</summary>
        public void UpdateSurface(GuardWaveFloodFlow flow)
        {
            _vertices.Clear();
            _triangles.Clear();
            for (int row = 0; row < flow.Rows; row++)
                for (int column = 0; column < flow.Columns; column++)
                {
                    float depth = flow.DepthAt(column, row);
                    if (depth < MINIMUM_VISIBLE_DEPTH) continue;
                    Vector2 center = flow.CenterAt(column, row);
                    float left = center.x - flow.CellX * 0.5f;
                    float right = center.x + flow.CellX * 0.5f;
                    float down = center.y - flow.CellZ * 0.5f;
                    float up = center.y + flow.CellZ * 0.5f;
                    float top = flow.GroundY + depth;
                    AddQuad(new Vector3(left, top, down), new Vector3(left, top, up),
                        new Vector3(right, top, up), new Vector3(right, top, down));

                    float leftDepth = column > 0 ? flow.DepthAt(column - 1, row) : 0f;
                    float rightDepth = column < flow.Columns - 1 ? flow.DepthAt(column + 1, row) : 0f;
                    float downDepth = row > 0 ? flow.DepthAt(column, row - 1) : 0f;
                    float upDepth = row < flow.Rows - 1 ? flow.DepthAt(column, row + 1) : 0f;
                    if (depth - leftDepth > MINIMUM_VISIBLE_DEPTH)
                        AddQuad(new Vector3(left, top, up), new Vector3(left, top, down),
                            new Vector3(left, flow.GroundY + leftDepth, down),
                            new Vector3(left, flow.GroundY + leftDepth, up));
                    if (depth - rightDepth > MINIMUM_VISIBLE_DEPTH)
                        AddQuad(new Vector3(right, top, down), new Vector3(right, top, up),
                            new Vector3(right, flow.GroundY + rightDepth, up),
                            new Vector3(right, flow.GroundY + rightDepth, down));
                    if (depth - downDepth > MINIMUM_VISIBLE_DEPTH)
                        AddQuad(new Vector3(left, top, down), new Vector3(right, top, down),
                            new Vector3(right, flow.GroundY + downDepth, down),
                            new Vector3(left, flow.GroundY + downDepth, down));
                    if (depth - upDepth > MINIMUM_VISIBLE_DEPTH)
                        AddQuad(new Vector3(right, top, up), new Vector3(left, top, up),
                            new Vector3(left, flow.GroundY + upDepth, up),
                            new Vector3(right, flow.GroundY + upDepth, up));
                }
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }

        /// <summary>스테이지 종료 또는 재시작 시 물 메시를 숨긴다.</summary>
        public void Hide()
        {
            if (_water != null) _water.SetActive(false);
        }

        /// <summary>런타임에 만든 메시와 머티리얼을 해제한다.</summary>
        public void Dispose()
        {
            foreach (GameObject surface in _shallowSurfaces) Object.Destroy(surface);
            foreach (Mesh mesh in _shallowMeshes) Object.Destroy(mesh);
            if (_water != null) Object.Destroy(_water);
            if (_mesh != null) Object.Destroy(_mesh);
            if (_material != null) Object.Destroy(_material);
            _shallowSurfaces.Clear();
            _shallowMeshes.Clear();
            _water = null;
            _mesh = null;
            _material = null;
        }

        /// <summary>네 꼭짓점으로 한 면을 메시 버퍼에 추가한다.</summary>
        private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            _triangles.Add(start);
            _triangles.Add(start + 1);
            _triangles.Add(start + 2);
            _triangles.Add(start);
            _triangles.Add(start + 2);
            _triangles.Add(start + 3);
        }

        /// <summary>물리 충돌이 없는 반투명 수면 오브젝트와 메시를 한 번 생성한다.</summary>
        private void EnsureWater()
        {
            if (_water != null) return;
            _water = new GameObject("Stage Flood Water", typeof(MeshFilter), typeof(MeshRenderer));
            _water.layer = 2;
            _mesh = new Mesh { name = "Stage Flood Surface", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _water.GetComponent<MeshFilter>().sharedMesh = _mesh;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            _material = new Material(shader);
            _material.name = "Stage Flood Transparent Blue";
            Color blue = new Color(0.08f, 0.43f, 0.98f, 0.43f);
            if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", blue);
            if (_material.HasProperty("_Color")) _material.SetColor("_Color", blue);
            if (_material.HasProperty("_Surface")) _material.SetFloat("_Surface", 1f);
            if (_material.HasProperty("_Blend")) _material.SetFloat("_Blend", 0f);
            if (_material.HasProperty("_SrcBlend"))
                _material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (_material.HasProperty("_DstBlend"))
                _material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (_material.HasProperty("_ZWrite")) _material.SetFloat("_ZWrite", 0f);
            if (_material.HasProperty("_Cull")) _material.SetFloat("_Cull", (float)CullMode.Off);
            _material.SetOverrideTag("RenderType", "Transparent");
            _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _material.DisableKeyword("_ALPHATEST_ON");
            _material.renderQueue = (int)RenderQueue.Transparent;

            MeshRenderer renderer = _water.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _water.SetActive(false);
        }
    }
}
