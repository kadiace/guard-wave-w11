using UnityEngine;

namespace Avalanche
{
    /// <summary>Resolution-independent prototype HUD, tools and always-visible citizen observations.</summary>
    public sealed class AvalancheHud : MonoBehaviour
    {
        private static readonly Color INK = new Color(0.055f, 0.1f, 0.14f, 0.96f);
        private static readonly Color PAPER = new Color(0.93f, 0.96f, 0.91f);
        private static readonly Color MUTED = new Color(0.62f, 0.74f, 0.75f);
        private static readonly Color AQUA = new Color(0.27f, 0.86f, 0.75f);
        private static readonly Color GOLD = new Color(1f, 0.75f, 0.33f);
        private AvalancheStage _stage;
        private GUIStyle _text;
        private GUIStyle _button;
        private GUIStyle _title;
        private bool _stylesReady;

        /// <summary>Connects the HUD to this scene's stage context.</summary>
        public void Initialize(AvalancheStage stage) => _stage = stage;

        /// <summary>Matches visible HUD panels so clicks cannot place materials through menus.</summary>


        /// <summary>Prevents camera gestures from interacting through visible HUD panels.</summary>
        public bool BlocksPointer(Vector2 screen)
        {
            if (_stage == null) return true;
            if (_stage.Phase == AvalanchePhase.Introduction ||
                _stage.IsFinished() || _stage.Paused) return true;
            float x = screen.x * 1600f / Screen.width;
            float y = (Screen.height - screen.y) * 900f / Screen.height;
            return y < 104f || y > 752f ||
                (x < 294f && y < 665f) || (x > 1306f && y < 452f);
        }

        /// <summary>Initializes GUI styles once with the project's Korean font.</summary>
        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _text = new GUIStyle(GUI.skin.label) { font = _stage.Font, fontSize = 19, wordWrap = true, richText = false, padding = new RectOffset(0, 0, 0, 0) };
            _text.normal.textColor = PAPER;
            _title = new GUIStyle(_text) { fontSize = 30, fontStyle = FontStyle.Bold };
            _button = new GUIStyle(GUI.skin.button) { font = _stage.Font, fontSize = 19, alignment = TextAnchor.MiddleCenter, wordWrap = true, border = new RectOffset(0, 0, 0, 0) };
            _button.normal.background = Texture2D.whiteTexture;
            _button.hover.background = Texture2D.whiteTexture;
            _button.active.background = Texture2D.whiteTexture;
            _button.normal.textColor = _button.hover.textColor = _button.active.textColor = INK;
            _stylesReady = true;
        }

        /// <summary>Draws a solid rectangle and restores the caller's GUI tint.</summary>
        private static void Fill(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>Draws a reusable label with explicit size and colour.</summary>
        private void Label(float x, float y, float w, float h, string text, Color color, int size = 19)
        {
            _text.fontSize = size;
            _text.normal.textColor = color;
            GUI.Label(new Rect(x, y, w, h), text, _text);
        }

        /// <summary>Draws a tinted actionable button without affecting subsequent GUI controls.</summary>
        private bool Button(Rect rect, string label, Color color)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = color;
            bool clicked = GUI.Button(rect, label, _button);
            GUI.backgroundColor = old;
            return clicked;
        }

        private void OnGUI()
        {
            if (_stage == null) return;
            EnsureStyles();
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Screen.width / 1600f, Screen.height / 900f, 1f));
            DrawHeader();
            DrawCitizens();
            DrawFloodStatus();
            DrawTools();
            DrawWorldMarkers();
            if (_stage.Phase == AvalanchePhase.Introduction) DrawIntroduction();
            else if (_stage.IsFinished()) DrawResult();
            else if (_stage.Paused) DrawPause();
            GUI.matrix = previous;
        }

        /// <summary>Shows the objective, phase and countdown with a persistent pause control.</summary>
        private void DrawHeader()
        {
            Fill(new Rect(0, 0, 1600, 94), INK);
            Fill(new Rect(24, 22, 5, 50), AQUA);
            Label(44, 15, 330, 25, "FLOOD / 02     RIVERSIDE", AQUA, 16);
            GUI.Label(new Rect(44, 40, 440, 44), "마을을 지키는 물길", _title);
            Label(550, 20, 470, 30, "목표  ·  시민 6명 모두 보호", PAPER, 23);
            string phase = _stage.Phase switch
            {
                AvalanchePhase.Introduction => "두 차례 홍수에 대비하여 시민을 보호하세요",
                AvalanchePhase.Preparing => _stage.LimitPreparationTime
                    ? $"준비 {Mathf.CeilToInt(_stage.PreparationLeft)}초  ·  0초가 되면 홍수가 시작됩니다"
                    : "준비 중  ·  팀 지정을 마치면 준비 완료를 누르세요",
                AvalanchePhase.Warning => $"홍수 경보  ·  {Mathf.CeilToInt(_stage.WarningLeft)}초 후 유입",
                AvalanchePhase.Flooding => $"홍수 진행  {_stage.FloodElapsed:0.0}초  ·  물길과 대피 상황 관찰",
                AvalanchePhase.Assessing => "유입 종료  ·  하류 도달과 잔류 침수 관찰 중",
                AvalanchePhase.Cleared => "시민 전원 보호 완료",
                AvalanchePhase.TechnicalError => "계산 오류  ·  다시 시도할 수 있습니다",
                _ => "보호 실패  ·  팀 지정을 변경하고 재도전하세요"
            };
            Label(550, 54, 680, 28, phase, GOLD, 18);
            Label(1220, 29, 170, 32, $"시도 {_stage.Attempt:00}", MUTED, 19);
            if (Button(new Rect(1410, 22, 164, 50), _stage.Paused ? "계속하기" : "일시정지", PAPER)) _stage.TogglePause();
        }

        /// <summary>Displays citizen risk and tools for assigning the two village crews.</summary>
        private void DrawCitizens()
        {
            var response = _stage.Response;
            Fill(new Rect(24, 116, 258, 539), INK);
            Label(42, 128, 225, 42, "대피팀 1개 · 배수팀 1개", AQUA, 19);
            for (int zone = 0; zone < 3; zone++)
            {
                float y = 170 + zone * 151;
                AvalancheCitizen first = _stage.Citizens[zone * 2];
                if (Button(new Rect(40, y, 226, 32), first.Zone + "   ↗", new Color(0.73f, 0.83f, 0.8f)))
                    _stage.ViewCamera.GetComponent<AvalancheCamera>().Focus(first.transform.position);
                for (int j = 0; j < 2; j++)
                {
                    AvalancheCitizen citizen = _stage.Citizens[zone * 2 + j];
                    Color color = CitizenColor(citizen);
                    float row = y + 35 + j * 24;
                    Label(42, row, 104, 24, citizen.CitizenId, PAPER, 16);
                    Fill(new Rect(111, row + 9, 106, 8), new Color(0.2f, 0.29f, 0.32f));
                    Fill(new Rect(111, row + 9, 106 * citizen.Exposure / 100f, 8), color);
                    Label(223, row - 1, 44, 24, $"{citizen.Exposure:0}", color, 17);
                }
                Label(42, y + 84, 226, 24, response.Status(zone), response.GuideZone == zone || response.DrainZone == zone ? AQUA : MUTED, 15);
                GUI.enabled = _stage.CanEdit;
                if (Button(new Rect(40, y + 109, 110, 30), "대피 지정", response.GuideZone == zone ? AQUA : PAPER)) response.AssignGuide(zone);
                if (Button(new Rect(155, y + 109, 111, 30), "배수 정비", response.DrainZone == zone ? AQUA : PAPER)) response.AssignDrain(zone);
                GUI.enabled = true;
            }
            if (Button(new Rect(40, 618, 226, 25), "전체 마을 보기 · Home", AQUA)) _stage.ViewCamera.GetComponent<AvalancheCamera>().Overview();
        }

        /// <summary>Shows the upstream input sequence separately from water arrival at the citizens.</summary>
        private void DrawFloodStatus()
        {
            Fill(new Rect(1318, 116, 258, 325), INK);
            Label(1336, 132, 220, 30, "홍수 예보", AQUA, 21);
            Label(1336, 176, 220, 48, "북쪽 유입 → 분기 수로\n남쪽 열린 배수구", PAPER, 18);
            string[] rows =
            {
                $"00–{_stage.FirstWaveEnd:00.##}초   첫 수위 상승",
                $"{_stage.FirstWaveEnd:00.##}–{_stage.IntermissionEnd:00.##}초   잠시 완화",
                $"{_stage.IntermissionEnd:00.##}–{_stage.SecondWaveEnd:00.##}초   두 번째 큰 홍수",
                $"{_stage.SecondWaveEnd:00.##}–{_stage.InflowEnd:00.##}초   유입 감소·종료"
            };
            for (int i = 0; i < rows.Length; i++) Label(1336, 240 + i * 31, 232, 26, rows[i], i == 2 ? GOLD : MUTED, 17);
            Label(1336, 373, 225, 56, "시민에게 도착하는 시각은\n물길의 흐름에 따라 달라집니다.", MUTED, 15);
        }

        /// <summary>Draws stock, tool selection, undo and the shared Ready action.</summary>
        private void DrawTools()
        {
            Fill(new Rect(0, 764, 1600, 136), INK);
            Label(24, 780, 1100, 50, "대피팀과 배수팀을 배정한 뒤 재해를 시작하세요.", GOLD, 22);
            GUI.enabled = _stage.CanEdit;
            if (Button(new Rect(1160, 793, 410, 72), "준비 완료  →  홍수 시작", GOLD))
                _stage.Ready();
            GUI.enabled = true;
            Label(306, 720, 990, 27,
                "WASD 이동  ·  휠 확대  ·  우클릭 드래그 회전  ·  중클릭 드래그 이동  ·  Esc 일시정지",
                INK, 16);
        }

        /// <summary>Returns a legible citizen status tint.</summary>
        private static Color CitizenColor(AvalancheCitizen citizen) => citizen.State == AvalancheCitizenState.Critical || citizen.State == AvalancheCitizenState.RescueRequired
            ? new Color(1f, 0.36f, 0.29f) : citizen.State == AvalancheCitizenState.Warning ? GOLD : AQUA;

        /// <summary>Overlays observations above each zone, even when structures obscure the figures.</summary>
        private void DrawWorldMarkers()
        {
            for (int i = 0; i < _stage.Citizens.Length; i += 2)
            {
                AvalancheCitizen first = _stage.Citizens[i];
                AvalancheCitizen second = _stage.Citizens[i + 1];
                AvalancheCitizen citizen = second.Exposure > first.Exposure || second.Depth > first.Depth ? second : first;
                Vector3 screen = _stage.ViewCamera.WorldToScreenPoint(first.transform.position + new Vector3(0.65f, 3f, 0f));
                if (screen.z <= 0f) continue;
                float x = screen.x / Screen.width * 1600f - 85f;
                float y = (1f - screen.y / Screen.height) * 900f - (i == 2 ? 94f : 75f);
                if (x < 294f || x + 172f > 1310f || y < 104f || y > 660f) continue;
                Fill(new Rect(x, y, 172, 58), INK);
                Label(x + 9, y + 5, 155, 26, citizen.Zone, CitizenColor(citizen), 17);
                Label(x + 9, y + 31, 155, 23, $"{citizen.Depth:0.00}m  /  {citizen.Speed:0.0}m/s", PAPER, 15);
            }
        }

        /// <summary>Draws a modal frame shared by the tutorial and attempt results.</summary>
        private void Modal(string eyebrow, string title)
        {
            Fill(new Rect(0, 94, 1600, 670), new Color(0.025f, 0.05f, 0.08f, 0.62f));
            Fill(new Rect(430, 159, 740, 540), INK);
            Fill(new Rect(430, 159, 740, 5), AQUA);
            Label(470, 191, 660, 28, eyebrow, AQUA, 16);
            GUI.Label(new Rect(470, 231, 660, 52), title, _title);
        }

        /// <summary>Introduces all essential rules before starting any timer.</summary>
        private void DrawIntroduction()
        {
            Modal("마을 홍수 대응", "대피할 길과 물이 빠질 길을 확보하세요.");
            string preparation = _stage.LimitPreparationTime
                ? $"{_stage.PreparationSeconds:0.##}초 동안"
                : "준비 단계에서";
            Label(470, 301, 660, 220,
                $"시장 · 광장 · 정류장에 시민 두 명씩이 있습니다.\n\n{preparation} 대피팀과 배수팀을 각각 한 구역에 지정하세요. 대피팀은 경보가 울리면 시민을 높은 대피대로 안내합니다.\n\n배수팀은 선택한 배수구를 정비합니다. 초록색 대피 통로와 파란색 배수구를 관찰하세요.", PAPER, 21);
            Label(470, 538, 660, 60, "팀 지정은 홍수 경보가 시작되기 전에 끝내세요.", GOLD, 19);
            if (Button(new Rect(470, 620, 660, 52), "마을 지키기 시작   ·   Enter", AQUA)) _stage.BeginPreparation();
        }

        /// <summary>Shows the observed result and restoration or empty-layout choices.</summary>
        private void DrawResult()
        {
            Modal("시도 결과", _stage.Phase == AvalanchePhase.Cleared
                ? "여섯 시민을 지켰습니다." : _stage.Phase == AvalanchePhase.TechnicalError
                    ? "다시 시도할 수 있습니다." : "시민을 보호하지 못했습니다.");
            Label(470, 304, 660, 160, _stage.ResultMessage, PAPER, 22);
            var response = _stage.Response;
            Label(470, 483, 660, 58,
                $"대피대 도착 {response.ShelteredCount}명 · 하류로 배수한 물 {response.DrainedVolume:0.0}m³",
                MUTED, 19);
            if (Button(new Rect(470, 560, 660, 52), "처음부터 다시 시도", AQUA))
                _stage.Retry(false);
            if (Button(new Rect(470, 628, 660, 43), "스테이지 선택으로", PAPER))
                _stage.ReturnToMenu();
        }

        /// <summary>Pauses all simulation and provides a safe return path.</summary>
        private void DrawPause()
        {
            Modal("일시정지", "마을과 시간을 잠시 멈췄습니다.");
            Label(470, 309, 660, 90,
                "물의 이동과 시민 위험도, 기물의 움직임이 함께 정지합니다.", PAPER, 23);
            if (Button(new Rect(470, 442, 660, 56), "계속하기 · Esc", AQUA))
                _stage.TogglePause();
            if (Button(new Rect(470, 522, 660, 56), "처음부터 다시 시도", PAPER))
                _stage.Retry(false);
            if (Button(new Rect(470, 602, 660, 56), "스테이지 선택으로", PAPER))
                _stage.ReturnToMenu();
        }
    }
}
