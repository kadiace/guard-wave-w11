using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.EventSystems;

namespace RestoreCastle3D
{
    public sealed class CastleGame3D : MonoBehaviour
    {
        private const int ROUNDS_PER_STAGE = 5;
        private const int PLACEMENTS_PER_ROUND = 5;
        private const int STAGE_COUNT = 5;
        private const float FLOOD_DURATION_INCREASE_PER_STAGE = 0.2f;
        private const float FLOOD_DEPTH_INCREASE_PER_STAGE = 0.5f;
        private const float BARREL_EXPLOSION_RADIUS = 2.5f;
        private const float CAMERA_DRAG_THRESHOLD_PIXELS = 6f;
        private const float CAMERA_MIN_TARGET_Z = -15f;
        private const float CAMERA_MAX_TARGET_Z = 15f;
        private const float WINDOWS_SCROLL_UNITS_PER_TICK = 120f;
        private const float BOUNDARY_DESTRUCTION_DELAY = 1f;

        [Header("Barrel explosion")]
        [SerializeField, Min(0f)] private float barrelExplosionImpulse = 18f;
        [SerializeField, Min(0f)] private float barrelExplosionUpwardsModifier = 0.6f;

        [Header("3D drop")]
        [SerializeField, Min(2f)] private float dropHeight = 7f;
        [SerializeField, Range(0.05f, 0.5f)] private float dropClickCooldown = 0.1f;
        [SerializeField] private float groundY = -2.25f;

        [Header("Flood stage transition")]
        [SerializeField, Min(0.1f)] private float floodFillDuration = 1.2f;
        [SerializeField, Min(0.1f)] private float floodTravelDuration = 2.25f;
        [SerializeField, Min(0.05f)] private float shallowWaterDepth = 0.3f;
        [SerializeField, Min(0.1f)] private float floodWaterDepth = 2f;
        [SerializeField]
        private AnimationCurve floodFillCurve =
            AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private readonly List<CastlePiece3D> _pieces = new List<CastlePiece3D>();
        private readonly HashSet<CastlePiece3D> _pieceSet = new HashSet<CastlePiece3D>();
        private readonly HashSet<CastlePiece3D> _outlinedNeighbors = new HashSet<CastlePiece3D>();
        private readonly HashSet<string> _consumedContacts = new HashSet<string>();
        private readonly CastleMaterial3D[] _offers = new CastleMaterial3D[3];
        private ForestSceneContext _sceneContext;
        private CastleCatalog3D _catalog;
        private CastleHud3D _hud;
        private CastleDropPreview3D _dropPreview;
        private CastleFloodEffect3D _floodEffect;
        private Bounds _placementBounds;
        private CastleFloatingText3D _floatingText;
        private GameObject _defenseFlag;
        private Rigidbody _flagBody;
        private BoxCollider _flagCollider;
        private Material _flagMaterial;
        private Coroutine _stageFloodRoutine;
        private Camera _camera;
        private CastlePiece3D _outlinedPiece;
        private System.Random _random;
        private Vector3 _cameraTarget;
        private int _selected = -1;
        private int _turn;
        private int _placementsInRound;
        private int _pendingDrops;
        private float _lastDropTime = float.NegativeInfinity;
        private bool _dropMouseReleased = true;
        private int _stage;
        private int _rerolls;
        private int _extraRerolls;
        private float _objectYaw;
        private bool _rulesOpen;
        private bool _paletteOpen = true;
        private bool _stageTransitioning;
        private bool _resolving;
        private bool _awaitingStage;
        private bool _finished;
        private bool _stagePassed;
        private string _status;

        [Header("Camera input")]
        private Vector2 _rightDragDisplacement;
        private bool _rightGestureActive;
        private bool _rightGestureMoved;
        private bool _rightGestureScrolled;

        internal float DestructionBoundaryZ => _sceneContext.DestructionBoundaryZ;

        private void Start()
        {
            _sceneContext = GetComponent<ForestSceneContext>();
            _catalog = new CastleCatalog3D();
            ConfigureScene();
            CreateHud();
            _dropPreview = new CastleDropPreview3D();
            _floodEffect = new CastleFloodEffect3D();
            ResetGame();
            Bounds lowerCreek = GameObject.Find("Flat Muddy Creek Bed After Cliff")
                .GetComponent<BoxCollider>().bounds;
            _floodEffect.ShowShallow(_placementBounds, lowerCreek,
                groundY + shallowWaterDepth, lowerCreek.center.y + shallowWaterDepth);
        }

        private void Update()
        {
            if (_catalog == null) return;
            if (_defenseFlag != null && _defenseFlag.transform.position.z <= DestructionBoundaryZ)
                DestroyDefenseFlag();
            Mouse mouse = Mouse.current;
            // Track releases even while the fifth drop is finishing or a popup is open.
            if (mouse != null && mouse.leftButton.wasReleasedThisFrame)
                _dropMouseReleased = true;

            UpdateCameraInput();
            if (!_stageTransitioning)
            {
                ApplyContactGlue();
                UpdateHoveredPiece();
            }
            if (_resolving || _awaitingStage || _finished || _rulesOpen)
            {
                HideDropPreview();
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.escapeKey.wasPressedThisFrame)
                    OpenCandidatePalette();
                if (keyboard.qKey.isPressed) _objectYaw -= RotationSpeed * Time.deltaTime;
                if (keyboard.eKey.isPressed) _objectYaw += RotationSpeed * Time.deltaTime;
            }

            UpdateDropPreview();

            if (mouse == null || !mouse.leftButton.wasPressedThisFrame ||
                mouse.rightButton.isPressed || mouse.rightButton.wasReleasedThisFrame ||
                !_dropMouseReleased || _selected < 0 ||
                _placementsInRound >= PLACEMENTS_PER_ROUND ||
                Time.unscaledTime - _lastDropTime < dropClickCooldown)
                return;

            if (!TryGetPlacementXZ(mouse.position.ReadValue(), out Vector3 placement))
                return;

            // An actual placement requires a new mouse-down after release and
            // at least 0.1 seconds since the previous accepted placement.
            _dropMouseReleased = false;
            _lastDropTime = Time.unscaledTime;
            _paletteOpen = false;
            HideDropPreview();
            StartCoroutine(DropAndResolve(_offers[_selected],
                new Vector3(placement.x, dropHeight, placement.z)));
        }

        /// <summary>Esc 또는 우클릭으로 현재 라운드의 고정 후보를 다시 연다.</summary>
        private void OpenCandidatePalette()
        {
            if (_resolving || _awaitingStage || _finished || _rulesOpen) return;
            _paletteOpen = true;
            _selected = -1;
            HideDropPreview();
            if (_placementsInRound > 0)
                _status = "후보를 다시 선택하세요. 남은 배치 "
                    + (PLACEMENTS_PER_ROUND - _placementsInRound) + "회 (리롤 불가)";
        }

        /// <summary>화면 커서가 배치 가능한 월드 XZ에 있으면 그 위치를 반환한다.</summary>
        private bool TryGetPlacementXZ(Vector2 screen, out Vector3 position)
        {
            if (!TryGetCursorXZ(screen, out position)) return false;
            return CanPlaceAt(position);
        }

        /// <summary>화면 안이고 UI 밖인 커서를 기준 평면 위 월드 XZ 위치로 변환한다.</summary>
        private bool TryGetCursorXZ(Vector2 screen, out Vector3 position)
        {
            position = default;
            if (screen.x < 0f || screen.y < 0f ||
                screen.x >= Screen.width || screen.y >= Screen.height ||
                (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
                return false;

            Ray viewRay = _camera.ScreenPointToRay(screen);
            Plane referencePlane = new Plane(Vector3.up, Vector3.zero);
            if (!referencePlane.Raycast(viewRay, out float distance)) return false;
            position = viewRay.GetPoint(distance);
            return true;
        }

        /// <summary>월드 XZ가 강바닥 안이고 Z가 15 미만이면 배치를 허용한다.</summary>
        private bool CanPlaceAt(Vector3 position)
        {
            return position.z < 15f && _placementBounds.Contains(
                new Vector3(position.x, groundY, position.z));
        }

        /// <summary>커서 위치의 배치 가능 여부에 따라 실제 자재의 낙하 미리보기 색을 바꾼다.</summary>
        private void UpdateDropPreview()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _selected < 0 || mouse.rightButton.isPressed ||
                _offers[_selected] == null ||
                !TryGetCursorXZ(mouse.position.ReadValue(), out Vector3 placement))
            {
                HideDropPreview();
                return;
            }

            bool canPlace = CanPlaceAt(placement);
            _dropPreview.Show(_offers[_selected], placement,
                Quaternion.Euler(0f, _objectYaw, 0f), dropHeight, canPlace,
                _placementBounds.max.y);

            if (_dropPreview.IsVisible)
            {
                Vector3 launchTarget = _camera.WorldToScreenPoint(
                    new Vector3(placement.x, dropHeight, placement.z));
                Vector3 screenTarget = _camera.WorldToScreenPoint(_dropPreview.LandingPosition);
                if (launchTarget.z > 0f && screenTarget.z > 0f)
                    _hud.ShowDropGuide(new Vector2(launchTarget.x, launchTarget.y),
                        new Vector2(screenTarget.x, screenTarget.y));
                else
                    _hud.HideDropGuide();
            }
            else
                _hud.HideDropGuide();
        }

        /// <summary>배치 중단·UI 가림·라운드 종료 시 고스트와 화면 점선을 함께 숨긴다.</summary>
        private void HideDropPreview()
        {
            _dropPreview?.Hide();
            _hud?.HideDropGuide();
        }

        private void OnDestroy()
        {
            SetOutlineTargets(null);
            _dropPreview?.Dispose();
            _floodEffect?.Dispose();
            if (_defenseFlag != null) Destroy(_defenseFlag);
            if (_flagMaterial != null) Destroy(_flagMaterial);
        }

        /// <summary>3D 물리 충돌체를 마우스 레이로 탐색해 원본 자재 상세 UI에 전달한다.</summary>
        private void UpdateHoveredPiece()
        {
            if (_hud == null) return;
            Mouse mouse = Mouse.current;
            if (mouse == null) { SetOutlineTargets(null); _hud.SetHoveredPiece(null, Vector2.zero); return; }

            Vector2 position = mouse.position.ReadValue();
            if (!CanShowItemInfo || mouse.rightButton.isPressed ||
                (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
            {
                SetOutlineTargets(null);
                _hud.SetHoveredPiece(null, position);
                return;
            }

            CastlePiece3D piece = null;
            Ray ray = _camera.ScreenPointToRay(position);
            if (Physics.Raycast(ray, out RaycastHit hit, 300f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                CastlePiece3D candidate = hit.collider.GetComponentInParent<CastlePiece3D>();
                if (candidate != null && _pieceSet.Contains(candidate)) piece = candidate;
            }
            SetOutlineTargets(piece);
            _hud.SetHoveredPiece(piece, position);
        }

        /// <summary>호버 대상은 노란색, 해당 대상과 현재 접촉 중인 기물은 cyan 레이어로 갱신한다.</summary>
        private void SetOutlineTargets(CastlePiece3D hovered)
        {
            if (_outlinedPiece != hovered)
            {
                if (_outlinedPiece != null) _outlinedPiece.SetOutlineLayer(0);
                _outlinedPiece = hovered;
            }

            foreach (CastlePiece3D neighbor in _outlinedNeighbors)
                if (neighbor != null) neighbor.SetOutlineLayer(0);
            _outlinedNeighbors.Clear();

            if (_outlinedPiece == null || !_pieceSet.Contains(_outlinedPiece))
            {
                _outlinedPiece = null;
                return;
            }

            foreach (CastlePiece3D neighbor in Adjacent(_outlinedPiece))
            {
                if (neighbor == _outlinedPiece) continue;
                neighbor.SetOutlineLayer(4u);
                _outlinedNeighbors.Add(neighbor);
            }
            _outlinedPiece.SetOutlineLayer(2u);
        }

        /// <summary>ISO 카메라를 설정하고 씬에 배치된 평평한 진흙 냇가의 배치 범위를 읽는다.</summary>
        private void ConfigureScene()
        {
            _camera = Camera.main;
            if (_camera == null) throw new InvalidOperationException("Game scene needs a Main Camera.");
            _camera.orthographic = true;
            _camera.orthographicSize = 10.5f;
            _camera.transform.rotation = Quaternion.Euler(60f, 45f, 0f);
            _cameraTarget = Vector3.zero;
            UpdateCameraPosition();
            _placementBounds = GameObject.Find("Flat Muddy Creek Bed")
                .GetComponent<BoxCollider>().bounds;
        }

        /// <summary>원본 2D 게임의 상단 상태 UI와 하단 자재 카드 UI 프리팹을 연결한다.</summary>
        private void CreateHud()
        {
            // The Game scene has no EventSystem; uGUI buttons and hover handlers need one.
            if (EventSystem.current == null)
            {
                GameObject events = new GameObject("RestoreCastle UI EventSystem");
                events.AddComponent<EventSystem>();
                events.AddComponent<InputSystemUIInputModule>();
            }
            GameObject source = Resources.Load<GameObject>("Prefabs/UIs/RestoreCastleHud");
            if (source == null) throw new InvalidOperationException("Missing original RestoreCastle HUD prefab.");
            GameObject instance = Instantiate(source);
            instance.name = "RestoreCastleHud";
            _hud = instance.AddComponent<CastleHud3D>();
            _hud.Initialize(this);
            _floatingText = instance.AddComponent<CastleFloatingText3D>();
            _floatingText.Initialize(_camera, (RectTransform)instance.transform);
            ChangeCommitted += _floatingText.Show;
        }

        public int StageNumber => Mathf.Min(_stage + 1, STAGE_COUNT);
        public int StageCount => STAGE_COUNT;
        public int RoundsPerStage => ROUNDS_PER_STAGE;
        public int CurrentStageRound => _awaitingStage ? ROUNDS_PER_STAGE : _turn % ROUNDS_PER_STAGE + 1;
        public int PlacementsPerRound => PLACEMENTS_PER_ROUND;
        public float RotationSpeed { get; set; } = 90f;
        public float CameraYawPerScrollStep { get; set; } = 5f;
        public int PlacementsThisRound => _placementsInRound;
        // The palette hides after each real drop; Esc/right-click reopens it.
        public bool IsRoundPlacing => _resolving || !_paletteOpen;
        public int RerollCount => _rerolls + _extraRerolls;
        public bool CanSelect => !_resolving && !_awaitingStage && !_finished && !_rulesOpen
            && _paletteOpen && _placementsInRound < PLACEMENTS_PER_ROUND;
        public bool CanReroll => CanSelect && _placementsInRound == 0;
        public bool CanShowItemInfo => !_awaitingStage && !_finished && !_rulesOpen;
        public event Action<CastleChange3D> ChangeCommitted;

        /// <summary>게임 규칙이 확정한 변화를 위치와 원인을 보존한 채 표시 시스템에 전달한다.</summary>
        public void PublishChange(CastleChange3D change) => ChangeCommitted?.Invoke(change);

        /// <summary>실제로 배치되어 살아 있는 기물인지 확인한다.</summary>
        public bool IsPlacedPiece(CastlePiece3D piece) => piece != null && _pieceSet.Contains(piece);

        /// <summary>배치한 기물의 카탈로그 정보를 제공한다.</summary>
        public CastleMaterial3D GetPieceMaterial(CastlePiece3D piece) => _catalog.Get(piece.MaterialId);
        /// <summary>현재 충돌 상태에 따라 해당 기물에 닿는 기물 목록을 제공한다.</summary>
        public CastlePiece3D[] GetTouchingPieces(CastlePiece3D piece) => Adjacent(piece).ToArray();
        public bool AwaitingStage => _awaitingStage;
        public bool IsFinished => _finished;
        public bool StagePassed => _stagePassed;
        public string StatusMessage => _status;
        public int PieceCount => _pieces.Count;
        public int SelectedOffer => _selected;
        public CastleMaterial3D[] CurrentOffers => _offers;

        /// <summary>화면 카드가 지정한 후보를 다음 낙하 재료로 선택한다.</summary>
        public void SelectOffer(int index)
        {
            if (!CanSelect || index < 0 || index >= _offers.Length) return;
            _selected = index;
            _status = _offers[index].name + " 선택 (남은 배치 "
                + (PLACEMENTS_PER_ROUND - _placementsInRound) + "회)";
        }

        /// <summary>규칙 창이 열려 있는 동안 게임의 배치 입력을 잠근다.</summary>
        public void SetRulesOpen(bool isOpen)
        {
            _rulesOpen = isOpen;
        }

        /// <summary>스테이지 또는 전체 게임 종료 팝업에서 재시작한다.</summary>
        public void Restart()
        {
            ResetGame();
        }

        /// <summary>
        /// 우클릭 드래그로 카메라 주시점을 강의 Z축을 따라 이동하고 휠로 회전 또는 줌을 처리한다.
        /// UI 밖에서 시작한 짧은 우클릭은 버튼을 놓을 때 자재 후보를 연다.
        /// </summary>
        private void UpdateCameraInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                _rightGestureActive = false;
                return;
            }

            var rightButton = mouse.rightButton;
            bool pointerOverUi = EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject();
            if (rightButton.wasPressedThisFrame)
            {
                _rightGestureActive = !pointerOverUi;
                _rightGestureMoved = false;
                _rightGestureScrolled = false;
                _rightDragDisplacement = Vector2.zero;
            }

            bool gestureInProgress = _rightGestureActive &&
                (rightButton.isPressed || rightButton.wasReleasedThisFrame);
            if (gestureInProgress)
            {
                Vector2 delta = rightButton.wasPressedThisFrame
                    ? Vector2.zero : mouse.delta.ReadValue();
                if (!_rightGestureMoved)
                {
                    _rightDragDisplacement += delta;
                    if (_rightDragDisplacement.sqrMagnitude >=
                        CAMERA_DRAG_THRESHOLD_PIXELS * CAMERA_DRAG_THRESHOLD_PIXELS)
                    {
                        _rightGestureMoved = true;
                        delta = _rightDragDisplacement;
                    }
                    else
                        delta = Vector2.zero;
                }

                if (_rightGestureMoved && delta.sqrMagnitude > 0f)
                {
                    float unit = 2f * _camera.orthographicSize / Mathf.Max(Screen.height, 1);
                    Vector3 right = Vector3.ProjectOnPlane(_camera.transform.right, Vector3.up).normalized;
                    Vector3 up = Vector3.ProjectOnPlane(_camera.transform.up, Vector3.up).normalized;
                    float movementZ = unit * (right.z * delta.x + up.z * delta.y);
                    _cameraTarget.z = Mathf.Clamp(_cameraTarget.z - movementZ,
                        CAMERA_MIN_TARGET_Z, CAMERA_MAX_TARGET_Z);
                }
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                if (gestureInProgress)
                {
                    _rightGestureScrolled = true;
                    if (!pointerOverUi)
                        _camera.transform.Rotate(Vector3.up,
                            NormalizeScrollSteps(scroll) * CameraYawPerScrollStep, Space.World);
                }
                else if (!rightButton.isPressed && !rightButton.wasReleasedThisFrame && !pointerOverUi)
                {
                    _camera.orthographicSize = Mathf.Clamp(_camera.orthographicSize *
                        Mathf.Exp(-scroll * 0.03f), 6f, 12.5f);
                }
            }

            if (rightButton.wasReleasedThisFrame)
            {
                if (_rightGestureActive && !_rightGestureMoved &&
                    !_rightGestureScrolled && !pointerOverUi)
                    OpenCandidatePalette();
                _rightGestureActive = false;
            }

            UpdateCameraPosition();
        }

        /// <summary>
        /// Input System의 휠 델타를 입력 설정과 실행 플랫폼에 맞춰 휠 칸 수로 환산한다.
        /// 정규화된 설정은 그대로 사용하고 Windows의 플랫폼 고유 값은 120으로 나눈다.
        /// </summary>
        private static float NormalizeScrollSteps(float scroll)
        {
            if (InputSystem.settings.scrollDeltaBehavior ==
                InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                (Application.platform == RuntimePlatform.WindowsEditor ||
                 Application.platform == RuntimePlatform.WindowsPlayer))
                return scroll / WINDOWS_SCROLL_UNITS_PER_TICK;

            return scroll;
        }

        /// <summary>현재 회전 방향과 주시 위치를 사용해 카메라 위치를 갱신한다.</summary>
        private void UpdateCameraPosition()
        {
            _camera.transform.position = _cameraTarget - _camera.transform.forward * 40f;
        }

        /// <summary>모든 배치와 소모품 상태를 초기화하고 기본 발판을 배치한다.</summary>
        private void ResetGame()
        {
            SetOutlineTargets(null);
            if (_stageFloodRoutine != null)
            {
                StopCoroutine(_stageFloodRoutine);
                _stageFloodRoutine = null;
            }
            _floodEffect?.Hide();
            _stageTransitioning = false;

            foreach (CastlePiece3D piece in _pieces)
                if (piece != null) Destroy(piece.gameObject);
            if (_defenseFlag != null) Destroy(_defenseFlag);
            _pieces.Clear();
            _pieceSet.Clear();
            _consumedContacts.Clear();
            _random = new System.Random(Environment.TickCount);
            _turn = 0;
            _stage = 0;
            _rerolls = 3;
            _extraRerolls = 0;
            _selected = -1;
            _paletteOpen = true;
            _placementsInRound = 0;
            _pendingDrops = 0;
            _lastDropTime = float.NegativeInfinity;
            _dropMouseReleased = true;
            HideDropPreview();
            _objectYaw = 0f;
            _rulesOpen = false;
            _resolving = false;
            _awaitingStage = false;
            _finished = false;
            _cameraTarget = Vector3.zero;
            _rightGestureActive = false;
            UpdateCameraPosition();
            CreateDefenseFlag();
            DealOffers();
            _status = "재료를 떨어뜨려 깃발을 지키세요. Q/E: 재료 회전";
        }

        /// <summary>월드 원점에 깃대와 붉은 깃발을 만들어 이번 게임의 보호 대상으로 둔다.</summary>
        private void CreateDefenseFlag()
        {
            if (_flagMaterial == null)
                _flagMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _defenseFlag = new GameObject("Defense Flag");
            _defenseFlag.transform.position = Vector3.zero;
            CreateFlagPart("Pole", PrimitiveType.Cylinder, new Vector3(0f, -0.7f, 0f),
                new Vector3(0.1f, 1.4f, 0.1f), new Color(0.72f, 0.53f, 0.25f));
            CreateFlagPart("Banner", PrimitiveType.Cube, new Vector3(0.55f, 0.35f, 0f),
                new Vector3(1f, 0.6f, 0.08f), new Color(0.9f, 0.15f, 0.12f));
            CreateFlagPart("Base", PrimitiveType.Cube, new Vector3(0f, -2.08f, 0f),
                new Vector3(0.55f, 0.12f, 0.55f), new Color(0.24f, 0.2f, 0.15f));
            _flagCollider = _defenseFlag.AddComponent<BoxCollider>();
            _flagCollider.center = new Vector3(0.25f, -0.65f, 0f);
            _flagCollider.size = new Vector3(1.4f, 2.7f, 0.4f);
            _flagBody = _defenseFlag.AddComponent<Rigidbody>();
            _flagBody.mass = 1f;
            _flagBody.linearDamping = 0.15f;
            _flagBody.angularDamping = 0.5f;
        }

        /// <summary>깃발의 한 부분을 지정한 모양, 위치, 크기, 색상으로 만든다.</summary>
        private void CreateFlagPart(string name, PrimitiveType shape, Vector3 position,
            Vector3 scale, Color color)
        {
            GameObject part = GameObject.CreatePrimitive(shape);
            part.name = name;
            part.transform.SetParent(_defenseFlag.transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            Renderer renderer = part.GetComponent<Renderer>();
            part.GetComponent<Collider>().enabled = false;
            renderer.sharedMaterial = _flagMaterial;
            MaterialPropertyBlock colors = new MaterialPropertyBlock();
            colors.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(colors);
        }

        /// <summary>Resources/Prefabs에서 3D 기물을 로드하고 Rigidbody를 통한 중력 낙하를 준비한다.</summary>
        private CastlePiece3D Spawn(CastleMaterial3D material, Vector3 position, bool anchored = false)
        {
            GameObject prefab = Resources.Load<GameObject>("Prefabs/" + material.id);
            if (prefab == null) throw new InvalidOperationException("Missing 3D prefab: " + material.id);
            GameObject instance = Instantiate(prefab, position, Quaternion.Euler(0f, _objectYaw, 0f));
            instance.name = material.name;
            CastlePiece3D piece = instance.GetComponent<CastlePiece3D>();
            piece.Initialize(this, _turn);
            piece.Body.isKinematic = anchored;
            piece.Body.useGravity = !anchored;
            _pieces.Add(piece);
            _pieceSet.Add(piece);
            if (material.id == "vine")
                PublishChange(new CastleChange3D(piece, null, CastleChangeKind.Created, 0,
                    position, "덩쿨 생성!"));
            return piece;
        }

        /// <summary>제외 대상이 아닌 자재 3종을 선택하고 살아있는 나무의 등장 확률을 절반으로 낮춘다.
        /// 카탈로그와 난수 상태를 사용해 _offers와 선택 상태를 갱신한다.</summary>
        private void DealOffers()
        {
            List<CastleMaterial3D> available = _catalog.All
                .Where(x => !x.startOnly && !x.spawnOnly
                    && x.id != "growing_vine" && x.id != "vine"
                    && x.id != "sunflower" && x.id != "empty_bottle"
                    && x.id != "barrel" && x.id != "foul_smelling_lump"
                    && x.id != "waste_bag_small" && x.id != "waste_bag_large")
                .ToList();
            for (int i = 0; i < _offers.Length; i++)
            {
                int index = _random.Next(available.Count);
                _offers[i] = available[index];
                available.RemoveAt(index);
                if (_offers[i].id == "living_tree" && _random.Next(2) == 0)
                {
                    index = _random.Next(available.Count);
                    _offers[i] = available[index];
                    available.RemoveAt(index);
                }
            }
            _selected = -1;
            _placementsInRound = 0;
            _paletteOpen = true;
            HideDropPreview();
        }

        /// <summary>기본 리롤권부터 소비하고 후보 3종을 다시 제시한다.</summary>
        public void Reroll()
        {
            if (!CanReroll) return;
            if (_rerolls > 0) _rerolls--;
            else if (_extraRerolls > 0) _extraRerolls--;
            else return;
            DealOffers();
        }

        /// <summary>각 기물의 낙하는 병렬 진행하고 5개가 모두 안정화되면 라운드를 정산한다.</summary>
        private IEnumerator DropAndResolve(CastleMaterial3D material, Vector3 position)
        {
            // StartCoroutine executes this section immediately, before its first
            // yield. Therefore subsequent clicks may spawn while earlier drops
            // are still falling, without unlocking the chosen material.
            CastlePiece3D dropped = Spawn(material, position);
            _placementsInRound++;
            _pendingDrops++;
            bool finalDrop = _placementsInRound == PLACEMENTS_PER_ROUND;
            if (finalDrop) _resolving = true;

            if (material.id == "empty_bottle")
            {
                _extraRerolls++;
                PublishChange(new CastleChange3D(dropped, dropped, CastleChangeKind.Resource,
                    1, dropped.transform.position, "리롤권 +1"));
            }
            _status = material.name + " 배치 " + _placementsInRound + " / "
                + PLACEMENTS_PER_ROUND
                + (finalDrop ? " — 낙하 정산 대기 중" : " (계속 드랍 가능)");

            float elapsed = 0f;
            while (elapsed < 7f)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
                if (dropped == null || (elapsed >= 1.2f && dropped.Body.IsSleeping()))
                    break;
            }

            _pendingDrops--;

            // Only the fifth placement owns round completion. Earlier coroutines
            // finish independently even if all five were spawned in the meantime.
            if (!finalDrop)
                yield break;

            // Wait for every previously spawned piece, not just the fifth one.
            while (_pendingDrops > 0)
                yield return null;

            // Barrel fuses and effects must finish before the round is scored.
            float fuseElapsed = 0f;
            while (_pieces.Any(x => x != null && x.MaterialId == "barrel" && x.FusePending)
                && fuseElapsed < 3.5f)
            {
                yield return null;
                fuseElapsed += Time.deltaTime;
            }

            yield return new WaitForSeconds(0.3f);
            _turn++;
            TickPeriodic();
            if (_turn % ROUNDS_PER_STAGE == 0)
                FinishStage();
            else
            {
                _resolving = false;
                DealOffers();
            }
            if (!_awaitingStage) _status = "라운드 정산 완료";
        }

        /// <summary>원본 턴 기반 식물 성장 및 덩굴 번식을 물리 기물에 적용한다.</summary>
        private void TickPeriodic()
        {
            foreach (CastlePiece3D piece in _pieces.ToArray())
            {
                if (piece == null || IsAbilityDisabled(piece)) continue;
                int age = _turn - piece.PlacedTurn;
                if (age <= 0) continue;
                if (piece.MaterialId == "living_tree" && age % 2 == 0)
                {
                    piece.transform.localScale *= 1.1f;
                    PublishChange(new CastleChange3D(piece, piece, CastleChangeKind.Growth,
                        0, piece.transform.position));
                }
                if (piece.MaterialId == "bamboo")
                {
                    BoxCollider collider = piece.GetComponent<BoxCollider>();
                    collider.center += Vector3.up * 0.5f;
                    collider.size += Vector3.up;
                    foreach (Transform child in piece.transform)
                    {
                        child.localPosition += Vector3.up * 0.5f;
                        Vector3 scale = child.localScale;
                        scale.y += 1f;
                        child.localScale = scale;
                    }
                    PublishChange(new CastleChange3D(piece, piece, CastleChangeKind.Growth,
                        0, piece.transform.position));
                }
            }
        }

        /// <summary>접촉한 모든 기물을 접착제에 결합하고, 기물 제거 시에는 결합을 해제한다.</summary>
        public void TryAttachGlue(CastlePiece3D a, CastlePiece3D b)
        {
            if (_stageTransitioning || a == null || b == null ||
                !_pieceSet.Contains(a) || !_pieceSet.Contains(b))
                return;

            bool canA = a.MaterialId == "all_purpose_glue" && !IsAbilityDisabled(a);
            bool canB = b.MaterialId == "all_purpose_glue" && !IsAbilityDisabled(b);
            if (!canA && !canB) return;

            CastlePiece3D glue = canA && canB
                ? (a.GetInstanceID() > b.GetInstanceID() ? a : b)
                : (canA ? a : b);
            CastlePiece3D target = glue == a ? b : a;
            glue.TryGlueTo(target);
        }

        /// <summary>이전 물리 프레임부터 닿아 있는 기물에도 접착 효과를 적용한다.</summary>
        private void ApplyContactGlue()
        {
            // Retry existing contacts if their initial collision callback was missed.
            foreach (CastlePiece3D glue in _pieces.ToArray())
            {
                if (glue == null || glue.MaterialId != "all_purpose_glue" ||
                    IsAbilityDisabled(glue))
                    continue;
                foreach (CastlePiece3D neighbor in Adjacent(glue).ToArray())
                    TryAttachGlue(glue, neighbor);
            }

        }

        /// <summary>접촉 그래프에서 소멸된 기물을 제외한 실제 인접 기물만 반환한다.</summary>
        private IEnumerable<CastlePiece3D> Adjacent(CastlePiece3D piece)
        {
            return piece.Contacts.Where(n => n != null && _pieceSet.Contains(n));
        }

        /// <summary>덩쿨 봉인 또는 활성 라바콘 꼭대기의 접촉으로 특수능력이 막혔는지 확인한다.</summary>
        public bool IsAbilityDisabled(CastlePiece3D piece)
        {
            return piece.AbilitySuppressed || Adjacent(piece).Any(n =>
                n.MaterialId == "traffic_cone" && !n.AbilitySuppressed &&
                piece.transform.position.y > n.transform.position.y + 0.15f);
        }

        /// <summary>
        /// Z 경계를 넘은 기물만 제거한다.
        /// piece는 경계를 넘은 기물이다.
        /// </summary>
        internal void RemoveOutsideBoundary(CastlePiece3D piece)
        {
            RemovePiece(piece, CastleChangeKind.Destroyed, BOUNDARY_DESTRUCTION_DELAY, false);
        }

        /// <summary>기물을 제거하고 요청된 경우에만 연결 기물까지 연쇄 제거한다.</summary>
        private int RemovePiece(CastlePiece3D piece, CastleChangeKind reason = CastleChangeKind.Destroyed,
            float destructionDelay = 0f, bool removeAttached = true)
        {
            if (piece == null || !_pieceSet.Contains(piece)) return 0;

            HashSet<CastlePiece3D> connected = new HashSet<CastlePiece3D>();
            Stack<CastlePiece3D> pending = new Stack<CastlePiece3D>();
            pending.Push(piece);
            while (pending.Count > 0)
            {
                CastlePiece3D current = pending.Pop();
                if (current == null || !_pieceSet.Contains(current) || !connected.Add(current))
                    continue;
                if (removeAttached)
                    foreach (CastlePiece3D attached in current.AttachedPieces)
                        if (attached != null && !connected.Contains(attached))
                            pending.Push(attached);
            }

            foreach (CastlePiece3D current in connected)
            {
                if (current == _outlinedPiece) SetOutlineTargets(null);
                _pieceSet.Remove(current);
                _pieces.Remove(current);
                PublishChange(new CastleChange3D(current, piece, reason, 0,
                    current.transform.position));
                Destroy(current.gameObject, destructionDelay);
            }
            return connected.Count;
        }

        /// <summary>폭발 시 오크통은 유지하고 주변 기물에 거리 감쇠가 있는 물리 충격을 준다.</summary>
        public void Explode(CastlePiece3D barrel)
        {
            if (barrel == null || !_pieceSet.Contains(barrel)) return;

            Vector3 origin = barrel.Body.worldCenterOfMass;
            HashSet<CastlePiece3D> targets = new HashSet<CastlePiece3D>();
            foreach (Collider collider in Physics.OverlapSphere(origin,
                BARREL_EXPLOSION_RADIUS, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            {
                CastlePiece3D target = collider.GetComponentInParent<CastlePiece3D>();
                if (target == null || target == barrel || target.MaterialId == "foundation"
                    || !_pieceSet.Contains(target))
                    continue;

                Rigidbody body = target.Body;
                if (body == null || body.isKinematic) continue;
                targets.Add(target);
            }

            foreach (CastlePiece3D target in targets)
            {
                // Move the actual Rigidbody instead of destroying its GameObject.
                // One impulse per piece, even when a prefab has multiple colliders.
                target.Body.AddExplosionForce(barrelExplosionImpulse, origin,
                    BARREL_EXPLOSION_RADIUS, barrelExplosionUpwardsModifier,
                    ForceMode.Impulse);
            }

            // The barrel itself also remains in the game world.
            _status = "오크통 폭발: " + targets.Count + "개 주변 기물을 날렸습니다.";
        }

        /// <summary>스테이지 완료 팝업을 표시한다.</summary>
        private void FinishStage()
        {
            SetOutlineTargets(null);
            HideDropPreview();
            _stagePassed = true;
            _status = "닫기 버튼을 누르면 홍수가 시작됩니다.";
            _awaitingStage = true;
            _resolving = false;
        }

        /// <summary>
        /// 한 차례의 격자 파도를 상류에서 하류로 흘려보내고 흐르는 물이 잦아들 때까지 기물에 힘을 적용한다.
        /// </summary>
        private IEnumerator ResolveStageFlood()
        {
            StageCullSnapshot snapshot = CaptureStageCullSnapshot();
            float waveDuration = floodFillDuration
                * (1f + _stage * FLOOD_DURATION_INCREASE_PER_STAGE);
            float waveDepth = floodWaterDepth
                * (1f + _stage * FLOOD_DEPTH_INCREASE_PER_STAGE);
            var flow = new CastleFloodFlow3D(snapshot.CenterX, snapshot.Width,
                snapshot.StartZ, snapshot.EndZ, groundY, waveDepth,
                floodTravelDuration);
            _floodEffect.Begin(flow);

            float elapsed = 0f;
            while (elapsed < waveDuration)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
                float fraction = Mathf.Clamp01(elapsed / waveDuration);
                float pulse = Mathf.Sin(Mathf.PI * fraction);
                float inlet = floodFillCurve == null ? pulse
                    : Mathf.Clamp01(floodFillCurve.Evaluate(pulse));
                flow.Step(Time.fixedDeltaTime, _pieces, _flagBody, _flagCollider, inlet);
                _floodEffect.UpdateSurface(flow);
            }

            float residualVolume = flow.WaterVolume * 0.05f;
            float stagnantTime = 0f;
            float drainElapsed = 0f;
            while (flow.WaterVolume > residualVolume && stagnantTime < 0.5f
                && drainElapsed < floodTravelDuration * 4f)
            {
                yield return new WaitForFixedUpdate();
                drainElapsed += Time.fixedDeltaTime;
                flow.Step(Time.fixedDeltaTime, _pieces, _flagBody, _flagCollider, 0f);
                _floodEffect.UpdateSurface(flow);
                stagnantTime = flow.MovingWaterVolume <= residualVolume
                    ? stagnantTime + Time.fixedDeltaTime : 0f;
            }
            _floodEffect.Hide();
            _stageTransitioning = false;
            _stageFloodRoutine = null;
            _resolving = false;
            _stage++;
            if (_stage == STAGE_COUNT) { _finished = true; yield break; }
            _rerolls = 3;
            DealOffers();
            _status = "홍수가 지나갔습니다. 다음 스테이지를 시작하세요.";
        }

        /// <summary>깃발이 절벽 뒤 파괴 경계를 넘으면 홍수를 멈추고 실패 팝업을 표시한다.</summary>
        private void DestroyDefenseFlag()
        {
            if (_stageFloodRoutine != null)
            {
                StopCoroutine(_stageFloodRoutine);
                _stageFloodRoutine = null;
            }
            _defenseFlag.SetActive(false);
            Destroy(_defenseFlag);
            _defenseFlag = null;
            _flagBody = null;
            _flagCollider = null;
            _floodEffect.Hide();
            _stageTransitioning = false;
            _resolving = false;
            _stagePassed = false;
            _awaitingStage = true;
            _finished = false;
            _status = "깃발이 절벽에 휘말려 파괴되었습니다. 닫기 버튼을 누르면 처음부터 다시 시작합니다.";
        }

        /// <summary>파도가 풀밭으로 번지지 않도록 위쪽 강바닥의 실제 경계만 캡처한다.</summary>
        private StageCullSnapshot CaptureStageCullSnapshot()
        {
            return new StageCullSnapshot(_placementBounds.center.x,
                _placementBounds.size.x, _placementBounds.max.z, _placementBounds.min.z);
        }

        private sealed class StageCullSnapshot
        {
            public readonly float CenterX;
            public readonly float Width;
            public readonly float StartZ;
            public readonly float EndZ;

            /// <summary>홍수 격자를 만들 강의 중심, 폭, 상류와 하류 경계를 저장한다.</summary>
            public StageCullSnapshot(float centerX, float width, float startZ, float endZ)
            {
                CenterX = centerX;
                Width = width;
                StartZ = startZ;
                EndZ = endZ;
            }
        }

        /// <summary>실패 시 재시작하고 통과 시 팝업을 닫아 홍수와 다음 스테이지를 진행한다.</summary>
        public void ContinueStage()
        {
            if (!_awaitingStage || _stageTransitioning) return;
            if (!_stagePassed || _finished) { ResetGame(); return; }
            _awaitingStage = false;
            _stageTransitioning = true;
            _resolving = true;
            _status = "홍수 발생: 물건들이 휩쓸려 갑니다...";
            _stageFloodRoutine = StartCoroutine(ResolveStageFlood());
        }

    }
}
