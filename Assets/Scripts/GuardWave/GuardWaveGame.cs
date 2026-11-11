using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.EventSystems;

namespace GuardWave
{
    public sealed class GuardWaveGame : MonoBehaviour
    {
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
        [SerializeField, Min(0.1f)] private float floodWaterDepth = 5f;
        [SerializeField]
        private AnimationCurve floodFillCurve =
            AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private readonly List<GuardWavePiece> _pieces = new List<GuardWavePiece>();
        private readonly HashSet<GuardWavePiece> _pieceSet = new HashSet<GuardWavePiece>();
        private readonly HashSet<GuardWavePiece> _outlinedNeighbors = new HashSet<GuardWavePiece>();
        private readonly HashSet<string> _consumedContacts = new HashSet<string>();
        private readonly GuardWaveMaterial[] _offers = new GuardWaveMaterial[3];
        private ForestSceneContext _sceneContext;
        private GuardWaveCatalog _catalog;
        private GuardWaveHud _hud;
        private GuardWaveDropPreview _dropPreview;
        private GuardWaveFlipRenderer _flipRenderer;
        private GuardWaveFluidSettings _fluidSettings;
        private GuardWaveFlipFluid _fluid;
        private Bounds _placementBounds;
        private Bounds _lowerCreekBounds;
        private GuardWaveFloatingText _floatingText;
        private GameObject _defenseFlag;
        private Rigidbody _flagBody;
        private BoxCollider _flagCollider;
        private RigidbodyState _initialFlagState;
        private Coroutine _stageFloodRoutine;
        private WaveSnapshot _waveSnapshot;
        private Camera _camera;
        private GuardWavePiece _outlinedPiece;
        private Vector3 _cameraTarget;
        private int _selected = -1;
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
        private string _status;
        private bool _initialized;

        [Header("Camera input")]
        private Vector2 _rightDragDisplacement;
        private bool _rightGestureActive;
        private bool _rightGestureMoved;

        internal float DestructionBoundaryZ => _sceneContext.DestructionBoundaryZ;

        private void Start()
        {
            _sceneContext = GetComponent<ForestSceneContext>();
            _catalog = new GuardWaveCatalog();
            _fluidSettings = Resources.Load<GuardWaveFluidSettings>("Datas/GuardWaveFluidSettings");
            if (_fluidSettings == null) throw new InvalidOperationException("Missing Resources/Datas/GuardWaveFluidSettings.asset");
            ConfigureScene();
            CreateHud();
            _dropPreview = new GuardWaveDropPreview();
            _flipRenderer = new GuardWaveFlipRenderer(_fluidSettings);
            // The latest master authors all stationary river/waterfall surfaces in Forest.
            // Its old flood mesh stays disabled; the GPU particle renderer replaces it.
            _sceneContext.FloodMesh.gameObject.SetActive(false);
            ResetGame();
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;
            // Only a started wave can fail; restored placements must remain editable.
            if (_stageTransitioning && _waveSnapshot != null &&
                _defenseFlag != null && _defenseFlag.activeSelf &&
                _defenseFlag.transform.position.z <= DestructionBoundaryZ)
                DestroyDefenseFlag();
            _flipRenderer.Render(_fluid);
            Mouse mouse = Mouse.current;
            // Track releases while pieces are falling or a popup is open.
            if (mouse != null && mouse.leftButton.wasReleasedThisFrame)
                _dropMouseReleased = true;

            UpdateCameraInput();
            if (!_stageTransitioning && !_awaitingStage && !_finished)
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
                Time.unscaledTime - _lastDropTime < dropClickCooldown)
                return;

            if (!TryGetPlacementXZ(mouse.position.ReadValue(), out Vector3 placement))
                return;

            // An actual placement requires a new mouse-down after release and
            // at least 0.1 seconds since the previous accepted placement.
            _dropMouseReleased = false;
            _lastDropTime = Time.unscaledTime;
            HideDropPreview();
            StartCoroutine(DropAndResolve(_offers[_selected],
                new Vector3(placement.x, dropHeight, placement.z)));
        }

        /// <summary>Esc 또는 우클릭으로 현재 기물 선택을 해제한다.</summary>
        private void OpenCandidatePalette()
        {
            if (_resolving || _awaitingStage || _finished || _rulesOpen) return;
            _paletteOpen = true;
            _selected = -1;
            HideDropPreview();
            _status = "후보를 다시 선택하세요.";
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
            DisposeFluid();
            _flipRenderer?.Dispose();
            // The flag is owned by the Forest scene.
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

            GuardWavePiece piece = null;
            Ray ray = _camera.ScreenPointToRay(position);
            if (Physics.Raycast(ray, out RaycastHit hit, 300f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                GuardWavePiece candidate = hit.collider.GetComponentInParent<GuardWavePiece>();
                if (candidate != null && _pieceSet.Contains(candidate)) piece = candidate;
            }
            SetOutlineTargets(piece);
            _hud.SetHoveredPiece(piece, position);
        }

        /// <summary>호버 대상은 노란색, 해당 대상과 현재 접촉 중인 기물은 cyan 레이어로 갱신한다.</summary>
        private void SetOutlineTargets(GuardWavePiece hovered)
        {
            if (_outlinedPiece != hovered)
            {
                if (_outlinedPiece != null) _outlinedPiece.SetOutlineLayer(0);
                _outlinedPiece = hovered;
            }

            foreach (GuardWavePiece neighbor in _outlinedNeighbors)
                if (neighbor != null) neighbor.SetOutlineLayer(0);
            _outlinedNeighbors.Clear();

            if (_outlinedPiece == null || !_pieceSet.Contains(_outlinedPiece))
            {
                _outlinedPiece = null;
                return;
            }

            foreach (GuardWavePiece neighbor in Adjacent(_outlinedPiece))
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
            _cameraTarget = Vector3.zero;
            _defenseFlag = _sceneContext.DefenseFlag;
            if (_defenseFlag == null)
                throw new InvalidOperationException(
                    "Forest scene is missing the serialized Defense Flag reference.");
            _flagBody = _defenseFlag.GetComponent<Rigidbody>();
            _flagCollider = _defenseFlag.GetComponent<BoxCollider>();
            _initialFlagState = new RigidbodyState(_flagBody);
            _placementBounds = GameObject.Find("Flat Muddy Creek Bed")
                .GetComponent<BoxCollider>().bounds;
            _lowerCreekBounds = GameObject.Find("Flat Muddy Creek Bed After Cliff")
                .GetComponent<BoxCollider>().bounds;
        }

        /// <summary>원본 2D 게임의 상단 상태 UI와 하단 자재 카드 UI 프리팹을 연결한다.</summary>
        private void CreateHud()
        {
            // The Game scene has no EventSystem; uGUI buttons and hover handlers need one.
            if (EventSystem.current == null)
            {
                GameObject events = new GameObject("GuardWave UI EventSystem");
                events.AddComponent<EventSystem>();
                events.AddComponent<InputSystemUIInputModule>();
            }
            _hud = _sceneContext.Hud;
            if (_hud == null)
                throw new InvalidOperationException("Forest scene is missing a GuardWaveHud reference.");
            _hud.Initialize(this);
            _floatingText = _hud.GetComponent<GuardWaveFloatingText>();
            _floatingText.Initialize(_camera, (RectTransform)_hud.transform);
            ChangeCommitted += _floatingText.Show;
        }

        public int StageNumber => Mathf.Min(_stage + 1, STAGE_COUNT);
        public int StageCount => STAGE_COUNT;
        public float RotationSpeed { get; set; } = 90f;
        public float CameraYawPerScrollStep { get; set; } = 5f;
        public bool IsPaletteHidden => false;
        public bool CanReady => !_stageTransitioning && !_resolving && !_awaitingStage
            && !_finished && !_rulesOpen && _defenseFlag != null
            && _defenseFlag.activeInHierarchy && !_pieces.Any(x => x != null && x.FusePending);
        public int RerollCount => _rerolls + _extraRerolls;
        public bool CanSelect => !_resolving && !_awaitingStage && !_finished && !_rulesOpen
            && _paletteOpen && !_stageTransitioning;
        public bool CanReroll => false;
        public bool CanShowItemInfo => !_awaitingStage && !_finished && !_rulesOpen
            && !_stageTransitioning;
        public event Action<GuardWaveChange> ChangeCommitted;

        /// <summary>게임 규칙이 확정한 변화를 위치와 원인을 보존한 채 표시 시스템에 전달한다.</summary>
        public void PublishChange(GuardWaveChange change) => ChangeCommitted?.Invoke(change);

        /// <summary>실제로 배치되어 살아 있는 기물인지 확인한다.</summary>
        public bool IsPlacedPiece(GuardWavePiece piece) => piece != null && _pieceSet.Contains(piece);

        /// <summary>배치한 기물의 카탈로그 정보를 제공한다.</summary>
        public GuardWaveMaterial GetPieceMaterial(GuardWavePiece piece) => _catalog.Get(piece.MaterialId);
        /// <summary>현재 충돌 상태에 따라 해당 기물에 닿는 기물 목록을 제공한다.</summary>
        public GuardWavePiece[] GetTouchingPieces(GuardWavePiece piece) => Adjacent(piece).ToArray();
        public bool AwaitingFailure => _awaitingStage;
        public bool IsFinished => _finished;
        public string StatusMessage => _status;
        public int PieceCount => _pieces.Count;
        public int SelectedOffer => _selected;
        public GuardWaveMaterial[] CurrentOffers => _offers;

        /// <summary>화면 카드가 지정한 후보를 다음 낙하 재료로 선택한다.</summary>
        public void SelectOffer(int index)
        {
            if (!CanSelect || index < 0 || index >= _offers.Length) return;
            _selected = index;
            _status = _offers[index].name + " 선택";
        }

        /// <summary>규칙 창이 열려 있는 동안 게임의 배치 입력을 잠근다.</summary>
        public void SetRulesOpen(bool isOpen)
        {
            _rulesOpen = isOpen;
        }

        /// <summary>전체 게임 종료 팝업에서 처음부터 다시 시작한다.</summary>
        public void Restart()
        {
            ResetGame();
        }

        /// <summary>
        /// 우클릭 드래그로 카메라 주시점을 강의 Z축을 따라 이동하고 휠로 회전 또는 줌을 처리한다.
        /// 우클릭을 누르는 순간 현재 선택을 해제한다.
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
                OpenCandidatePalette();
                _rightGestureActive = !pointerOverUi;
                _rightGestureMoved = false;
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
                _rightGestureActive = false;

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
            DisposeFluid();
            _stageTransitioning = false;
            CommitWaveSnapshot();

            foreach (GuardWavePiece piece in _pieces)
                if (piece != null) Destroy(piece.gameObject);
            _pieces.Clear();
            _pieceSet.Clear();
            _consumedContacts.Clear();
            _stage = 0;
            _rerolls = 3;
            _extraRerolls = 0;
            _selected = -1;
            _paletteOpen = true;
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
            ResetDefenseFlag();
            DealOffers();
            _status = "고무오리·쓰레기 봉투·큰 돌을 선택해 배치하세요. 우클릭: 선택 해제";
        }

        /// <summary>씬에 배치된 깃발을 최초의 자세와 운동 상태로 되돌린다.</summary>
        private void ResetDefenseFlag()
        {
            _defenseFlag.SetActive(true);
            _initialFlagState.RestorePose(_flagBody);
            Physics.SyncTransforms();
            _initialFlagState.RestoreMotion(_flagBody);
        }

        /// <summary>Resources/Prefabs에서 3D 기물을 로드하고 Rigidbody를 통한 중력 낙하를 준비한다.</summary>
        private GuardWavePiece Spawn(GuardWaveMaterial material, Vector3 position, bool anchored = false)
        {
            GameObject prefab = Resources.Load<GameObject>("Prefabs/" + material.id);
            if (prefab == null) throw new InvalidOperationException("Missing 3D prefab: " + material.id);
            GameObject instance = Instantiate(prefab, position, Quaternion.Euler(0f, _objectYaw, 0f));
            instance.name = material.name;
            GuardWavePiece piece = instance.GetComponent<GuardWavePiece>();
            piece.Initialize(this, 0);
            piece.Body.isKinematic = anchored;
            piece.Body.useGravity = !anchored;
            _pieces.Add(piece);
            _pieceSet.Add(piece);
            if (material.id == "vine")
                PublishChange(new GuardWaveChange(piece, null, GuardWaveChangeKind.Created, 0,
                    position, "덩쿨 생성!"));
            return piece;
        }

        /// <summary>팔레트 재료를 고무오리, 쓰레기 봉투, 큰 돌로 고정한다.</summary>
        private void DealOffers()
        {
            _offers[0] = _catalog.Get("rubber_duck");
            _offers[1] = _catalog.Get("waste_bag_medium");
            _offers[2] = _catalog.Get("large_stone");
            _selected = -1;
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

        /// <summary>기물 배치 후 후보를 즉시 갱신하고 떨어지는 기물 수를 추적한다.</summary>
        private IEnumerator DropAndResolve(GuardWaveMaterial material, Vector3 position)
        {
            GuardWavePiece dropped = Spawn(material, position);
            _pendingDrops++;
            if (material.id == "empty_bottle")
            {
                _extraRerolls++;
                PublishChange(new GuardWaveChange(dropped, dropped, GuardWaveChangeKind.Resource,
                    1, dropped.transform.position, "리롤권 +1"));
            }
            _status = material.name + " 배치 중. 같은 기물을 계속 배치하거나 준비완료를 누르세요.";

            float elapsed = 0f;
            while (elapsed < 7f)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
                if (dropped == null || (elapsed >= 1.2f && dropped.Body.IsSleeping()))
                    break;
            }

            _pendingDrops--;
            if (_pendingDrops == 0 && !_stageTransitioning && !_awaitingStage)
                _status = "배치 완료. 준비완료를 누르면 파도가 시작됩니다.";
        }

        /// <summary>접촉한 모든 기물을 접착제에 결합하고, 기물 제거 시에는 결합을 해제한다.</summary>
        public void TryAttachGlue(GuardWavePiece a, GuardWavePiece b)
        {
            if (_stageTransitioning || a == null || b == null ||
                !_pieceSet.Contains(a) || !_pieceSet.Contains(b))
                return;

            bool canA = a.MaterialId == "all_purpose_glue" && !IsAbilityDisabled(a);
            bool canB = b.MaterialId == "all_purpose_glue" && !IsAbilityDisabled(b);
            if (!canA && !canB) return;

            GuardWavePiece glue = canA && canB
                ? (a.GetInstanceID() > b.GetInstanceID() ? a : b)
                : (canA ? a : b);
            GuardWavePiece target = glue == a ? b : a;
            glue.TryGlueTo(target);
        }

        /// <summary>이전 물리 프레임부터 닿아 있는 기물에도 접착 효과를 적용한다.</summary>
        private void ApplyContactGlue()
        {
            // Retry existing contacts if their initial collision callback was missed.
            foreach (GuardWavePiece glue in _pieces.ToArray())
            {
                if (glue == null || glue.MaterialId != "all_purpose_glue" ||
                    IsAbilityDisabled(glue))
                    continue;
                foreach (GuardWavePiece neighbor in Adjacent(glue).ToArray())
                    TryAttachGlue(glue, neighbor);
            }

        }

        /// <summary>접촉 그래프에서 소멸된 기물을 제외한 실제 인접 기물만 반환한다.</summary>
        private IEnumerable<GuardWavePiece> Adjacent(GuardWavePiece piece)
        {
            return piece.Contacts.Where(n => n != null && _pieceSet.Contains(n));
        }

        /// <summary>덩쿨 봉인 또는 활성 라바콘 꼭대기의 접촉으로 특수능력이 막혔는지 확인한다.</summary>
        public bool IsAbilityDisabled(GuardWavePiece piece)
        {
            return piece.AbilitySuppressed || Adjacent(piece).Any(n =>
                n.MaterialId == "traffic_cone" && !n.AbilitySuppressed &&
                piece.transform.position.y > n.transform.position.y + 0.15f);
        }

        /// <summary>
        /// Z 경계를 넘은 기물만 제거한다.
        /// piece는 경계를 넘은 기물이다.
        /// </summary>
        internal void RemoveOutsideBoundary(GuardWavePiece piece)
        {
            RemovePiece(piece, GuardWaveChangeKind.Destroyed, BOUNDARY_DESTRUCTION_DELAY, false);
        }

        /// <summary>기물을 제거하고 요청된 경우에만 연결 기물까지 연쇄 제거한다.</summary>
        private int RemovePiece(GuardWavePiece piece, GuardWaveChangeKind reason = GuardWaveChangeKind.Destroyed,
            float destructionDelay = 0f, bool removeAttached = true)
        {
            if (piece == null || !_pieceSet.Contains(piece)) return 0;

            HashSet<GuardWavePiece> connected = new HashSet<GuardWavePiece>();
            Stack<GuardWavePiece> pending = new Stack<GuardWavePiece>();
            pending.Push(piece);
            while (pending.Count > 0)
            {
                GuardWavePiece current = pending.Pop();
                if (current == null || !_pieceSet.Contains(current) || !connected.Add(current))
                    continue;
                if (removeAttached)
                    foreach (GuardWavePiece attached in current.AttachedPieces)
                        if (attached != null && !connected.Contains(attached))
                            pending.Push(attached);
            }

            foreach (GuardWavePiece current in connected)
            {
                if (current == _outlinedPiece) SetOutlineTargets(null);
                _pieceSet.Remove(current);
                _pieces.Remove(current);
                PublishChange(new GuardWaveChange(current, piece, reason, 0,
                    current.transform.position));
                if (_waveSnapshot != null)
                    current.gameObject.SetActive(false);
                else
                    Destroy(current.gameObject, destructionDelay);
            }
            return connected.Count;
        }

        /// <summary>폭발 시 오크통은 유지하고 주변 기물에 거리 감쇠가 있는 물리 충격을 준다.</summary>
        public void Explode(GuardWavePiece barrel)
        {
            if (barrel == null || !_pieceSet.Contains(barrel)) return;

            Vector3 origin = barrel.Body.worldCenterOfMass;
            HashSet<GuardWavePiece> targets = new HashSet<GuardWavePiece>();
            foreach (Collider collider in Physics.OverlapSphere(origin,
                BARREL_EXPLOSION_RADIUS, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
            {
                GuardWavePiece target = collider.GetComponentInParent<GuardWavePiece>();
                if (target == null || target == barrel || target.MaterialId == "foundation"
                    || !_pieceSet.Contains(target))
                    continue;

                Rigidbody body = target.Body;
                if (body == null || body.isKinematic) continue;
                targets.Add(target);
            }

            foreach (GuardWavePiece target in targets)
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

        /// <summary>준비완료 시 깃발과 기물의 물리 상태를 저장하고 파도를 시작한다.</summary>
        public void Ready()
        {
            if (!CanReady) return;
            SetOutlineTargets(null);
            HideDropPreview();
            _waveSnapshot = CaptureWaveSnapshot();
            _stageTransitioning = true;
            _resolving = true;
            _status = "홍수 발생: 기물과 깃발을 지키세요.";
            _stageFloodRoutine = StartCoroutine(ResolveStageFlood());
        }

        /// <summary>
        /// Emits a finite 24m wide -Z FLIP/APIC particle wave from Forest's upstream
        /// boundary and advances the GPU fluid with real 3D obstacle reactions.
        /// </summary>
        private IEnumerator ResolveStageFlood()
        {
            float waveDuration = floodFillDuration
                * (1f + _stage * FLOOD_DURATION_INCREASE_PER_STAGE);
            float waveHeight = floodWaterDepth
                * (1f + _stage * FLOOD_DEPTH_INCREASE_PER_STAGE);

            // The Forest creek is author-time geometry spanning x=[-12,12], z=[-15,50].
            // Only this scene's collider bounds define the upstream/downstream extent.
            try
            {
                _fluid = new GuardWaveFlipFluid(_placementBounds.max.z,
                    _placementBounds.min.z, _placementBounds.max.y,
                    _lowerCreekBounds.max.y, waveHeight, floodTravelDuration, _pieces, _flagBody, _fluidSettings);
            }
            catch (Exception error)
            {
                Debug.LogError("Unable to begin GPU fluid wave: " + error);
                DisposeFluid();
                _stageTransitioning = false;
                _resolving = false;
                CommitWaveSnapshot();
                _stageFloodRoutine = null;
                _status = "GPU 파도 시작 실패: Compute Shader와 그래픽 API를 확인하세요.";
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < waveDuration)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
                float fraction = Mathf.Clamp01(elapsed / waveDuration);
                float pulse = Mathf.Sin(Mathf.PI * fraction);
                float inlet = floodFillCurve == null ? pulse
                    : Mathf.Clamp01(floodFillCurve.Evaluate(pulse));
                _fluid.Step(Time.fixedDeltaTime, inlet);
            }

            // Run until the last injected particles could traverse the entire channel.
            // No XZ heightfield, scripted object knockback, or obstacle lookahead remains.
            float travel = (_placementBounds.max.z - _placementBounds.min.z) /
                _fluid.InletSpeed;
            float drainElapsed = 0f;
            while (drainElapsed < travel + 3.0f)
            {
                yield return new WaitForFixedUpdate();
                drainElapsed += Time.fixedDeltaTime;
                _fluid.Step(Time.fixedDeltaTime, 0f);
            }

            float settleElapsed = 0f;
            while (settleElapsed < 1f)
            {
                yield return new WaitForFixedUpdate();
                settleElapsed += Time.fixedDeltaTime;
                if (_defenseFlag != null &&
                    _defenseFlag.transform.position.z <= DestructionBoundaryZ)
                {
                    _stageFloodRoutine = null;
                    DestroyDefenseFlag();
                    yield break;
                }
            }

            DisposeFluid();
            CommitWaveSnapshot();
            _stageTransitioning = false;
            _stageFloodRoutine = null;
            _resolving = false;
            _stage++;
            if (_stage == STAGE_COUNT) { _finished = true; yield break; }
            _status = "홍수가 지나갔습니다. 계속 배치하거나 준비완료를 누르세요.";
        }

        /// <summary>Releases all GPU fluid resources before reset, rollback or stage exit.</summary>
        private void DisposeFluid()
        {
            _fluid?.Dispose();
            _fluid = null;
        }

        /// <summary>깃발이 파괴되면 팝업을 열되 기물의 속도와 Rigidbody 물리는 유지한다.</summary>
        private void DestroyDefenseFlag()
        {
            if (!_stageTransitioning || _waveSnapshot == null || _awaitingStage)
                return;
            if (_stageFloodRoutine != null)
            {
                StopCoroutine(_stageFloodRoutine);
                _stageFloodRoutine = null;
            }
            _defenseFlag.SetActive(false);
            DisposeFluid();
            _stageTransitioning = false;
            _resolving = false;
            _awaitingStage = true;
            _finished = false;
            _status = "깃발이 파괴되었습니다. 확인을 누르면 준비완료 직전으로 돌아갑니다.";
        }

        /// <summary>준비완료 클릭 시점의 깃발, 기물, 씬 물리 오브젝트 및 배치 UI 상태를 기록한다.</summary>
        private WaveSnapshot CaptureWaveSnapshot()
        {
            WaveSnapshot snapshot = new WaveSnapshot
            {
                Flag = new RigidbodyState(_flagBody),
                SelectedOffer = _selected,
                ObjectYaw = _objectYaw,
                CameraTarget = _cameraTarget,
                CameraRotation = _camera.transform.rotation,
                CameraZoom = _camera.orthographicSize
            };
            foreach (GuardWavePiece piece in _pieces)
                if (piece != null) snapshot.Pieces.Add(new PieceState(piece));

            foreach (Rigidbody body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                if (body == _flagBody || body.GetComponent<GuardWavePiece>() != null)
                    continue;
                if (body.gameObject.scene.handle != gameObject.scene.handle)
                    continue;
                snapshot.SceneBodies.Add(new SceneBodyState(body));
            }
            return snapshot;
        }

        /// <summary>기물·깃발의 자세를 일괄 복원한 뒤 물리를 재개해 준비완료 시점으로 되돌린다.</summary>
        private void RestoreWaveSnapshot()
        {
            WaveSnapshot snapshot = _waveSnapshot;
            SetOutlineTargets(null);
            HideDropPreview();

            foreach (GuardWavePiece piece in _pieces)
                if (piece != null) piece.gameObject.SetActive(false);
            _pieces.Clear();
            _pieceSet.Clear();
            foreach (PieceState state in snapshot.Pieces)
            {
                GuardWavePiece piece = state.Piece;
                if (piece == null) continue;
                piece.ClearContacts();
                piece.gameObject.SetActive(true);
                state.Body.RestorePose(piece.Body);
                _pieces.Add(piece);
                _pieceSet.Add(piece);
            }

            _defenseFlag.SetActive(true);
            snapshot.Flag.RestorePose(_flagBody);
            foreach (SceneBodyState state in snapshot.SceneBodies)
            {
                if (state.Body == null) continue;
                state.Body.gameObject.SetActive(true);
                state.State.RestorePose(state.Body);
            }

            // Sync all saved poses before resuming the recorded motion.
            Physics.SyncTransforms();
            foreach (PieceState state in snapshot.Pieces)
                if (state.Piece != null) state.Body.RestoreMotion(state.Piece.Body);
            snapshot.Flag.RestoreMotion(_flagBody);
            foreach (SceneBodyState state in snapshot.SceneBodies)
                if (state.Body != null) state.State.RestoreMotion(state.Body);

            _selected = snapshot.SelectedOffer;
            _objectYaw = snapshot.ObjectYaw;
            _cameraTarget = snapshot.CameraTarget;
            _camera.transform.rotation = snapshot.CameraRotation;
            _camera.orthographicSize = snapshot.CameraZoom;
            UpdateCameraPosition();

            _waveSnapshot = null;
            _awaitingStage = false;
            _stageTransitioning = false;
            _resolving = false;
            _status = "준비완료 클릭 직전 상태로 복원했습니다. 배치를 바꾸고 다시 시도하세요.";
        }

        /// <summary>성공한 파도에서 제거된 기물을 실제로 파괴하고 스냅샷을 해제한다.</summary>
        private void CommitWaveSnapshot()
        {
            if (_waveSnapshot == null) return;
            foreach (PieceState state in _waveSnapshot.Pieces)
                if (state.Piece != null && !_pieceSet.Contains(state.Piece))
                    Destroy(state.Piece.gameObject);
            _waveSnapshot = null;
        }

        private sealed class WaveSnapshot
        {
            public readonly List<PieceState> Pieces = new List<PieceState>();
            public readonly List<SceneBodyState> SceneBodies = new List<SceneBodyState>();
            public RigidbodyState Flag;
            public int SelectedOffer;
            public float ObjectYaw;
            public Vector3 CameraTarget;
            public Quaternion CameraRotation;
            public float CameraZoom;
        }

        private readonly struct SceneBodyState
        {
            public readonly Rigidbody Body;
            public readonly RigidbodyState State;

            /// <summary>파도의 영향을 받을 수 있는 씬 물리 오브젝트의 상태를 보관한다.</summary>
            public SceneBodyState(Rigidbody body)
            {
                Body = body;
                State = new RigidbodyState(body);
            }
        }

        private readonly struct PieceState
        {
            public readonly GuardWavePiece Piece;
            public readonly RigidbodyState Body;

            /// <summary>기물과 해당 시점의 물리 상태를 저장한다.</summary>
            public PieceState(GuardWavePiece piece)
            {
                Piece = piece;
                Body = new RigidbodyState(piece.Body);
            }
        }

        private readonly struct RigidbodyState
        {
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;
            private readonly Vector3 _scale;
            private readonly Vector3 _velocity;
            private readonly Vector3 _angularVelocity;
            private readonly bool _sleeping;
            private readonly bool _kinematic;
            private readonly bool _useGravity;

            /// <summary>리지드바디의 위치, 속도 및 물리 속성을 기록한다.</summary>
            public RigidbodyState(Rigidbody body)
            {
                _position = body.transform.position;
                _rotation = body.transform.rotation;
                _scale = body.transform.localScale;
                _velocity = body.linearVelocity;
                _angularVelocity = body.angularVelocity;
                _sleeping = body.IsSleeping();
                _kinematic = body.isKinematic;
                _useGravity = body.useGravity;
            }

            /// <summary>물리 시뮬레이션을 정지시키고 저장한 화면 위치를 Transform과 Rigidbody 양쪽에 설정한다.</summary>
            public void RestorePose(Rigidbody body)
            {
                body.isKinematic = true;
                body.useGravity = false;
                body.transform.localScale = _scale;
                body.transform.SetPositionAndRotation(_position, _rotation);
                body.position = _position;
                body.rotation = _rotation;
            }

            /// <summary>모든 위치가 동기화된 뒤 저장된 속도·중력·수면 상태를 다시 적용한다.</summary>
            public void RestoreMotion(Rigidbody body)
            {
                body.useGravity = _useGravity;
                body.isKinematic = _kinematic;
                if (!_kinematic)
                {
                    body.linearVelocity = _velocity;
                    body.angularVelocity = _angularVelocity;
                }
                if (_sleeping) body.Sleep();
                else body.WakeUp();
            }
        }

        /// <summary>실패 팝업 확인 시 준비완료 직전 상태를 복원한다.</summary>
        public void ConfirmFailure()
        {
            if (!_awaitingStage) return;
            if (_waveSnapshot == null) return;
            RestoreWaveSnapshot();
        }

    }
}
