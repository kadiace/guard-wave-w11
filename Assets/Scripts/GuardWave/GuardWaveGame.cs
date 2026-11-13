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
        // Flow begins at Ready; the crest arrives five physics seconds later.
        private const float CREEK_PREWAVE_SECONDS = 5f;
        private const float FLAG_SURVIVAL_CHECK_SECONDS = 5f;
        private const float FLOOD_DURATION_INCREASE_PER_STAGE = 0.2f;
        private const float FLOOD_DEPTH_INCREASE_PER_STAGE = 0.5f;
        private const float CAMERA_DRAG_THRESHOLD_PIXELS = 6f;
        private const float CAMERA_MIN_TARGET_Z = -15f;
        private const float CAMERA_MAX_TARGET_Z = 15f;
        private const float WINDOWS_SCROLL_UNITS_PER_TICK = 120f;
        private const float BOUNDARY_DESTRUCTION_DELAY = 1f;

        [Header("3D drop")]
        [SerializeField, Min(2f)] private float dropHeight = 7f;
        [SerializeField, Range(0.05f, 0.5f)] private float dropClickCooldown = 0.1f;

        [Header("Ambient creek and one-shot wave (Ready button)")]
        [SerializeField] private bool ambientFlowPreview = true;

        [Header("Flood stage transition")]
        [SerializeField, Min(0.1f)] private float floodFillDuration = 1.2f;
        [SerializeField, Min(0.1f)] private float floodTravelDuration = 2.25f;
        [SerializeField, Min(0.05f)] private float shallowWaterDepth = 0.3f;
        [SerializeField, Min(0.1f)] private float floodWaterDepth = 5f;
        [SerializeField]
        private AnimationCurve floodFillCurve =
            AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private readonly List<GuardWavePiece> _pieces = new List<GuardWavePiece>();
        // Keep removed/undone runtime objects alive so older checkpoints can restore them.
        private readonly HashSet<GuardWavePiece> _runtimePieces = new HashSet<GuardWavePiece>();
        private readonly Queue<GuardWavePiece> _pendingOutflowRemovals =
            new Queue<GuardWavePiece>();
        private readonly HashSet<GuardWavePiece> _queuedOutflowRemovals =
            new HashSet<GuardWavePiece>();
        private readonly List<WaveSnapshot> _editHistory = new List<WaveSnapshot>();
        private int _playerPlacementCount;
        private readonly List<GuardWaveMudPatch> _mudPatches = new List<GuardWaveMudPatch>();
        // Reverse index includes both a piece's own painted surface and bonds to it.
        // Removing a piece only visits patches that can actually reference it.
        private readonly Dictionary<GuardWavePiece, HashSet<GuardWaveMudPatch>> _mudPatchesByPiece =
            new Dictionary<GuardWavePiece, HashSet<GuardWaveMudPatch>>();
        private readonly List<GuardWaveMudPatch> _removalMudPatches = new List<GuardWaveMudPatch>();
        private readonly HashSet<GuardWaveMudPatch> _sceneMudPatches = new HashSet<GuardWaveMudPatch>();
        private readonly List<GuardWaveMudPatch.MudBondState> _initialMudBondStates =
            new List<GuardWaveMudPatch.MudBondState>();
        // A piece can form just one mud bond during an attempt. Breaking that bond
        // must never immediately re-attach it to this or another wet stroke.
        private readonly HashSet<GuardWavePiece> _everMudBonded = new HashSet<GuardWavePiece>();
        private readonly Dictionary<GuardWavePiece, MudJointState> _activeMudJoints =
            new Dictionary<GuardWavePiece, MudJointState>();
        private readonly Dictionary<Rigidbody, SolverOverride> _solverOverrides =
            new Dictionary<Rigidbody, SolverOverride>();
        private readonly List<GuardWavePiece> _expiredMudJoints = new List<GuardWavePiece>();
        private const int MudSolverIterations = 16;
        private const int MudSolverVelocityIterations = 8;

        private readonly struct MudJointState
        {
            public readonly FixedJoint Joint;
            public readonly Rigidbody PieceBody;
            public readonly Rigidbody ConnectedBody;
            public MudJointState(FixedJoint joint, Rigidbody pieceBody, Rigidbody connectedBody)
            {
                Joint = joint;
                PieceBody = pieceBody;
                ConnectedBody = connectedBody;
            }
        }

        private struct SolverOverride
        {
            public int Count;
            public int Iterations;
            public int VelocityIterations;
        }
        private GameObject _mudPreview;
        private GuardWaveMudPatch _activeMudStroke;
        private Vector2 _lastMudCursorScreen;
        private bool _paintingMud;
        private bool _mudStrokePainted;
        private GuardWaveMudPatch _dryingTarget;
        private GuardWaveDryerCoverage _dryerCoverage;
        private Collider _creekBedCollider;
        private float _dryingHoldSeconds;
        private const float MUD_DRY_HOLD_SECONDS = 1f;

        [Header("Mud adhesion")]
        [SerializeField, Min(0f)] private float mudBreakForce = 150f;
        [SerializeField, Min(0f)] private float mudBreakTorque = 1400f;
        private readonly HashSet<GuardWavePiece> _pieceSet = new HashSet<GuardWavePiece>();
        private readonly HashSet<GuardWavePiece> _scenePieces = new HashSet<GuardWavePiece>();
        private readonly List<SceneBodyState> _initialScenePieceStates = new List<SceneBodyState>();
        // The scene-authored Initial Wall uses world-anchored FixedJoints, not MudPatch bonds.
        private readonly List<FixedJointState> _initialSceneJointStates = new List<FixedJointState>();
        private readonly List<SceneParentActiveState> _initialSceneParentStates =
            new List<SceneParentActiveState>();
        private readonly GuardWaveMaterial[] _offers = new GuardWaveMaterial[4];
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
        private float _objectYaw;
        private bool _rulesOpen;
        private bool _paletteOpen = true;
        private bool _stageTransitioning;
        private bool _waveHasStarted;
        private bool _ambientFlowRunning;
        private bool _resolving;
        private bool _awaitingStage;
        private bool _finished;
        private string _status;
        private bool _initialized;

        [Header("Camera input")]
        private Vector2 _rightDragDisplacement;
        private bool _rightGestureActive;
        private bool _rightGestureMoved;
        private bool _rightGestureStartedWithOffer;
        private GuardWavePiece _rightPressedPiece;

        internal float DestructionBoundaryZ => _sceneContext.DestructionBoundaryZ;
        internal Collider CreekBedCollider => _creekBedCollider;

        private void Start()
        {
            _sceneContext = GetComponent<ForestSceneContext>();
            _catalog = new GuardWaveCatalog();
            _fluidSettings = Resources.Load<GuardWaveFluidSettings>("Datas/GuardWaveFluidSettings");
            if (_fluidSettings == null) throw new InvalidOperationException("Missing Resources/Datas/GuardWaveFluidSettings.asset");
            ConfigureScene();
            RegisterScenePieces();
            RegisterSceneMudPatches();
            CreateHud();
            _dropPreview = new GuardWaveDropPreview();
            _flipRenderer = new GuardWaveFlipRenderer(_fluidSettings);
            ResetGame();
            RegisterExistingMudBonds();
            _initialSceneJointStates.Clear();
            // Only non-mud joints belong to the generic joint snapshot.
            // Adopted authored joints are captured by their MudPatch instead.
            CaptureNonMudFixedJoints(_initialSceneJointStates);
            foreach (GuardWaveMudPatch patch in _mudPatches)
                if (patch != null) patch.CaptureBonds(_initialMudBondStates);
            // ResetGame captured the first history entry before adoption.
            _editHistory.Clear();
            SaveEditState(false);
            _initialized = true;
        }

        private void FixedUpdate()
        {
            PruneDeadMudJoints();
            // Drain every queued crossing this physics step, without an object-count cap.
            while (_pendingOutflowRemovals.Count > 0)
            {
                GuardWavePiece candidate = _pendingOutflowRemovals.Dequeue();
                _queuedOutflowRemovals.Remove(candidate);
                if (candidate != null && IsPlacedPiece(candidate) &&
                    candidate.transform.position.z <= DestructionBoundaryZ)
                    RemovePiece(candidate, GuardWaveChangeKind.Destroyed,
                        BOUNDARY_DESTRUCTION_DELAY);
            }
            if (_fluid == null) return;
            // Drain at most one completed GPU step per physics tick, before
            // dispatching the next simulation step.
            _fluid.ApplyPendingImpulses();
            if (ambientFlowPreview && _ambientFlowRunning)
                _fluid.Step(Time.fixedDeltaTime, 0f);
        }

        private void Update()
        {
            if (!_initialized) return;
            if (_sceneContext.PauseMenu != null && _sceneContext.PauseMenu.IsOpen)
            {
                EndMudDrying();
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard?.rKey.wasPressedThisFrame == true)
            {
                ClearEditorSceneSelectionBeforeReload();
                Managers.Scene.ReloadCurrentScene();
                return;
            }

            if (keyboard != null && keyboard.zKey.wasPressedThisFrame &&
                (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed))
            {
                UndoLastEdit();
                return;
            }

            // Ambient flow can destroy the flag before the first Ready click.
            if (!_awaitingStage && !_finished &&
                _defenseFlag != null && _defenseFlag.activeSelf &&
                _defenseFlag.transform.position.z <= DestructionBoundaryZ)
                DestroyDefenseFlag();
            // Shader variants are compiled in small, mutation-free batches
            // during preparation, not all at the 00:00 trigger.
            if (ambientFlowPreview && !_ambientFlowRunning)
                _fluid?.PrewarmOneKernel();
            _flipRenderer.Render(_fluid);
            Mouse mouse = Mouse.current;
            // Track releases while pieces are falling or a popup is open.
            if (mouse != null && mouse.leftButton.wasReleasedThisFrame)
                _dropMouseReleased = true;

            UpdateCameraInput();
            if (!_stageTransitioning && !_awaitingStage && !_finished)
                UpdateHoveredPiece();
            else
                SetOutlineTargets(null);
            if (_resolving || _awaitingStage || _finished || _rulesOpen)
            {
                EndMudDrying();
                HideDropPreview();
                return;
            }

            if (keyboard != null)
            {
                if (keyboard.qKey.isPressed) _objectYaw -= RotationSpeed * Time.deltaTime;
                if (keyboard.eKey.isPressed) _objectYaw += RotationSpeed * Time.deltaTime;
            }

            UpdateDropPreview();

            // Unlike an ordinary object drop, painting continues throughout
            // the held left-button gesture and emits brush samples as it moves.
            if (_selected >= 0 && _offers[_selected]?.id == "mud")
            {
                UpdateMudPainting(mouse);
                return;
            }
            if (_selected >= 0 && _offers[_selected]?.id == "hair_dryer")
            {
                UpdateMudDrying(mouse);
                return;
            }
            EndMudDrying();

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
                new Vector3(placement.x, _placementBounds.max.y + dropHeight, placement.z)));
        }

        /// <summary>Esc 또는 우클릭으로 현재 기물 선택을 해제한다.</summary>
        private void OpenCandidatePalette()
        {
            if (_resolving || _awaitingStage || _finished || _rulesOpen) return;
            _paletteOpen = true;
            EndMudPainting();
            EndMudDrying();
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
            Plane referencePlane = new Plane(Vector3.up,
                new Vector3(0f, _placementBounds.max.y, 0f));
            if (!referencePlane.Raycast(viewRay, out float distance)) return false;
            position = viewRay.GetPoint(distance);
            return true;
        }

        /// <summary>월드 XZ가 강바닥 안이고 Z가 15 미만이면 배치를 허용한다.</summary>
        private bool CanPlaceAt(Vector3 position)
        {
            return position.z < 15f && _placementBounds.Contains(
                new Vector3(position.x, _placementBounds.center.y, position.z));
        }

        /// <summary>
        /// Pick the first real collider along the camera's mouse ray, rather
        /// than projecting onto an XZ plane or raycasting down in -Y. A log
        /// in front of the riverbed must receive the paint, not the riverbed.
        /// Painted patches are triggers and are ignored by the raycast.
        /// </summary>
        private bool TryGetMudSurface(Vector2 screen, out RaycastHit surface)
        {
            surface = default;
            if (_camera == null ||
                screen.x < 0f || screen.y < 0f ||
                screen.x >= Screen.width || screen.y >= Screen.height ||
                (EventSystem.current != null &&
                 EventSystem.current.IsPointerOverGameObject()))
                return false;

            Ray ray = _camera.ScreenPointToRay(screen);
            return Physics.Raycast(ray, out surface, 300f,
                       Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                   CanPlaceAt(surface.point);
        }

        /// <summary>
        /// Holds one brush stroke while the left mouse button is down.
        /// Resamples long mouse movements so no visible gaps appear between
        /// frames, including when the pointer moves quickly.
        /// </summary>
        private void UpdateMudPainting(Mouse mouse)
        {
            if (mouse == null || !mouse.leftButton.isPressed ||
                mouse.rightButton.isPressed || _rulesOpen || _resolving)
            {
                EndMudPainting();
                return;
            }

            Vector2 cursorScreen = mouse.position.ReadValue();
            if (!_paintingMud)
            {
                if (!mouse.leftButton.wasPressedThisFrame || !_dropMouseReleased ||
                    Time.unscaledTime - _lastDropTime < dropClickCooldown ||
                    !TryGetMudSurface(cursorScreen, out RaycastHit firstHit))
                    return;

                _paintingMud = true;
                _dropMouseReleased = false;
                _lastDropTime = Time.unscaledTime;
                _lastMudCursorScreen = cursorScreen;
                PaintMudAt(firstHit);
                return;
            }

            // Interpolate in screen space, not in projected XZ world space.
            // Each sample gets its own camera ray, so an intervening log
            // occludes the ground even during a quick mouse drag.
            float worldUnitsPerPixel = 2f * _camera.orthographicSize /
                Mathf.Max(Screen.height, 1);
            float travel = Vector2.Distance(_lastMudCursorScreen, cursorScreen);
            int segments = Mathf.Clamp(
                Mathf.CeilToInt(travel * worldUnitsPerPixel /
                    GuardWaveMudPatch.SampleSpacing), 1, 64);

            for (int i = 1; i <= segments; i++)
            {
                Vector2 screenSample = Vector2.Lerp(
                    _lastMudCursorScreen, cursorScreen, i / (float)segments);
                if (TryGetMudSurface(screenSample, out RaycastHit hit))
                    PaintMudAt(hit);
                else
                    _activeMudStroke = null;
            }
            _lastMudCursorScreen = cursorScreen;
        }

        private void PaintMudAt(RaycastHit surface)
        {
            // One held brush gesture may cross from a log to the creek floor.
            // Give each surface its own moving mud mesh, but explicitly bond
            // the surfaces at the brush boundary when they physically meet.
            GuardWaveMudPatch previousPatch = _activeMudStroke != null &&
                _activeMudStroke.SurfaceCollider != surface.collider
                ? _activeMudStroke : null;
            Vector3 lastPoint = default;
            Vector3 lastNormal = Vector3.up;
            bool hasPreviousPoint = previousPatch != null &&
                previousPatch.TryGetLastSurfaceSample(
                    out lastPoint, out lastNormal);
            if (hasPreviousPoint && Vector3.Distance(lastPoint, surface.point) >
                GuardWaveMudPatch.BrushRadius * 2.5f)
                hasPreviousPoint = false;

            if (_activeMudStroke == null ||
                _activeMudStroke.SurfaceCollider != surface.collider ||
                _activeMudStroke.IsFull ||
                !_activeMudStroke.CanContinueAt(surface.point, surface.normal))
            {
                GameObject strokeObject = new GameObject("Painted Mud Stroke");
                strokeObject.transform.SetPositionAndRotation(
                    surface.point, Quaternion.FromToRotation(Vector3.up, surface.normal));
                // Keep paint attached to the surface as it moves or rotates.
                // On terrain, the FixedJoint connects to the world; on a log,
                // the joint connects to the log's Rigidbody.
                strokeObject.transform.SetParent(surface.collider.transform, true);
                _activeMudStroke = strokeObject.AddComponent<GuardWaveMudPatch>();
                _activeMudStroke.Initialize(this, surface.collider,
                    surface.rigidbody, mudBreakForce, mudBreakTorque);
                _mudPatches.Add(_activeMudStroke);
            }

            _activeMudStroke.AddPoint(surface.point, surface.normal);
            _mudStrokePainted = true;
            if (hasPreviousPoint)
            {
                previousPatch.TryBridgeTo(surface.collider, lastPoint, lastNormal);
                _activeMudStroke.TryBridgeTo(previousPatch.SurfaceCollider,
                    surface.point, surface.normal);
            }
            _status = "진흙을 바르는 중… 접촉한 표면을 고정합니다.";
        }

        private void EndMudPainting()
        {
            bool painted = _mudStrokePainted;
            _paintingMud = false;
            _mudStrokePainted = false;
            _activeMudStroke?.FlushStrokeMesh();
            _activeMudStroke = null;
            // One held brush gesture (even if split into patches) is one undo step.
            if (painted && _initialized && !_stageTransitioning && !_awaitingStage)
                SaveEditState(true);
        }

        /// <summary>
        /// Blowing must remain on the same visible wet stroke for one second.
        /// Moving off the stroke, releasing the mouse or changing target resets
        /// the timer. Each stroke dries once, independently.
        /// </summary>
        private void UpdateMudDrying(Mouse mouse)
        {
            if (mouse == null || !mouse.leftButton.isPressed ||
                mouse.rightButton.isPressed || _rulesOpen || _resolving ||
                !TryGetMudSurface(mouse.position.ReadValue(), out RaycastHit surface))
            {
                EndMudDrying();
                return;
            }

            GuardWaveMudPatch target = null;
            float shortestDistance = float.PositiveInfinity;
            foreach (GuardWaveMudPatch patch in _mudPatches)
            {
                if (patch == null ||
                    !patch.TryGetDryingDistance(surface.collider, surface.point,
                        surface.normal, out float distanceSqr) ||
                    distanceSqr >= shortestDistance) continue;
                target = patch;
                shortestDistance = distanceSqr;
            }

            if (target == null)
            {
                EndMudDrying();
                return;
            }

            if (_dryingTarget != target)
            {
                _dryingTarget = target;
                _dryingHoldSeconds = 0f;
            }

            _dryingHoldSeconds += Time.deltaTime;
            if (_dryerCoverage == null)
                _dryerCoverage = new GuardWaveDryerCoverage();
            _dryerCoverage.Show(surface.point, surface.normal,
                Mathf.Clamp01(_dryingHoldSeconds / MUD_DRY_HOLD_SECONDS));
            if (_dryingHoldSeconds < MUD_DRY_HOLD_SECONDS)
            {
                _status = "진흙 건조 중… " +
                    Mathf.FloorToInt(_dryingHoldSeconds / MUD_DRY_HOLD_SECONDS * 100f) + "%";
                return;
            }

            // Refresh the preceding checkpoint immediately before curing so
            // Ctrl+Z can restore exact pre-cure bond strengths and motion.
            if (_editHistory.Count > 0)
                _editHistory[_editHistory.Count - 1] = CaptureWaveSnapshot();
            target.Dry();
            EndMudDrying();
            // One fully dried patch equals one edit; partial heating is not saved.
            SaveEditState(false);
            _status = "진흙 건조 완료! 추가 부착 불가, 기존 접착력 2배.";
        }

        private void EndMudDrying()
        {
            _dryingTarget = null;
            _dryingHoldSeconds = 0f;
            _dryerCoverage?.Hide();
        }

        // Tracks a patch when a piece is its painted surface or a bonded target.
        // A patch can be indexed under several pieces and a piece under several patches.
        internal void TrackMudPatch(GuardWavePiece piece, GuardWaveMudPatch patch)
        {
            if (piece == null || patch == null) return;
            if (!_mudPatchesByPiece.TryGetValue(piece, out HashSet<GuardWaveMudPatch> patches))
            {
                patches = new HashSet<GuardWaveMudPatch>();
                _mudPatchesByPiece.Add(piece, patches);
            }
            patches.Add(patch);
        }

        internal void UntrackMudPatch(GuardWavePiece piece, GuardWaveMudPatch patch)
        {
            if (ReferenceEquals(piece, null) || ReferenceEquals(patch, null) ||
                !_mudPatchesByPiece.TryGetValue(piece, out HashSet<GuardWaveMudPatch> patches))
                return;
            patches.Remove(patch);
            if (patches.Count == 0) _mudPatchesByPiece.Remove(piece);
        }

        internal bool HasMudBond(GuardWavePiece piece) =>
            piece != null && _activeMudJoints.TryGetValue(piece, out MudJointState state) &&
            state.Joint != null;

        internal bool WasEverMudBonded(GuardWavePiece piece) =>
            piece != null && _everMudBonded.Contains(piece);

        internal void RegisterMudBond(GuardWavePiece piece, FixedJoint joint)
        {
            if (piece == null || joint == null) return;
            if (_activeMudJoints.TryGetValue(piece, out MudJointState old))
                ReleaseMudBond(piece, old.Joint);
            _everMudBonded.Add(piece);
            Rigidbody body = piece.Body;
            Rigidbody connected = joint.connectedBody;
            _activeMudJoints[piece] = new MudJointState(joint, body, connected);
            RetainMudSolver(body);
            RetainMudSolver(connected);
        }

        internal void ReleaseMudBond(GuardWavePiece piece, FixedJoint joint)
        {
            if (ReferenceEquals(piece, null) ||
                !_activeMudJoints.TryGetValue(piece, out MudJointState state) ||
                !ReferenceEquals(state.Joint, joint)) return;
            ReleaseMudSolver(state.PieceBody);
            ReleaseMudSolver(state.ConnectedBody);
            _activeMudJoints.Remove(piece);
        }

        private void RetainMudSolver(Rigidbody body)
        {
            if (body == null) return;
            if (_solverOverrides.TryGetValue(body, out SolverOverride value))
            {
                value.Count++;
                _solverOverrides[body] = value;
                return;
            }
            _solverOverrides[body] = new SolverOverride
            {
                Count = 1,
                Iterations = body.solverIterations,
                VelocityIterations = body.solverVelocityIterations
            };
            body.solverIterations = Mathf.Max(body.solverIterations, MudSolverIterations);
            body.solverVelocityIterations =
                Mathf.Max(body.solverVelocityIterations, MudSolverVelocityIterations);
        }

        private void ReleaseMudSolver(Rigidbody body)
        {
            if (ReferenceEquals(body, null) ||
                !_solverOverrides.TryGetValue(body, out SolverOverride value)) return;
            if (--value.Count > 0)
            {
                _solverOverrides[body] = value;
                return;
            }
            if (body != null)
            {
                body.solverIterations = value.Iterations;
                body.solverVelocityIterations = value.VelocityIterations;
            }
            _solverOverrides.Remove(body);
        }

        private void PruneDeadMudJoints()
        {
            if (_activeMudJoints.Count == 0) return;
            _expiredMudJoints.Clear();
            foreach (KeyValuePair<GuardWavePiece, MudJointState> entry in _activeMudJoints)
            {
                if (entry.Key == null || entry.Value.Joint == null ||
                    !IsPlacedPiece(entry.Key))
                    _expiredMudJoints.Add(entry.Key);
            }
            foreach (GuardWavePiece piece in _expiredMudJoints)
            {
                if (_activeMudJoints.TryGetValue(piece, out MudJointState state))
                    ReleaseMudBond(piece, state.Joint);
            }
            _expiredMudJoints.Clear();
        }

        /// <summary>씬에 원래 배치된 기물인지 판별한다.</summary>
        internal bool IsScenePiece(GuardWavePiece piece) => _scenePieces.Contains(piece);

        /// <summary>커서 위치의 배치 가능 여부에 따라 실제 자재의 낙하 미리보기 색을 바꾼다.</summary>
        private void UpdateDropPreview()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && _selected >= 0 && _offers[_selected]?.id == "mud")
            {
                _dropPreview.Hide();
                _hud.HideDropGuide();
                if (!mouse.rightButton.isPressed &&
                    TryGetMudSurface(mouse.position.ReadValue(), out RaycastHit surface))
                {
                    if (_mudPreview == null) _mudPreview = GuardWaveMudPatch.CreatePreview();
                    _mudPreview.transform.SetPositionAndRotation(
                        surface.point + surface.normal * 0.020f,
                        Quaternion.FromToRotation(Vector3.up, surface.normal));
                    _mudPreview.SetActive(true);
                }
                else if (_mudPreview != null) _mudPreview.SetActive(false);
                return;
            }
            if (_mudPreview != null) _mudPreview.SetActive(false);
            if (_selected >= 0 && _offers[_selected]?.id == "hair_dryer")
            {
                HideDropPreview();
                return;
            }
            if (mouse == null || _selected < 0 || mouse.rightButton.isPressed ||
                _offers[_selected] == null ||
                !TryGetCursorXZ(mouse.position.ReadValue(), out Vector3 placement))
            {
                HideDropPreview();
                return;
            }

            bool canPlace = CanPlaceAt(placement);
            _dropPreview.Show(_offers[_selected], placement,
                Quaternion.Euler(0f, _objectYaw, 0f),
                _placementBounds.max.y + dropHeight, canPlace,
                _placementBounds.max.y);

            // A valid placement always has a guide even if collision raycasts
            // miss (e.g. an empty creek section) or the ghost cannot load.
            // The marker aligns with the same XZ used by Spawn().
            Vector3 launchTarget = _camera.WorldToScreenPoint(
                new Vector3(placement.x,
                    _placementBounds.max.y + dropHeight, placement.z));
            Vector3 landingPosition = _dropPreview.IsVisible
                ? _dropPreview.LandingPosition
                : new Vector3(placement.x, _placementBounds.max.y, placement.z);
            Vector3 screenTarget = _camera.WorldToScreenPoint(landingPosition);
            if (canPlace && launchTarget.z > 0f && screenTarget.z > 0f)
                _hud.ShowDropGuide(new Vector2(launchTarget.x, launchTarget.y),
                    new Vector2(screenTarget.x, screenTarget.y));
            else
                _hud.HideDropGuide();
        }

        /// <summary>배치 중단·UI 가림·라운드 종료 시 고스트와 화면 점선을 함께 숨긴다.</summary>
        private void HideDropPreview()
        {
            _dropPreview?.Hide();
            _hud?.HideDropGuide();
            if (_mudPreview != null) _mudPreview.SetActive(false);
        }

        /// <summary>
        /// Unity Editor creates Rigidbody/Transform/GameObject inspectors from
        /// the current selection. Clear references to an object BEFORE its
        /// runtime destruction so inspectors do not initialize with null targets.
        /// Never touches selection in player builds.
        /// </summary>
        internal static void SafeDestroySelectedObject(GameObject candidate)
        {
#if UNITY_EDITOR
            if (candidate == null) return;
            foreach (UnityEngine.Object selected in UnityEditor.Selection.objects)
            {
                GameObject selectedObject = selected as GameObject;
                if (selectedObject == null && selected is Component component)
                    selectedObject = component.gameObject;
                if (selectedObject != null &&
                    (selectedObject == candidate ||
                     selectedObject.transform.IsChildOf(candidate.transform)))
                {
                    UnityEditor.Selection.objects = Array.Empty<UnityEngine.Object>();
                    break;
                }
            }
#endif
        }

        private void ClearEditorSceneSelectionBeforeReload()
        {
#if UNITY_EDITOR
            foreach (UnityEngine.Object selected in UnityEditor.Selection.objects)
            {
                GameObject selectedObject = selected as GameObject;
                if (selectedObject == null && selected is Component component)
                    selectedObject = component.gameObject;
                if (selectedObject != null &&
                    selectedObject.scene.handle == gameObject.scene.handle)
                {
                    UnityEditor.Selection.objects = Array.Empty<UnityEngine.Object>();
                    break;
                }
            }
#endif
        }

        private void OnDestroy()
        {
            SetOutlineTargets(null);
            _dropPreview?.Dispose();
            EndMudPainting();
            EndMudDrying();
            _dryerCoverage?.Dispose();
            _dryerCoverage = null;
            if (_mudPreview != null)
            {
                MeshFilter filter = _mudPreview.GetComponent<MeshFilter>();
                if (filter != null) Destroy(filter.sharedMesh);
                SafeDestroySelectedObject(_mudPreview);
                Destroy(_mudPreview);
            }
            DisposeFluid();
            _flipRenderer?.Dispose();
            // The flag is owned by the Forest scene.
        }

        /// <summary>3D 물리 충돌체를 마우스 레이로 탐색해 원본 자재 상세 UI에 전달한다.</summary>
        private void UpdateHoveredPiece()
        {
            if (_hud == null) return;
            Mouse mouse = Mouse.current;
            Vector2 position = mouse == null ? Vector2.zero : mouse.position.ReadValue();
            GuardWavePiece piece = mouse != null && CanShowItemInfo &&
                !mouse.rightButton.isPressed ? FindPointedPiece(position) : null;
            // While a Forest palette tool is selected, hovered physics
            // objects must not compete with the placement / paint preview.
            // Keep Fog's hover behavior unchanged.
            bool paintingOrPlacing = _offers[0]?.id == "mud" && _selected >= 0;
            SetOutlineTargets(paintingOrPlacing ? null : piece);
            _hud.SetHoveredPiece(piece, position);
        }

        /// <summary>Finds the nearest selectable collider under the cursor, excluding UI.</summary>
        private GuardWavePiece FindPointedPiece(Vector2 position)
        {
            if (_camera == null || position.x < 0 || position.y < 0 ||
                position.x >= Screen.width || position.y >= Screen.height ||
                (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
                return null;

            Ray ray = _camera.ScreenPointToRay(position);
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return null;
            GuardWavePiece piece = hit.collider.GetComponentInParent<GuardWavePiece>();
            return piece != null && _pieceSet.Contains(piece) ? piece : null;
        }

        /// <summary>Highlights one hovered object; scene sticks and placed pieces use the same render layer.</summary>
        private void SetOutlineTargets(GuardWavePiece hovered)
        {
            if (_outlinedPiece == hovered) return;
            if (_outlinedPiece != null) _outlinedPiece.SetOutlineLayer(0);
            _outlinedPiece = hovered != null && _pieceSet.Contains(hovered) ? hovered : null;
            if (_outlinedPiece != null) _outlinedPiece.SetOutlineLayer(2u);
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
            // Fog is an independent urban stage, not a resized Forest creek.
            bool urbanFog = GetComponent<FogSceneController>() != null;
            string groundName = urbanFog ? "Fog Asphalt Ground" : "Flat Muddy Creek Bed";
            GameObject ground = GameObject.Find(groundName);
            if (ground == null)
                throw new InvalidOperationException("Missing scene ground: " + groundName);
            _creekBedCollider = ground.GetComponent<BoxCollider>();
            _placementBounds = _creekBedCollider.bounds;
            _lowerCreekBounds = urbanFog ? _placementBounds
                : GameObject.Find("Flat Muddy Creek Bed After Cliff")
                    .GetComponent<BoxCollider>().bounds;
            _cameraTarget.y = _placementBounds.max.y + 2f;
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
        public bool CanReady => (!ambientFlowPreview ||
            (_fluid != null && _fluid.IsShaderPrewarmed)) &&
            !_stageTransitioning && !_resolving && !_awaitingStage
            && !_finished && !_rulesOpen && _defenseFlag != null
            && _defenseFlag.activeInHierarchy;
        public bool WaveHasStarted => _waveHasStarted;
        public bool CanSelect => !_resolving && !_awaitingStage && !_finished && !_rulesOpen
            && _paletteOpen && !_stageTransitioning;
        public bool CanShowItemInfo => !_awaitingStage && !_finished && !_rulesOpen
            && !_stageTransitioning;
        public event Action<GuardWaveChange> ChangeCommitted;

        /// <summary>게임 규칙이 확정한 변화를 위치와 원인을 보존한 채 표시 시스템에 전달한다.</summary>
        public void PublishChange(GuardWaveChange change) => ChangeCommitted?.Invoke(change);

        /// <summary>실제로 배치되어 살아 있는 기물인지 확인한다.</summary>
        public bool IsPlacedPiece(GuardWavePiece piece) => piece != null &&
            piece.gameObject.activeInHierarchy && _pieceSet.Contains(piece);

        /// <summary>배치한 기물의 카탈로그 정보를 제공한다.</summary>
        public GuardWaveMaterial GetPieceMaterial(GuardWavePiece piece) => _catalog.Get(piece.MaterialId);
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
            if (_offers[index] == null) return;
            EndMudPainting();
            EndMudDrying();
            SetOutlineTargets(null);
            _selected = index;
            _status = _offers[index].name + " 선택";
        }

        /// <summary>규칙 창이 열려 있는 동안 게임의 배치 입력을 잠근다.</summary>
        public void SetRulesOpen(bool isOpen)
        {
            if (isOpen)
            {
                EndMudPainting();
                EndMudDrying();
            }
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
                _rightGestureStartedWithOffer = false;
                _rightPressedPiece = null;
                return;
            }

            var rightButton = mouse.rightButton;
            bool pointerOverUi = EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject();
            if (rightButton.wasPressedThisFrame)
            {
                _rightGestureActive = !pointerOverUi;
                // A right-click gesture begun while holding any palette item
                // cancels placement first, even over an existing piece. Capture
                // this at mouse-down so it cannot also remove on mouse-up.
                _rightGestureStartedWithOffer = _rightGestureActive && _selected >= 0;
                _rightPressedPiece = _rightGestureActive && !_rightGestureStartedWithOffer &&
                    !_stageTransitioning && !_resolving && !_awaitingStage &&
                    !_finished && !_rulesOpen
                    ? FindPointedPiece(mouse.position.ReadValue()) : null;
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
                        Mathf.Exp(-scroll * 0.03f), 6f,
                        GetComponent<FogSceneController>() != null ? 32f : 12.5f);
                }
            }

            if (rightButton.wasReleasedThisFrame)
            {
                if (_rightGestureActive && !_rightGestureMoved && !pointerOverUi)
                {
                    if (_rightGestureStartedWithOffer)
                        OpenCandidatePalette();
                    else if (_rightPressedPiece != null)
                        _rightPressedPiece.RemoveByRightClick();
                    else
                        OpenCandidatePalette();
                }
                _rightPressedPiece = null;
                _rightGestureActive = false;
                _rightGestureStartedWithOffer = false;
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
            EndMudPainting();
            _editHistory.Clear();
            _playerPlacementCount = 0;
            EndMudDrying();
            SetOutlineTargets(null);
            if (_stageFloodRoutine != null)
            {
                StopCoroutine(_stageFloodRoutine);
                _stageFloodRoutine = null;
            }
            DisposeFluid();
            _pendingOutflowRemovals.Clear();
            _queuedOutflowRemovals.Clear();
            _stageTransitioning = false;
            _waveHasStarted = false;
            _ambientFlowRunning = false;
            CommitWaveSnapshot();

            foreach (GuardWaveMudPatch patch in _mudPatches)
            {
                if (patch == null) continue;
                patch.ClearBonds();
                if (_sceneMudPatches.Contains(patch))
                {
                    patch.ResetDryness();
                    continue;
                }
                patch.gameObject.SetActive(false);
                SafeDestroySelectedObject(patch.gameObject);
                Destroy(patch.gameObject);
            }
            _mudPatches.Clear();
            _mudPatches.AddRange(_sceneMudPatches);
            _mudPatchesByPiece.Clear();
            _removalMudPatches.Clear();
            _everMudBonded.Clear();
            foreach (GuardWavePiece piece in _runtimePieces)
            {
                if (piece == null) continue;
                SafeDestroySelectedObject(piece.gameObject);
                Destroy(piece.gameObject);
            }
            _runtimePieces.Clear();
            _pieces.Clear();
            _pieceSet.Clear();
            RestoreInitialScenePieces();
            // During Start(), keep authored joints intact for first adoption.
            // On later resets, restore non-mud and mud joints separately.
            if (_initialized)
            {
                RebuildFixedJoints(_initialSceneJointStates);
                foreach (GuardWaveMudPatch.MudBondState state in _initialMudBondStates)
                    if (state.Patch != null) state.Patch.RestoreBond(state);
            }
            _stage = 0;
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
            _cameraTarget = new Vector3(0f, _placementBounds.max.y +
                (GetComponent<FogSceneController>() != null ? 7f : 2f), 0f);
            _rightGestureActive = false;
            _rightGestureStartedWithOffer = false;
            _rightPressedPiece = null;
            UpdateCameraPosition();
            ResetDefenseFlag();
            DealOffers();
            if (ambientFlowPreview) BeginAmbientCreekFlow();
            _status = GetComponent<FogSceneController>() != null
                ? "건물을 배치하면 +Z 방향 실외기 바람으로 안개를 밀어냅니다. 준비 완료를 누르세요."
                : "진흙을 바르고 드라이기로 말려 접착력을 강화하세요. 기물 우클릭: 제거, 빈 공간 우클릭: 선택 해제";
            // Initial state enables Ctrl+Z to undo the very first placement.
            SaveEditState(false);
        }

        /// <summary>Registers authored Forest objects as selectable without creating runtime scene objects.</summary>
        private void RegisterScenePieces()
        {
            foreach (GuardWavePiece piece in FindObjectsByType<GuardWavePiece>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (piece == null || piece.gameObject.scene.handle != gameObject.scene.handle)
                    continue;
                piece.Initialize(this);
                Rigidbody body = piece.Body;
                if (body == null) continue;
                _scenePieces.Add(piece);
                _initialScenePieceStates.Add(new SceneBodyState(body));
            }
            CaptureSceneParentActivation(_initialSceneParentStates);
        }

        /// <summary>씬에 배치된 진흙을 게임의 접착 및 되돌리기 대상에 포함한다.</summary>
        private void RegisterSceneMudPatches()
        {
            string groundName = GetComponent<FogSceneController>() != null
                ? "Fog Asphalt Ground" : "Flat Muddy Creek Bed";
            Collider creekBed = GameObject.Find(groundName).GetComponent<Collider>();
            foreach (GuardWaveMudPatch patch in FindObjectsByType<GuardWaveMudPatch>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (patch.gameObject.scene.handle != gameObject.scene.handle) continue;
                patch.InitializeAuthored(this, creekBed, creekBed.attachedRigidbody,
                    mudBreakForce, mudBreakTorque);
                _sceneMudPatches.Add(patch);
                _mudPatches.Add(patch);
            }
        }

        /// <summary>
        /// Pre-existing authored FixedJoints enter the ordinary mud bond
        /// registry. The patch itself checks proximity and connected body:
        /// no object names, manually paired lists, or initial-log dry logic.
        /// </summary>
        private void RegisterExistingMudBonds()
        {
            foreach (GuardWavePiece piece in _scenePieces)
            {
                if (!IsPlacedPiece(piece)) continue;
                foreach (FixedJoint joint in piece.GetComponents<FixedJoint>())
                {
                    if (joint == null) continue;
                    foreach (GuardWaveMudPatch patch in _mudPatches)
                    {
                        if (patch != null && patch.TryAdoptExistingBond(piece, joint))
                            break;
                    }
                }
            }
        }

        /// <summary>Captures parent activation so disabled Beaver Houses survive rollback.</summary>
        private void CaptureSceneParentActivation(List<SceneParentActiveState> destination)
        {
            HashSet<GameObject> visited = new HashSet<GameObject>();
            foreach (GuardWavePiece piece in _scenePieces)
            {
                if (piece == null) continue;
                for (Transform ancestor = piece.transform.parent; ancestor != null;
                    ancestor = ancestor.parent)
                {
                    GameObject parent = ancestor.gameObject;
                    if (parent.scene.handle != gameObject.scene.handle) break;
                    if (visited.Add(parent))
                        destination.Add(new SceneParentActiveState(parent));
                }
            }
        }

        /// <summary>Restores all authored sticks on a full game restart.</summary>
        private void RestoreInitialScenePieces()
        {
            foreach (SceneParentActiveState parent in _initialSceneParentStates)
                parent.Restore();
            foreach (SceneBodyState state in _initialScenePieceStates)
            {
                if (state.Body == null) continue;
                state.Body.gameObject.SetActive(state.ActiveSelf);
                state.State.RestorePose(state.Body);
                GuardWavePiece restoredScenePiece = state.Body.GetComponent<GuardWavePiece>();
                if (restoredScenePiece != null && _scenePieces.Contains(restoredScenePiece))
                    _pieceSet.Add(restoredScenePiece);
            }
            Physics.SyncTransforms();
            foreach (SceneBodyState state in _initialScenePieceStates)
            {
                if (state.Body == null) continue;
                state.State.RestoreMotion(state.Body);
                GuardWavePiece piece = state.Body.GetComponent<GuardWavePiece>();
                if (piece != null) _pieceSet.Add(piece);
            }
        }

        /// <summary>Read-only entry point for scene-attached buoyancy diagnostics.</summary>
        internal bool TryGetBuoyancyProbeSample(Rigidbody body,
            out GuardWaveFlipFluid.BuoyancyProbeSample sample)
        {
            sample = default;
            return _fluid != null && _fluid.TryGetBuoyancyProbeSample(body, out sample);
        }

        /// <summary>Prepare a stationary creek at scene load; Ready starts the current.</summary>
        private void BeginAmbientCreekFlow()
        {
            _ambientFlowRunning = false;
            try
            {
                _fluid = new GuardWaveFlipFluid(_placementBounds.max.z,
                    _placementBounds.min.z, _placementBounds.max.y,
                    _lowerCreekBounds.max.y, _fluidSettings.AmbientWaterDepth,
                    floodTravelDuration, _pieces, _flagBody, _fluidSettings, ambientFlow: true);
                // The authored surface is still visible in the editor; disable
                // its renderer only in Play Mode to expose the real FLIP water.
                GameObject upperWater = GameObject.Find("Upper River Water");
                if (upperWater != null && upperWater.TryGetComponent(out MeshRenderer mesh))
                    mesh.enabled = false;
                _status = "기본 수심 1m 흐름 실험: 폭포로 빠진 입자만 상류에서 보충합니다.";
            }
            catch (Exception error)
            {
                Debug.LogError("Unable to initialize ambient creek flow: " + error);
                DisposeFluid();
                _status = "기본 강물 시뮬레이션 시작 실패";
            }
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
            GameObject prefab = Resources.Load<GameObject>("Prefabs/Objects/" + material.id);
            if (prefab == null) throw new InvalidOperationException("Missing 3D prefab: " + material.id);
            GameObject instance = Instantiate(prefab, position, Quaternion.Euler(0f, _objectYaw, 0f));
            instance.name = material.name;
            // Imported OBJ Mesh local IDs may be remapped by Unity 6. Ensure
            // a Forest pebble is not an invisible Rigidbody with a null Mesh.
            if (material.id == "forest_pebble")
                GuardWaveForestPebbleMesh.EnsureMesh(instance);
            GuardWavePiece piece = instance.GetComponent<GuardWavePiece>();
            piece.Initialize(this);
            piece.Body.isKinematic = anchored;
            piece.Body.useGravity = !anchored;
            _pieces.Add(piece);
            _runtimePieces.Add(piece);
            _pieceSet.Add(piece);
            // Newly placed bodies must join the live ambient FLIP solver:
            // this registers their Rigidbody and non-trigger colliders so
            // log (0.6) and Forest pebble (2.6) receive density-based forces.
            _fluid?.RegisterPiece(piece);
            if (material.id == "vine")
                PublishChange(new GuardWaveChange(piece, null, GuardWaveChangeKind.Created, 0,
                    position, "덩쿨 생성!"));
            return piece;
        }

        /// <summary>Fog has only the placeable building in slot 1; Forest keeps its three tools.</summary>
        private void DealOffers()
        {
            bool fogScene = GetComponent<FogSceneController>() != null;
            _offers[0] = _catalog.Get(fogScene ? "building" : "mud");
            _offers[1] = fogScene ? null : _catalog.Get("log");
            _offers[2] = fogScene ? null : _catalog.Get("forest_pebble");
            _offers[3] = fogScene ? null : _catalog.Get("hair_dryer");
            _selected = -1;
            _paletteOpen = true;
            HideDropPreview();
        }

        /// <summary>기물 배치 후 후보를 즉시 갱신하고 떨어지는 기물 수를 추적한다.</summary>
        private IEnumerator DropAndResolve(GuardWaveMaterial material, Vector3 position)
        {
            GuardWavePiece dropped = Spawn(material, position);
            WaveSnapshot placementState = SaveEditState(true);
            _pendingDrops++;
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
            // Update the checkpoint with the settled pose, but never overwrite a
            // newer action or a checkpoint already removed by Ctrl+Z.
            RefreshLatestEditState(placementState);
            if (_pendingDrops == 0 && !_stageTransitioning && !_awaitingStage)
                _status = "배치 완료. 준비완료를 누르면 파도가 시작됩니다.";
        }

        /// <summary>
        /// Z 경계를 넘은 기물만 제거한다.
        /// piece는 경계를 넘은 기물이다.
        /// </summary>
        internal void RemoveOutsideBoundary(GuardWavePiece piece)
        {
            if (piece == null || !IsPlacedPiece(piece) ||
                !_queuedOutflowRemovals.Add(piece)) return;
            _pendingOutflowRemovals.Enqueue(piece);
        }

        /// <summary>A completed right-click removes the selected object only, never its neighbors.</summary>
        internal void RemovePieceByRightClick(GuardWavePiece piece)
        {
            if (_stageTransitioning || _resolving || _awaitingStage || _finished ||
                _rulesOpen || _selected >= 0 || piece == null ||
                piece.MaterialId == "foundation" ||
                piece.MaterialId == "default_flag" || piece.gameObject == _defenseFlag)
                return;
            // Manual removal is an edit too, so Ctrl+Z restores the removed piece.
            if (RemovePiece(piece, GuardWaveChangeKind.Removed) > 0)
                SaveEditState(false);
        }

        /// <summary>Removes one object while retaining scene-authored sticks for reload or rollback.</summary>
        private int RemovePiece(GuardWavePiece piece, GuardWaveChangeKind reason = GuardWaveChangeKind.Destroyed,
            float destructionDelay = 0f)
        {
            // Never remove the defense flag through the ordinary piece pipeline.
            if (piece == null || piece.MaterialId == "default_flag" ||
                piece.gameObject == _defenseFlag || !_pieceSet.Remove(piece)) return 0;
            if (piece == _outlinedPiece) SetOutlineTargets(null);
            _pieces.Remove(piece);
            _everMudBonded.Remove(piece);
            if (_mudPatchesByPiece.TryGetValue(piece, out HashSet<GuardWaveMudPatch> patches))
            {
                // DetachPiece can mutate the reverse index while bonds are removed.
                // Copy only this piece's related patches into a reusable scratch list.
                _removalMudPatches.Clear();
                _removalMudPatches.AddRange(patches);
                foreach (GuardWaveMudPatch patch in _removalMudPatches)
                    if (patch != null) patch.DetachPiece(piece);
                _removalMudPatches.Clear();
            }
            PublishChange(new GuardWaveChange(piece, piece, reason, 0, piece.transform.position));
            if (_scenePieces.Contains(piece) || _runtimePieces.Contains(piece))
            {
                if (destructionDelay > 0f)
                    StartCoroutine(DeactivateRemovedPieceAfterDelay(piece, destructionDelay));
                else
                    piece.gameObject.SetActive(false);
            }
            else
            {
                SafeDestroySelectedObject(piece.gameObject);
                Destroy(piece.gameObject, destructionDelay);
            }
            return 1;
        }

        /// <summary>경계 밖 씬 기물을 지연 비활성화하되, 그동안 복원되었다면 유지한다.</summary>
        private IEnumerator DeactivateRemovedPieceAfterDelay(GuardWavePiece piece, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (piece != null && !_pieceSet.Contains(piece))
                piece.gameObject.SetActive(false);
        }

        /// <summary>준비완료 시 마지막 배치 체크포인트를 파도 실패 복원 기준으로 사용한다.</summary>
        public void Ready()
        {
            if (!CanReady) return;
            EndMudPainting();
            EndMudDrying();
            SetOutlineTargets(null);
            HideDropPreview();
            // Include drying-only edits, even without any placed objects.
            _waveSnapshot = _editHistory.Count > 0
                ? _editHistory[_editHistory.Count - 1] : null;
            _waveHasStarted = true;
            _stageTransitioning = true;
            _resolving = true;
            if (ambientFlowPreview)
            {
                _ambientFlowRunning = true;
                _status = "물살이 시작되었습니다. 5초 뒤 파도가 옵니다.";
                _stageFloodRoutine = StartCoroutine(ResolveAmbientWave());
                return;
            }

            _status = "홍수 발생: 기물과 깃발을 지키세요.";
            _stageFloodRoutine = StartCoroutine(ResolveStageFlood());
        }

        /// <summary>
        /// Start the current at Ready, emit one crest after five seconds, then
        /// check whether the flag survives five more seconds after the crest passes.
        /// </summary>
        private IEnumerator ResolveAmbientWave()
        {
            float warmupElapsed = 0f;
            while (warmupElapsed < CREEK_PREWAVE_SECONDS)
            {
                yield return new WaitForFixedUpdate();
                if (_awaitingStage)
                {
                    _stageFloodRoutine = null;
                    yield break;
                }
                warmupElapsed += Time.fixedDeltaTime;
            }

            try
            {
                // The current has already run for five seconds on the same
                // FLIP simulation: do not construct a second liquid volume.
                _fluid.EmitSingleCrest();
            }
            catch (Exception error)
            {
                Debug.LogError("Unable to emit the 3m wave crest: " + error);
                _ambientFlowRunning = false;
                _waveHasStarted = false;
                _stageTransitioning = false;
                _resolving = false;
                CommitWaveSnapshot();
                _stageFloodRoutine = null;
                _status = "파봉 생성 실패: 입자 예산과 설정을 확인하세요.";
                yield break;
            }

            _status = "파도가 이동 중입니다. 깃발을 지키세요.";
            float travel = (_placementBounds.size.z + _fluidSettings.WaveCrestLength) /
                Mathf.Max(0.1f, _fluidSettings.WaveCrestSpeed);
            float elapsed = 0f;
            while (elapsed < travel + 3f)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;
                if (_awaitingStage)
                {
                    _stageFloodRoutine = null;
                    yield break;
                }
            }

            // Preserve the creek's FLIP steps and Rigidbody forces throughout
            // this five-second survival window; movement never resets the clock.
            _status = "파도가 지나갔습니다. 5초 후 깃발 생존 여부를 판정합니다.";
            float checkElapsed = 0f;
            while (checkElapsed < FLAG_SURVIVAL_CHECK_SECONDS)
            {
                yield return new WaitForFixedUpdate();
                if (_awaitingStage)
                {
                    _stageFloodRoutine = null;
                    yield break;
                }
                if (_defenseFlag == null || !_defenseFlag.activeInHierarchy ||
                    _flagBody.position.z <= DestructionBoundaryZ)
                {
                    _stageFloodRoutine = null;
                    DestroyDefenseFlag();
                    yield break;
                }
                checkElapsed += Time.fixedDeltaTime;
            }

            // The transition remains active throughout settling, so the
            // next preparation countdown cannot restart before clearance.
            CommitWaveSnapshot();
            _ambientFlowRunning = false;
            _stageTransitioning = false;
            _resolving = false;
            _stageFloodRoutine = null;
            CompleteStage();
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

            // A failed attempt retains its GPU particles for the entire popup.
            // Once the wave finishes moving, leave its final frame visible
            // until the player explicitly restores the pre-wave snapshot.
            if (_awaitingStage)
            {
                _stageFloodRoutine = null;
                yield break;
            }

            DisposeFluid();
            CommitWaveSnapshot();
            _stageTransitioning = false;
            _stageFloodRoutine = null;
            _resolving = false;
            CompleteStage();
        }

        /// <summary>재해 종료 시 방어 깃발의 생존 여부를 판정하고 이번 실행의 클리어를 기록한다.</summary>
        public void CompleteStage()
        {
            if (_finished || _awaitingStage) return;
            if (_defenseFlag == null || !_defenseFlag.activeInHierarchy ||
                _defenseFlag.transform.position.z <= DestructionBoundaryZ)
            {
                DestroyDefenseFlag();
                return;
            }

            _stageTransitioning = false;
            _resolving = false;
            _finished = true;
            _status = "재해가 지나갔습니다. 깃발을 지켜냈습니다.";
            StageClearSession.MarkCleared(Managers.Scene.CurrentScene);
        }

        /// <summary>Releases all GPU fluid resources before reset, rollback or stage exit.</summary>
        private void DisposeFluid()
        {
            // Unregister before releasing buffers to avoid rendering disposed GPU data.
            _flipRenderer?.Render(null);
            _fluid?.Dispose();
            _fluid = null;
        }

        /// <summary>깃발이 파괴되면 팝업을 열되 기물의 속도와 Rigidbody 물리는 유지한다.</summary>
        private void DestroyDefenseFlag()
        {
            if (_awaitingStage || _finished)
                return;
            // Without Ready, pick a checkpoint whose flag is still in bounds.
            // Never save the flag's already-destroyed position as the reset state.
            if (_waveSnapshot == null)
                for (int i = _editHistory.Count - 1; i >= 0; i--)
                    if (_editHistory[i].Flag.PositionZ > DestructionBoundaryZ)
                    {
                        _waveSnapshot = _editHistory[i];
                        break;
                    }
            EndMudPainting();
            EndMudDrying();
            // Keep the flood coroutine and GPU particle buffers alive so
            // the wave continues flowing behind the failure popup.
            _stageTransitioning = false;
            _resolving = false;
            _awaitingStage = true;
            _finished = false;
            _status = _waveSnapshot == null
                ? "깃발이 파괴되었습니다. 확인을 누르면 씬을 다시 시작합니다."
                : "깃발이 파괴되었습니다. 확인을 누르면 마지막 배치 상태로 돌아갑니다.";
            StartCoroutine(DeactivateDefenseFlagAfterDelay());
        }

        /// <summary>파괴된 깃발도 일반 기물처럼 1초간 남기되 되돌린 깃발은 숨기지 않는다.</summary>
        private IEnumerator DeactivateDefenseFlagAfterDelay()
        {
            WaveSnapshot failedWave = _waveSnapshot;
            yield return new WaitForSeconds(BOUNDARY_DESTRUCTION_DELAY);
            if (_awaitingStage && _waveSnapshot == failedWave)
                _defenseFlag.SetActive(false);
        }

        /// <summary>깃발, 배치 기물, 진흙, 접착 상태 및 물리 상태를 체크포인트로 기록한다.</summary>
        private WaveSnapshot CaptureWaveSnapshot()
        {
            WaveSnapshot snapshot = new WaveSnapshot
            {
                Flag = new RigidbodyState(_flagBody),
                SelectedOffer = _selected,
                ObjectYaw = _objectYaw,
                CameraTarget = _cameraTarget,
                CameraRotation = _camera.transform.rotation,
                CameraZoom = _camera.orthographicSize,
                PlayerPlacementCount = _playerPlacementCount
            };
            foreach (GuardWavePiece piece in _pieces)
                if (piece != null) snapshot.Pieces.Add(new PieceState(piece));
            foreach (GuardWaveMudPatch patch in _mudPatches)
            {
                if (patch == null || !patch.gameObject.activeSelf) continue;
                snapshot.MudPatches.Add(patch);
                snapshot.MudDryness[patch] = patch.IsDry;
                patch.CaptureBonds(snapshot.MudBonds);
            }
            snapshot.MudBondHistory.UnionWith(_everMudBonded);

            // Include the 13 preauthored Initial Wall joints. These are not
            // MudPatch-created bonds and must be reconstructed separately.
            CaptureNonMudFixedJoints(snapshot.FixedJoints);

            // Inactive authored sticks must also be snapshotted: their parent
            // may be activated later without restarting the Forest scene.
            CaptureSceneParentActivation(snapshot.SceneParents);
            foreach (GuardWavePiece piece in _scenePieces)
                if (piece != null && _pieceSet.Contains(piece))
                    snapshot.SelectableScenePieces.Add(piece);

            foreach (Rigidbody body in FindObjectsByType<Rigidbody>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (body == _flagBody) continue;
                GuardWavePiece bodyPiece = body.GetComponent<GuardWavePiece>();
                if (bodyPiece != null && !_scenePieces.Contains(bodyPiece))
                    continue;
                if (body.gameObject.scene.handle != gameObject.scene.handle)
                    continue;
                snapshot.SceneBodies.Add(new SceneBodyState(body));
            }
            return snapshot;
        }

        /// <summary>Capture scene-authored and other non-mud FixedJoints.</summary>
        private void CaptureNonMudFixedJoints(List<FixedJointState> destination)
        {
            HashSet<FixedJoint> mudJoints = new HashSet<FixedJoint>();
            foreach (MudJointState bond in _activeMudJoints.Values)
                if (bond.Joint != null) mudJoints.Add(bond.Joint);
            foreach (FixedJoint joint in FindObjectsByType<FixedJoint>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (joint == null || joint.gameObject.scene.handle != gameObject.scene.handle ||
                    mudJoints.Contains(joint))
                    continue;
                destination.Add(new FixedJointState(joint));
            }
        }

        /// <summary>Recreate authored joints after they were destroyed by physics or a rollback.</summary>
        private void RebuildFixedJoints(List<FixedJointState> states)
        {
            foreach (FixedJoint joint in FindObjectsByType<FixedJoint>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (joint != null && joint.gameObject.scene.handle == gameObject.scene.handle)
                    Destroy(joint);
            }
            // Destroy() is deferred. Newly created joints are passed straight to
            // GuardWaveInitialLogJoint.Restart, never found via GetComponent.
            HashSet<GameObject> restoredInitialOwners = new HashSet<GameObject>();
            foreach (FixedJointState state in states)
            {
                if (state.IsInitialLog) restoredInitialOwners.Add(state.Owner);
                state.Restore();
            }

            // A checkpoint taken after an Initial Wall joint broke has no
            // saved joint. Restore that authored joint on the next rollback too.
            foreach (FixedJointState state in _initialSceneJointStates)
            {
                if (!state.IsInitialLog || restoredInitialOwners.Contains(state.Owner) ||
                    state.Owner == null || !state.Owner.activeInHierarchy)
                    continue;
                GuardWavePiece piece = state.Owner.GetComponent<GuardWavePiece>();
                if (piece == null || !IsPlacedPiece(piece)) continue;
                state.Restore();
                restoredInitialOwners.Add(state.Owner);
            }
        }

        /// <summary>Undo와 파도 실패가 공유하는 단일 체크포인트 복원 경로.</summary>
        private void RestoreEditState(WaveSnapshot snapshot)
        {
            if (snapshot == null) return;
            _pendingOutflowRemovals.Clear();
            _queuedOutflowRemovals.Clear();
            SetOutlineTargets(null);
            HideDropPreview();

            // Clear live joints before changing active bodies/patches.
            foreach (GuardWaveMudPatch patch in _mudPatches)
                if (patch != null) patch.ClearBonds();
            foreach (GuardWaveMudPatch patch in _mudPatches)
            {
                if (patch == null) continue;
                patch.RestoreDryness(snapshot.MudDryness.TryGetValue(patch, out bool wasDry)
                    && wasDry);
                patch.gameObject.SetActive(snapshot.MudPatches.Contains(patch));
            }
            foreach (GuardWavePiece piece in _runtimePieces)
                if (piece != null) piece.gameObject.SetActive(false);
            _pieces.Clear();
            _pieceSet.Clear();
            foreach (SceneParentActiveState parent in snapshot.SceneParents)
                parent.Restore();
            foreach (PieceState state in snapshot.Pieces)
            {
                GuardWavePiece piece = state.Piece;
                if (piece == null) continue;
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
                state.Body.gameObject.SetActive(state.ActiveSelf);
                state.State.RestorePose(state.Body);
            }
            foreach (GuardWavePiece piece in snapshot.SelectableScenePieces)
                if (piece != null) _pieceSet.Add(piece);

            // Restore preauthored bonds against the saved world-space poses.
            Physics.SyncTransforms();
            RebuildFixedJoints(snapshot.FixedJoints);
            foreach (PieceState state in snapshot.Pieces)
                if (state.Piece != null) state.Body.RestoreMotion(state.Piece.Body);
            snapshot.Flag.RestoreMotion(_flagBody);
            foreach (SceneBodyState state in snapshot.SceneBodies)
                if (state.Body != null) state.State.RestoreMotion(state.Body);
            // Broken/moved joints are not restored by Rigidbody poses alone.
            _everMudBonded.Clear();
            _everMudBonded.UnionWith(snapshot.MudBondHistory);
            foreach (GuardWaveMudPatch.MudBondState bond in snapshot.MudBonds)
                if (bond.Patch != null) bond.Patch.RestoreBond(bond);

            // An authored log remains glued after rollback even if its joint
            // broke before the checkpoint was recorded. Other removed pieces
            // are not resurrected.
            foreach (GuardWaveMudPatch.MudBondState bond in _initialMudBondStates)
            {
                if (bond.Patch == null || bond.Piece == null ||
                    !bond.Patch.gameObject.activeInHierarchy ||
                    !IsPlacedPiece(bond.Piece) || bond.Patch.HasBond(bond.Piece))
                    continue;
                bond.Patch.RestoreBond(bond, fromInitialDefinition: true);
            }

            _selected = snapshot.SelectedOffer;
            _objectYaw = snapshot.ObjectYaw;
            _cameraTarget = snapshot.CameraTarget;
            _camera.transform.rotation = snapshot.CameraRotation;
            _camera.orthographicSize = snapshot.CameraZoom;
            _playerPlacementCount = snapshot.PlayerPlacementCount;
            UpdateCameraPosition();
        }

        /// <summary>파도 스냅샷은 배치 이력과 공유되므로 파도 종료 시 참조만 해제한다.</summary>
        private void CommitWaveSnapshot()
        {
            _waveSnapshot = null;
        }

        /// <summary>한 번의 배치, 붓질 또는 수동 제거 결과를 저장한다.</summary>
        private WaveSnapshot SaveEditState(bool placed)
        {
            if (placed) _playerPlacementCount++;
            WaveSnapshot state = CaptureWaveSnapshot();
            _editHistory.Add(state);
            return state;
        }

        /// <summary>마지막 낙하가 안정화되면 다른 입력을 덮지 않고 위치를 갱신한다.</summary>
        private void RefreshLatestEditState(WaveSnapshot original)
        {
            if (_stageTransitioning || _awaitingStage || _editHistory.Count == 0 ||
                !ReferenceEquals(_editHistory[_editHistory.Count - 1], original))
                return;
            _editHistory[_editHistory.Count - 1] = CaptureWaveSnapshot();
        }

        /// <summary>Ctrl+Z로 가장 최근 사용자 편집 한 단계만 되돌린다.</summary>
        private void UndoLastEdit()
        {
            if (_stageTransitioning || _awaitingStage || _finished || _rulesOpen ||
                _resolving) return;
            EndMudDrying(); // Cancel partial heating; it is not an edit.
            // Ending a brush gesture commits it first, so Ctrl+Z removes that stroke.
            EndMudPainting();
            if (_editHistory.Count <= 1) return;
            _editHistory.RemoveAt(_editHistory.Count - 1);
            // A fresh upstream inflow is part of an initial-impact retry:
            // no old GPU particles may immediately hit the restored 80 N joint.
            DisposeFluid();
            RestoreEditState(_editHistory[_editHistory.Count - 1]);
            if (ambientFlowPreview) BeginAmbientCreekFlow();
            _status = "이전 배치 상태로 되돌렸습니다.";
        }

        private sealed class WaveSnapshot
        {
            public readonly List<PieceState> Pieces = new List<PieceState>();
            public readonly HashSet<GuardWaveMudPatch> MudPatches =
                new HashSet<GuardWaveMudPatch>();
            public readonly Dictionary<GuardWaveMudPatch, bool> MudDryness =
                new Dictionary<GuardWaveMudPatch, bool>();
            public readonly List<GuardWaveMudPatch.MudBondState> MudBonds =
                new List<GuardWaveMudPatch.MudBondState>();
            public readonly List<FixedJointState> FixedJoints = new List<FixedJointState>();
            public readonly HashSet<GuardWavePiece> MudBondHistory =
                new HashSet<GuardWavePiece>();
            public readonly List<SceneBodyState> SceneBodies = new List<SceneBodyState>();
            public readonly List<SceneParentActiveState> SceneParents = new List<SceneParentActiveState>();
            public readonly HashSet<GuardWavePiece> SelectableScenePieces = new HashSet<GuardWavePiece>();
            public RigidbodyState Flag;
            public int SelectedOffer;
            public float ObjectYaw;
            public Vector3 CameraTarget;
            public Quaternion CameraRotation;
            public float CameraZoom;
            public int PlayerPlacementCount;
        }

        /// <summary>Rebuilds preauthored or other non-mud fixed joints after they break.</summary>
        private readonly struct FixedJointState
        {
            private readonly GameObject _owner;
            public GameObject Owner => _owner;
            public bool IsInitialLog => _owner != null &&
                _owner.GetComponent<GuardWaveInitialLogJoint>() != null;
            private readonly Rigidbody _connectedBody;
            private readonly Vector3 _anchor;
            private readonly Vector3 _connectedAnchor;
            private readonly Vector3 _axis;
            private readonly bool _autoAnchor;
            private readonly bool _collision;
            private readonly bool _preprocessing;
            private readonly float _breakForce;
            private readonly float _breakTorque;
            private readonly float _massScale;
            private readonly float _connectedMassScale;

            public FixedJointState(FixedJoint joint)
            {
                _owner = joint.gameObject;
                _connectedBody = joint.connectedBody;
                _anchor = joint.anchor;
                _connectedAnchor = joint.connectedAnchor;
                _axis = joint.axis;
                _autoAnchor = joint.autoConfigureConnectedAnchor;
                _collision = joint.enableCollision;
                _preprocessing = joint.enablePreprocessing;
                _breakForce = joint.breakForce;
                _breakTorque = joint.breakTorque;
                _massScale = joint.massScale;
                _connectedMassScale = joint.connectedMassScale;
            }

            public void Restore()
            {
                if (_owner == null) return;
                FixedJoint joint = _owner.AddComponent<FixedJoint>();
                joint.autoConfigureConnectedAnchor = false;
                joint.connectedBody = _connectedBody;
                joint.anchor = _anchor;
                joint.connectedAnchor = _connectedAnchor;
                joint.axis = _axis;
                joint.breakForce = _breakForce;
                joint.breakTorque = _breakTorque;
                joint.enableCollision = _collision;
                joint.enablePreprocessing = _preprocessing;
                joint.massScale = _massScale;
                joint.connectedMassScale = _connectedMassScale;
                joint.autoConfigureConnectedAnchor = _autoAnchor;
                GuardWaveInitialLogJoint initialLog =
                    _owner.GetComponent<GuardWaveInitialLogJoint>();
                if (initialLog != null)
                    initialLog.Restart(joint, false);
            }
        }

        /// <summary>Preserves an authored parent object's own activation state.</summary>
        private readonly struct SceneParentActiveState
        {
            private readonly GameObject _gameObject;
            private readonly bool _activeSelf;

            public SceneParentActiveState(GameObject gameObject)
            {
                _gameObject = gameObject;
                _activeSelf = gameObject.activeSelf;
            }

            public void Restore()
            {
                if (_gameObject != null) _gameObject.SetActive(_activeSelf);
            }
        }

        private readonly struct SceneBodyState
        {
            public readonly Rigidbody Body;
            public readonly RigidbodyState State;
            public readonly bool ActiveSelf;

            /// <summary>Preserves Rigidbody motion and its own active flag, even under an inactive parent.</summary>
            public SceneBodyState(Rigidbody body)
            {
                Body = body;
                ActiveSelf = body.gameObject.activeSelf;
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
            public float PositionZ => _position.z;

            public RigidbodyState(Rigidbody body)
            {
                if (body == null) throw new ArgumentNullException(nameof(body));
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
                // Sleep/WakeUp requires an active simulated Rigidbody.
                if (!body.gameObject.activeInHierarchy) return;
                if (_sleeping) body.Sleep();
                else body.WakeUp();
            }
        }

        /// <summary>실패 시 마지막 배치 상태로, 배치가 없으면 씬 로드 상태로 돌아간다.</summary>
        public void ConfirmFailure()
        {
            if (!_awaitingStage) return;
            if (_waveSnapshot == null)
            {
                ClearEditorSceneSelectionBeforeReload();
                Managers.Scene.ReloadCurrentScene();
                return;
            }
            // Keep the wave visible until the player confirms the failure.
            if (_stageFloodRoutine != null)
            {
                StopCoroutine(_stageFloodRoutine);
                _stageFloodRoutine = null;
            }
            DisposeFluid();
            _ambientFlowRunning = false;
            // Drop unsafe later edits if the pre-Ready failure selected an
            // earlier valid checkpoint.
            int checkpointIndex = _editHistory.IndexOf(_waveSnapshot);
            if (checkpointIndex >= 0 && checkpointIndex < _editHistory.Count - 1)
                _editHistory.RemoveRange(checkpointIndex + 1,
                    _editHistory.Count - checkpointIndex - 1);
            RestoreEditState(_waveSnapshot);
            CommitWaveSnapshot();
            _awaitingStage = false;
            _stageTransitioning = false;
            _waveHasStarted = false;
            _resolving = false;
            if (ambientFlowPreview)
                BeginAmbientCreekFlow();
            _status = "마지막 배치 상태로 복원했습니다. 배치를 바꾸고 다시 시도하세요.";
        }

    }
}
