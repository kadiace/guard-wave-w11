using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>Forest 씬에 배치된 수면 렌더러의 메시를 매 물리 틱 갱신한다.</summary>
    internal sealed class GuardWaveFloodEffect
    {
        private const float MINIMUM_VISIBLE_DEPTH = 0.03f;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();
        private readonly GameObject _water;
        private readonly MeshFilter _filter;
        private readonly Mesh _mesh;

        /// <summary>씬에 미리 배치된 비활성 수면에 런타임 갱신용 메시 데이터만 연결한다.</summary>
        public GuardWaveFloodEffect(MeshFilter sceneWater)
        {
            _filter = sceneWater;
            _water = sceneWater.gameObject;
            _mesh = new Mesh { name = "Stage Flood Surface", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _filter.sharedMesh = _mesh;
            _water.SetActive(false);
        }

        /// <summary>파도 시작 시 기존 씬 오브젝트를 활성화한다.</summary>
        public void Begin(GuardWaveFloodFlow flow)
        {
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

        /// <summary>스테이지 종료 또는 재시작 시 씬 수면만 숨긴다.</summary>
        public void Hide()
        {
            _water.SetActive(false);
        }

        /// <summary>동적 메시 데이터만 해제하고 씬에 배치된 오브젝트는 유지한다.</summary>
        public void Dispose()
        {
            _water.SetActive(false);
            _filter.sharedMesh = null;
            Object.Destroy(_mesh);
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

    }
}
