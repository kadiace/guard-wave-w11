using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace GuardWave
{
    public enum GuardWaveChangeKind { Destroyed, Flooded, EffectEnabled, EffectDisabled, Growth, Created }

    public readonly struct GuardWaveChange
    {
        public readonly GuardWavePiece Target;
        public readonly GuardWavePiece Source;
        public readonly GuardWaveChangeKind Kind;
        public readonly int Amount;
        public readonly Vector3 Position;
        public readonly string Label;

        /// <summary>확정된 변화의 대상, 원인, 변화량 및 독립적인 표시 위치를 저장한다.</summary>
        public GuardWaveChange(GuardWavePiece target, GuardWavePiece source, GuardWaveChangeKind kind,
            int amount, Vector3 position, string label = null)
        {
            Target = target;
            Source = source;
            Kind = kind;
            Amount = amount;
            Position = position;
            Label = label;
        }
    }

    /// <summary>확정된 게임 이벤트를 카메라 투영 TMP 레이블로 표현하고 비활성 레이블을 재사용한다.</summary>
    public sealed class GuardWaveFloatingText : MonoBehaviour
    {
        [Header("Colors")]
        [SerializeField] private Color increaseColor = new Color32(255, 100, 110, 255);
        [SerializeField] private Color decreaseColor = new Color32(89, 191, 255, 255);
        [SerializeField] private Color destructionColor = new Color32(213, 138, 255, 255);
        [SerializeField] private Color floodColor = new Color32(40, 220, 200, 255);
        [SerializeField] private Color effectColor = new Color32(255, 209, 106, 255);

        [Header("Animation")]
        [SerializeField, Min(10f)] private float fontSize = 32f;
        [SerializeField, Min(0.1f)] private float duration = 1.2f;
        [SerializeField, Min(0f)] private float riseSpeed = 0.85f;
        [SerializeField, Min(0f)] private float spread = 0.35f;

        private readonly List<Entry> _active = new List<Entry>();
        private readonly Stack<Entry> _available = new Stack<Entry>();
        private Camera _camera;
        private RectTransform _canvasRect;
        private TMP_FontAsset _font;
        private int _sequence;

        private sealed class Entry
        {
            public TextMeshProUGUI Text;
            public Vector3 Origin;
            public Vector3 Offset;
            public Color Color;
            public GuardWaveChangeKind Kind;
            public float Started;
        }

        /// <summary>게임 카메라와 화면 오버레이를 연결해 레이블이 기물 제거와 무관하게 유지되도록 한다.</summary>
        public void Initialize(Camera camera, RectTransform canvasRect)
        {
            _camera = camera;
            _canvasRect = canvasRect;
            _font = TMP_FontAsset.CreateFontAsset(Resources.Load<Font>("Fonts & Materials/NotoSansKR"));
            _font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
        }

        /// <summary>변화별 독립적인 레이블을 풀에서 꺼내 위치와 메시지를 설정한다.</summary>
        public void Show(GuardWaveChange change)
        {
            Entry entry;
            if (_available.Count > 0)
            {
                entry = _available.Pop();
                entry.Text.gameObject.SetActive(true);
            }
            else
            {
                GameObject label = new GameObject("Floating Change", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                label.transform.SetParent(_canvasRect, false);
                TextMeshProUGUI text = label.GetComponent<TextMeshProUGUI>();
                text.font = _font;
                text.alignment = TextAlignmentOptions.Center;
                text.raycastTarget = false;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.outlineWidth = 0.22f;
                text.outlineColor = Color.black;
                RectTransform rect = (RectTransform)label.transform;
                rect.sizeDelta = new Vector2(310f, 70f);
                entry = new Entry { Text = text };
            }

            entry.Kind = change.Kind;
            entry.Origin = change.Position + Vector3.up * 0.6f;
            int slot = _sequence++;
            entry.Offset = new Vector3(((slot % 7) - 3) * spread * 0.35f,
                (slot % 5) * spread * 0.35f, 0f);
            entry.Started = Time.unscaledTime;
            entry.Text.text = change.Label ?? (change.Kind switch
                {
                    GuardWaveChangeKind.Destroyed => "파괴!",
                    GuardWaveChangeKind.Flooded => "침수!",
                    GuardWaveChangeKind.EffectEnabled => "효과 발동!",
                    GuardWaveChangeKind.EffectDisabled => "효과 해제!",
                    GuardWaveChangeKind.Growth => "성장!",
                    GuardWaveChangeKind.Created => "생성!",
                    _ => "획득!"
                });
            entry.Color = change.Kind == GuardWaveChangeKind.Destroyed ? destructionColor :
                change.Kind == GuardWaveChangeKind.Flooded ? floodColor : effectColor;
            entry.Text.fontSize = fontSize * (change.Kind == GuardWaveChangeKind.Destroyed ||
                change.Kind == GuardWaveChangeKind.Flooded ? 1.3f : 1f);
            entry.Text.transform.SetAsLastSibling();
            _active.Add(entry);
            Animate(entry, 0f);
        }

        private void LateUpdate()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Entry entry = _active[i];
                float progress = (Time.unscaledTime - entry.Started) / duration;
                if (progress >= 1f)
                {
                    entry.Text.gameObject.SetActive(false);
                    _available.Push(entry);
                    _active.RemoveAt(i);
                }
                else Animate(entry, progress);
            }
        }

        /// <summary>월드 좌표를 매 프레임 재투영하고 사건별 등장 모션과 페이드아웃을 적용한다.</summary>
        private void Animate(Entry entry, float progress)
        {
            Vector3 position = entry.Origin + entry.Offset + Vector3.up * riseSpeed * duration * progress;
            if (entry.Kind == GuardWaveChangeKind.Flooded)
                position += Vector3.down * 0.35f * (1f - Mathf.Clamp01(progress * 4f));
            Vector3 screen = _camera.WorldToScreenPoint(position);
            entry.Text.enabled = screen.z > 0f;
            if (screen.z <= 0f) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null,
                out Vector2 local);
            entry.Text.rectTransform.anchoredPosition = local;
            float pop = entry.Kind == GuardWaveChangeKind.Destroyed ? 0.55f : 0.22f;
            entry.Text.rectTransform.localScale = Vector3.one * (1f + pop *
                (1f - Mathf.Clamp01(progress * 6f)));
            Color color = entry.Color;
            color.a *= Mathf.Clamp01((1f - progress) * 2.5f);
            entry.Text.color = color;
        }
    }
}
