using UnityEngine;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>
    /// 실제 낙하 시작점과 낙하 고스트의 스크린 좌표를 Canvas상의 점선으로 연결한다.
    /// 각각의 대시를 하나의 UGUI Graphic 메시로 배치하여 레이캐스트나 물리에 영향이 없다.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class GuardWaveDropGuideGraphic : MaskableGraphic
    {
        private const float DashLength = 12f;
        private const float DashGap = 9f;
        private const float LineThickness = 3f;
        private const int MaxDashes = 256;

        private Vector2 _start;
        private Vector2 _end;
        private bool _hasEndpoints;

        /// <summary>화면 좌표를 현재 풀스크린 RectTransform의 로컬 좌표로 변환한다.</summary>
        public void SetScreenEndpoints(Vector2 launchScreen, Vector2 landingScreen)
        {
            RectTransform rect = rectTransform;
            Canvas canvas = GetComponentInParent<Canvas>();
            Camera eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;

            bool startOk = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rect, launchScreen, eventCamera, out Vector2 start);
            bool endOk = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rect, landingScreen, eventCamera, out Vector2 end);
            _hasEndpoints = startOk && endOk;
            if (!_hasEndpoints) { SetVerticesDirty(); return; }

            if ((_start - start).sqrMagnitude < 0.01f &&
                (_end - end).sqrMagnitude < 0.01f)
                return;
            _start = start;
            _end = end;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!_hasEndpoints) return;

            Vector2 delta = _end - _start;
            float distance = delta.magnitude;
            if (distance <= 0.01f) return;

            Vector2 direction = delta / distance;
            Vector2 normal = new Vector2(-direction.y, direction.x) * (LineThickness * 0.5f);
            Color32 tint = color;
            float spacing = DashLength + DashGap;
            int count = Mathf.Min(MaxDashes, Mathf.CeilToInt(distance / spacing));

            for (int i = 0; i < count; i++)
            {
                float from = i * spacing;
                if (from >= distance) break;
                float to = Mathf.Min(from + DashLength, distance);

                Vector2 a = _start + direction * from;
                Vector2 b = _start + direction * to;
                int index = vh.currentVertCount;
                UIVertex v = UIVertex.simpleVert;
                v.color = tint;
                v.position = a - normal; vh.AddVert(v);
                v.position = a + normal; vh.AddVert(v);
                v.position = b + normal; vh.AddVert(v);
                v.position = b - normal; vh.AddVert(v);
                vh.AddTriangle(index, index + 1, index + 2);
                vh.AddTriangle(index, index + 2, index + 3);
            }
        }
    }
}
