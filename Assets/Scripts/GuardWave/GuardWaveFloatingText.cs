using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace GuardWave
{
    public enum GuardWaveChangeKind { Destroyed, Flooded, EffectEnabled, EffectDisabled, Growth, Created, Removed }

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
        [SerializeField] private Color removalColor = new Color32(255, 177, 69, 255);
        [SerializeField] private Color floodColor = new Color32(40, 220, 200, 255);
        [SerializeField] private Color effectColor = new Color32(255, 209, 106, 255);

        [Header("Animation")]
        [SerializeField, Min(10f)] private float fontSize = 32f;
        [SerializeField, Min(0.1f)] private float duration = 1.2f;
        [SerializeField, Min(0f)] private float riseSpeed = 0.85f;
        [SerializeField, Min(0f)] private float spread = 0.35f;

        private readonly List<Entry> _active = new List<Entry>();
        private readonly Stack<Entry> _available = new Stack<Entry>();
        private const int MAX_VISIBLE_DESTRUCTION_LABELS = 8;
        // Warm up enough labels for the first collapse, avoiding runtime TMP construction.
        private const int PREWARMED_LABEL_COUNT = 12;
        private const string PREWARM_CHARACTERS =
            "파괴!제거!침수!효과 발동!효과 해제!성장!생성!획득!덩쿨 생성!";
        private Camera _camera;
        private RectTransform _canvasRect;
        private TMP_FontAsset _font;
        private Material _outlineMaterial;
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
            // Dynamic fonts only add glyphs on first use. Populate the atlas
            // before the first outflow event so "파괴!" and "제거!" never pay it.
            _font.TryAddCharacters(PREWARM_CHARACTERS, out _);

            // Match the old label's exact TMP outline path. The convenience
            // properties may configure more than just shader values, so derive
            // the shared preset from a label using those original setters.
            Entry first = CreateEntry();
            first.Text.outlineWidth = 0.22f;
            first.Text.outlineColor = Color.black;
            _outlineMaterial = new Material(first.Text.fontSharedMaterial);
            first.Text.fontSharedMaterial = _outlineMaterial;
            first.Text.UpdateMeshPadding();

            // Prebuild the TextMeshPro meshes and Canvas geometry on scene setup,
            // rather than when the first piece falls past the destruction line.
            List<Entry> warmup = new List<Entry>(PREWARMED_LABEL_COUNT);
            for (int i = 0; i < PREWARMED_LABEL_COUNT; i++)
            {
                Entry entry = i == 0 ? first : CreateEntry();
                entry.Text.fontSize = fontSize * 1.3f;
                entry.Text.text = PREWARM_CHARACTERS;
                entry.Text.color = Color.clear;
                entry.Text.ForceMeshUpdate();
                warmup.Add(entry);
            }
            Canvas.ForceUpdateCanvases();
            foreach (Entry entry in warmup)
            {
                entry.Text.gameObject.SetActive(false);
                _available.Push(entry);
            }
        }

        /// <summary>Builds a floating label using the shared font and material.</summary>
        private Entry CreateEntry()
        {
            GameObject label = new GameObject("Floating Change", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            label.transform.SetParent(_canvasRect, false);
            TextMeshProUGUI text = label.GetComponent<TextMeshProUGUI>();
            text.font = _font;
            if (_outlineMaterial != null)
            {
                text.fontSharedMaterial = _outlineMaterial;
                text.UpdateMeshPadding();
            }
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            RectTransform rect = (RectTransform)label.transform;
            rect.sizeDelta = new Vector2(310f, 70f);
            return new Entry { Text = text };
        }

        /// <summary>변화별 독립적인 레이블을 풀에서 꺼내 위치와 메시지를 설정한다.</summary>
        public void Show(GuardWaveChange change)
        {
            // A falling wall can cross the cliff in a single physics step.
            // Avoid creating hundreds of simultaneous TMP components and
            // regenerating their meshes/Canvas geometry for invisible clutter.
            if (change.Kind == GuardWaveChangeKind.Destroyed)
            {
                int visibleDestructions = 0;
                foreach (Entry active in _active)
                    if (active.Kind == GuardWaveChangeKind.Destroyed &&
                        ++visibleDestructions >= MAX_VISIBLE_DESTRUCTION_LABELS)
                        return;
            }
            Entry entry;
            if (_available.Count > 0)
            {
                entry = _available.Pop();
                entry.Text.gameObject.SetActive(true);
            }
            else
            {
                // More than the prewarmed capacity can still be displayed,
                // but all such labels share the same font atlas and material.
                entry = CreateEntry();
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
                    GuardWaveChangeKind.Removed => "제거!",
                    GuardWaveChangeKind.Flooded => "침수!",
                    GuardWaveChangeKind.EffectEnabled => "효과 발동!",
                    GuardWaveChangeKind.EffectDisabled => "효과 해제!",
                    GuardWaveChangeKind.Growth => "성장!",
                    GuardWaveChangeKind.Created => "생성!",
                    _ => "획득!"
                });
            entry.Color = change.Kind == GuardWaveChangeKind.Destroyed ? destructionColor :
                change.Kind == GuardWaveChangeKind.Removed ? removalColor :
                change.Kind == GuardWaveChangeKind.Flooded ? floodColor : effectColor;
            entry.Text.fontSize = fontSize * (change.Kind == GuardWaveChangeKind.Destroyed ||
                change.Kind == GuardWaveChangeKind.Removed ||
                change.Kind == GuardWaveChangeKind.Flooded ? 1.3f : 1f);
            entry.Text.transform.SetAsLastSibling();
            _active.Add(entry);
            Animate(entry, 0f);
        }

        private void OnDestroy()
        {
            if (_outlineMaterial != null) Destroy(_outlineMaterial);
            if (_font != null) Destroy(_font);
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
            float pop = entry.Kind == GuardWaveChangeKind.Destroyed ? 0.55f :
                entry.Kind == GuardWaveChangeKind.Removed ? 0.35f : 0.22f;
            entry.Text.rectTransform.localScale = Vector3.one * (1f + pop *
                (1f - Mathf.Clamp01(progress * 6f)));
            Color color = entry.Color;
            color.a *= Mathf.Clamp01((1f - progress) * 2.5f);
            entry.Text.color = color;
        }
    }
}
