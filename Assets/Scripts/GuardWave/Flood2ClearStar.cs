using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>Draws one clear reward and displays its rule when the pointer enters the star.</summary>
    public sealed class Flood2ClearStar : MaskableGraphic, IPointerEnterHandler, IPointerExitHandler
    {
        private Text _hint;
        private int _budgetLimit;
        private bool _earned;

        /// <summary>Sets the budget threshold. A negative threshold awards the star for clearing.</summary>
        public void Initialize(Text hint, int budgetLimit)
        {
            _hint = hint;
            _budgetLimit = budgetLimit;
        }

        /// <summary>Fills or outlines the star using the budget at clearance.</summary>
        public void ShowResult(int budget)
        {
            _earned = _budgetLimit < 0 || budget <= _budgetLimit;
            color = _earned ? new Color(1f, 0.8f, 0.25f) : new Color(0.76f, 0.79f, 0.83f);
            SetVerticesDirty();
        }

        /// <summary>Shows this star's condition for both filled and empty stars.</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            _hint.text = _budgetLimit < 0 ? "스테이지 클리어" : $"클리어 시 남은 예산 ${_budgetLimit} 이하";
        }

        /// <summary>Clears the condition when the pointer leaves the star.</summary>
        public void OnPointerExit(PointerEventData eventData) => _hint.text = "";

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = rectTransform.rect;
            float radius = Mathf.Min(rect.width, rect.height) * 0.46f;
            mesh.AddVert(rect.center, color, Vector2.zero);
            for (int i = 0; i < 10; i++)
            {
                float angle = Mathf.PI * (0.5f - i / 5f);
                Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) *
                    (i % 2 == 0 ? radius : radius * 0.46f);
                mesh.AddVert(rect.center + offset, color, Vector2.zero);
                mesh.AddVert(rect.center + offset * 0.78f, color, Vector2.zero);
            }
            for (int i = 0; i < 10; i++)
            {
                int a = 1 + i * 2;
                int b = 1 + ((i + 1) % 10) * 2;
                if (_earned) mesh.AddTriangle(0, a, b);
                else
                {
                    mesh.AddTriangle(a, b, a + 1);
                    mesh.AddTriangle(a + 1, b, b + 1);
                }
            }
        }
    }
}
