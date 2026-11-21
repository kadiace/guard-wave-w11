using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>Displays a filled or outlined Flood2 clear star and its hover condition.</summary>
    [RequireComponent(typeof(Image))]
    public sealed class Flood2ClearStar : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const int STAR_TEXTURE_SIZE = 96;
        private const float OUTLINE_WIDTH = 5.5f;

        private static Sprite _filledSprite;
        private static Sprite _outlinedSprite;

        private Image _image;
        private Text _hint;
        private int _budgetLimit;

        /// <summary>Connects the shared hint and the minimum remaining budget required for this star.</summary>
        public void Initialize(Text hint, int budgetLimit)
        {
            _hint = hint;
            _budgetLimit = budgetLimit;
            _image = GetComponent<Image>();
            _image.type = Image.Type.Simple;
            _image.preserveAspect = true;
            _image.raycastTarget = true;
            _image.sprite = GetStarSprite(false);
            _image.color = new Color(0.76f, 0.79f, 0.83f, 1f);
        }

        /// <summary>Shows the earned star or an empty outline based on remaining budget.</summary>
        public void ShowResult(int budget)
        {
            bool earned = _budgetLimit < 0 || budget >= _budgetLimit;
            _image.sprite = GetStarSprite(earned);
            _image.color = earned
                ? new Color(1f, 0.8f, 0.25f, 1f)
                : new Color(0.76f, 0.79f, 0.83f, 1f);
        }

        /// <summary>Shows this star's rule even when it has not been earned.</summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            _hint.text = _budgetLimit < 0
                ? "스테이지 클리어"
                : $"클리어 시 남은 예산 ${_budgetLimit} 이상";
        }

        /// <summary>Hides this star's rule when the pointer leaves its icon.</summary>
        public void OnPointerExit(PointerEventData eventData)
        {
            _hint.text = "";
        }

        /// <summary>Shares two generated star sprites across all three standard uGUI images.</summary>
        private static Sprite GetStarSprite(bool filled)
        {
            if (filled)
                return _filledSprite != null ? _filledSprite : _filledSprite = CreateStarSprite(true);
            return _outlinedSprite != null ? _outlinedSprite : _outlinedSprite = CreateStarSprite(false);
        }

        /// <summary>Rasterizes a five-point star, using its edge distance for antialiased fill or outline.</summary>
        private static Sprite CreateStarSprite(bool filled)
        {
            const int corners = 10;
            Vector2 center = Vector2.one * (STAR_TEXTURE_SIZE * 0.5f);
            float radius = STAR_TEXTURE_SIZE * 0.45f;
            Vector2[] vertices = new Vector2[corners];
            for (int i = 0; i < corners; i++)
            {
                float angle = Mathf.PI * (0.5f - i / 5f);
                float cornerRadius = i % 2 == 0 ? radius : radius * 0.46f;
                vertices[i] = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * cornerRadius;
            }

            Color32[] pixels = new Color32[STAR_TEXTURE_SIZE * STAR_TEXTURE_SIZE];
            for (int y = 0; y < STAR_TEXTURE_SIZE; y++)
            for (int x = 0; x < STAR_TEXTURE_SIZE; x++)
            {
                Vector2 point = new Vector2(x + 0.5f, y + 0.5f);
                bool inside = false;
                float closestEdge = float.MaxValue;
                for (int i = 0, previous = corners - 1; i < corners; previous = i++)
                {
                    Vector2 a = vertices[previous];
                    Vector2 b = vertices[i];
                    if ((a.y > point.y) != (b.y > point.y) &&
                        point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                        inside = !inside;

                    Vector2 edge = b - a;
                    Vector2 closest = a + edge * Mathf.Clamp01(
                        Vector2.Dot(point - a, edge) / edge.sqrMagnitude);
                    closestEdge = Mathf.Min(closestEdge, Vector2.Distance(point, closest));
                }

                float outerCoverage = Mathf.Clamp01(
                    0.5f + (inside ? closestEdge : -closestEdge));
                float alpha = filled ? outerCoverage
                    : outerCoverage * Mathf.Clamp01(OUTLINE_WIDTH + 0.5f - closestEdge);
                pixels[y * STAR_TEXTURE_SIZE + x] =
                    new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }

            Texture2D texture = new Texture2D(
                STAR_TEXTURE_SIZE, STAR_TEXTURE_SIZE, TextureFormat.RGBA32, false);
            texture.name = filled ? "Flood2 Star Filled" : "Flood2 Star Outline";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0f, 0f, STAR_TEXTURE_SIZE, STAR_TEXTURE_SIZE),
                new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
