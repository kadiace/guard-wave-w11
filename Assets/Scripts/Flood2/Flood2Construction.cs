using System.Collections.Generic;

using UnityEngine;

using UnityEngine.InputSystem;

namespace Flood2
{
    public enum Flood2Tool { Panel, Sandbag, Adhesive, Cure, Remove }

    /// <summary>Limited construction, contact adhesion, curing and complete editable-layout snapshots.</summary>
    public sealed class Flood2Construction : MonoBehaviour
    {
        private const float BRUSH_RADIUS = 0.48f;
        private const int MAX_PATCHES = 96;
        private static readonly Color SANDBAG_COLOR = new Color(0.66f, 0.54f, 0.30f);

        [Header("Flood defence supplies")]
        [SerializeField, Min(0)] private int _startingPanels = 24;
        [SerializeField, Min(0)] private int _startingSandbags = 18;
        [SerializeField, Min(0f)] private float _startingAdhesive = 30f;
        [SerializeField] private Vector3 _panelSize = new Vector3(3.6f, 1.2f, 0.6f);
        [SerializeField] private Vector3 _sandbagSize = new Vector3(1.4f, 0.55f, 1.1f);
        [SerializeField, Min(1f)] private float _sandbagRelativeDensity = 2f;
        private MaterialPropertyBlock _sandbagTint;
        private readonly List<Flood2Piece> _pieces = new List<Flood2Piece>();
        private readonly List<Patch> _patches = new List<Patch>();
        private readonly Stack<Snapshot> _undo = new Stack<Snapshot>();
        private readonly Collider[] _overlaps = new Collider[64];
        private Flood2Stage _stage;
        private GameObject _preview;
        private Renderer _previewRenderer;
        private MaterialPropertyBlock _previewTint;
        private Snapshot _checkpoint;
        private int _nextId;
        private int _nextPatchId;
        private float _yaw;
        private float _contactClock;
        private bool _painting;
        private bool _hasPreviousPaint;
        private Vector3 _previousPaint;
        private Patch _currentPatch;
        private Patch _curingPatch;
        private float _cureTime;
        private Vector3 _candidate;
        private bool _candidateValid;
        public IReadOnlyList<Flood2Piece> Pieces => _pieces;
        public Flood2Tool Tool { get; private set; }
        public int Panels { get; private set; }
        public int Sandbags { get; private set; }
        public float Adhesive { get; private set; }
        public int StartingPanels => _startingPanels;
        public int StartingSandbags => _startingSandbags;
        public float StartingAdhesive => _startingAdhesive;
        public int PatchCount => _patches.Count;
        public int BondCount { get { int n = 0; foreach (var p in _pieces) if (p.IsBonded) n++; return n; } }
        public string Hint { get; private set; } = "물막이판으로 흐름을 돌리고 모래주머니로 낮은 틈을 보강하세요.";
        public float CureProgress => _cureTime;
        public bool HasCheckpoint => _checkpoint != null;

        private sealed class Patch
        {
            public int Id { get; set; }
            public int Owner { get; set; }
            public bool Dry { get; set; }
            public List<Vector3> Points { get; set; } = new List<Vector3>();
            public LineRenderer Line { get; set; }
        }

        private sealed class PieceState
        {
            public int Id { get; set; }
            public Flood2Material Kind { get; set; }
            public Vector3 Position { get; set; }
            public Quaternion Rotation { get; set; }
            public Vector3 Velocity { get; set; }
            public Vector3 Angular { get; set; }
            public bool Bonded { get; set; }
            public bool EverBonded { get; set; }
            public bool Dry { get; set; }
            public bool Active { get; set; }
            public int Connected { get; set; }
            public int PatchId { get; set; }
            public Vector3 Anchor { get; set; }
        }

        private sealed class Snapshot
        {
            public List<PieceState> Pieces { get; set; } = new List<PieceState>();
            public List<Patch> Patches { get; set; } = new List<Patch>();
            public int Panels { get; set; }
            public int Sandbags { get; set; }
            public float Adhesive { get; set; }
            public int NextId { get; set; }
            public int NextPatchId { get; set; }
        }

        /// <summary>Creates the non-colliding placement preview and initializes the stage-specific tools.</summary>
        public void Initialize(Flood2Stage stage)
        {
            _stage = stage;
            Panels = _startingPanels;
            Sandbags = _startingSandbags;
            Adhesive = _startingAdhesive;
            _sandbagTint = new MaterialPropertyBlock();
            _sandbagTint.SetColor("_BaseColor", SANDBAG_COLOR);
            _preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _preview.name = "Placement preview";
            _preview.layer = 2;
            _preview.GetComponent<Collider>().enabled = false;
            _previewRenderer = _preview.GetComponent<Renderer>();
            _previewRenderer.sharedMaterial = stage.PreviewMaterial;
            _previewTint = new MaterialPropertyBlock();
            _preview.SetActive(false);
        }

        /// <summary>Returns the world dimensions of a catalog item.</summary>
        private Vector3 Size(Flood2Material kind) =>
            Vector3.Max(kind == Flood2Material.Panel ? _panelSize : _sandbagSize, Vector3.one * 0.1f);

        /// <summary>Switches tools and finalizes any pending paint gesture.</summary>
        public void SelectTool(Flood2Tool tool)
        {
            FinishInput();
            Tool = tool;
            Hint = tool == Flood2Tool.Adhesive ? "접촉면 또는 자재 밑동 주변에 드래그 · 시작 0.5m + 도포 길이"
                : tool == Flood2Tool.Cure ? "젖은 선 위에서 1초 누르기 · 경화 후에는 새 접합을 받지 않습니다."
                : tool == Flood2Tool.Remove ? "설치 자재를 클릭하여 회수합니다. 접합재는 Undo로 되돌릴 수 있습니다."
                : "클릭: 설치  /  Q·E: 회전  /  Shift: 정밀 배치";
        }

        private void Update()
        {
            if (_stage == null) return;
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            if (!_stage.CanEdit || mouse == null)
            {
                _preview.SetActive(false);
                FinishInput();
                return;
            }
            if (keyboard != null)
            {
                if (keyboard.digit1Key.wasPressedThisFrame) SelectTool(Flood2Tool.Panel);
                if (keyboard.digit2Key.wasPressedThisFrame) SelectTool(Flood2Tool.Sandbag);
                if (keyboard.digit3Key.wasPressedThisFrame) SelectTool(Flood2Tool.Adhesive);
                if (keyboard.digit4Key.wasPressedThisFrame) SelectTool(Flood2Tool.Cure);
                if (keyboard.digit5Key.wasPressedThisFrame) SelectTool(Flood2Tool.Remove);
                if (keyboard.qKey.wasPressedThisFrame) _yaw -= 15f;
                if (keyboard.eKey.wasPressedThisFrame) _yaw += 15f;
                if (keyboard.zKey.wasPressedThisFrame && keyboard.ctrlKey.isPressed) Undo();
            }
            if (!mouse.leftButton.isPressed) FinishInput();
            if (_stage.Hud.BlocksPointer(mouse.position.ReadValue())) { _preview.SetActive(false); FinishInput(); return; }
            Ray ray = _stage.ViewCamera.ScreenPointToRay(mouse.position.ReadValue());
            if (!Physics.Raycast(ray, out RaycastHit hit, 300f, ~4, QueryTriggerInteraction.Ignore)) { _preview.SetActive(false); FinishInput(); return; }
            if (Tool == Flood2Tool.Panel || Tool == Flood2Tool.Sandbag)
            {
                Flood2Material kind = Tool == Flood2Tool.Panel ? Flood2Material.Panel : Flood2Material.Sandbag;
                Vector3 size = Size(kind);
                _candidate = hit.point;
                if (keyboard == null || !keyboard.shiftKey.isPressed)
                {
                    _candidate.x = Mathf.Round(_candidate.x / 0.3f) * 0.3f;
                    _candidate.z = Mathf.Round(_candidate.z / 0.3f) * 0.3f;
                }
                float support = hit.point.y;
                Quaternion rotation = Quaternion.Euler(0f, _yaw, 0f);
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    Vector3 p = _candidate + rotation * new Vector3(x * size.x * 0.5f, 0, z * size.z * 0.5f);
                    support = Mathf.Max(support, Flood2Terrain.Height(p.x, p.z));
                }
                _candidate.y = support + size.y * 0.5f + 0.035f;
                _candidateValid = CanPlace(kind, _candidate, rotation, out string reason);
                Hint = _candidateValid ? "클릭: 설치  /  Q·E: 15° 회전  /  Shift: 정밀 배치" : reason;
                _preview.SetActive(true);
                _preview.transform.SetPositionAndRotation(_candidate, rotation);
                _preview.transform.localScale = size;
                _previewTint.SetColor("_BaseColor", _candidateValid ? new Color(0.2f, 0.95f, 0.7f, 0.45f) : new Color(1f, 0.25f, 0.2f, 0.45f));
                _previewRenderer.SetPropertyBlock(_previewTint);
                if (mouse.leftButton.wasPressedThisFrame) TryPlace(kind, _candidate, _yaw);
            }
            else
            {
                _preview.SetActive(false);
                if (Tool == Flood2Tool.Adhesive && mouse.leftButton.isPressed) Paint(hit.collider, hit.point);
                if (Tool == Flood2Tool.Cure && mouse.leftButton.isPressed) CureAt(hit.point, Time.deltaTime);
                if (Tool == Flood2Tool.Remove && mouse.leftButton.wasPressedThisFrame) Remove(hit.collider.GetComponent<Flood2Piece>());
            }
        }

        /// <summary>Checks inventory, map limits, citizen clearance and actual collider penetration.</summary>
        private bool CanPlace(Flood2Material kind, Vector3 center, Quaternion rotation, out string reason)
        {
            reason = "";
            if (kind == Flood2Material.Panel ? Panels <= 0 : Sandbags <= 0) { reason = "이 자재의 재고가 없습니다."; return false; }
            Vector3 size = Size(kind);
            Vector3 extents = new Vector3(Mathf.Abs((rotation * Vector3.right).x) * size.x + Mathf.Abs((rotation * Vector3.forward).x) * size.z, 0f,
                Mathf.Abs((rotation * Vector3.right).z) * size.x + Mathf.Abs((rotation * Vector3.forward).z) * size.z) * 0.5f;
            if (Mathf.Abs(center.x) + extents.x > 17.8f || center.z - extents.z < 1f || center.z + extents.z > 53f || center.y > 5f)
            { reason = "표시된 마을 안에 설치하세요. 상류 유입부와 배수구는 비워 둡니다."; return false; }
            foreach (var citizen in _stage.Citizens)
            {
                Vector3 local = Quaternion.Inverse(rotation) * (citizen.transform.position - center);
                if (Mathf.Abs(local.x) < size.x * 0.5f + 0.6f && Mathf.Abs(local.z) < size.z * 0.5f + 0.6f && center.y - size.y / 2f < citizen.transform.position.y + 1.8f)
                { reason = "시민의 대기 공간을 비워 주세요."; return false; }
            }
            int count = Physics.OverlapBoxNonAlloc(center, size * 0.5f - Vector3.one * 0.025f, _overlaps, rotation, ~4, QueryTriggerInteraction.Ignore);
            if (count > 0) { reason = "자재나 지형과 겹칩니다. 위 또는 옆으로 옮겨 주세요."; return false; }
            return true;
        }

        /// <summary>Places a validated real Rigidbody and records a resource-aware undo snapshot.</summary>
        public bool TryPlace(Flood2Material kind, Vector3 center, float yaw)
        {
            if (!_stage.CanEdit || !CanPlace(kind, center, Quaternion.Euler(0f, yaw, 0f), out string reason)) return false;
            _undo.Push(Capture());
            Spawn(_nextId++, kind, center, Quaternion.Euler(0f, yaw, 0f));
            if (kind == Flood2Material.Panel) Panels--; else Sandbags--;
            Physics.SyncTransforms();
            return true;
        }

        /// <summary>Instantiates a catalog body; callers handle validation and stock.</summary>
        private Flood2Piece Spawn(int id, Flood2Material kind, Vector3 position, Quaternion rotation)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"{kind} {id:00}";
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = Size(kind);
            Renderer solid = go.GetComponent<Renderer>();
            if (kind == Flood2Material.Panel) solid.sharedMaterial = _stage.PanelMaterial;
            else
            {
                solid.enabled = false;
                AddSandbagVisual(go.transform);
            }
            Flood2Piece piece = go.AddComponent<Flood2Piece>();
            piece.Initialize(id, kind, _stage.Friction, _stage.LineMaterial, kind == Flood2Material.Panel ? 0.6f : _sandbagRelativeDensity);
            _pieces.Add(piece);
            return piece;
        }

        /// <summary>Shapes the sandbag silhouette while keeping one rectangular collider per bag.</summary>
        private void AddSandbagVisual(Transform parent)
        {
            GameObject cloth = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            cloth.name = "Sandbag canvas";
            cloth.transform.SetParent(parent, false);
            cloth.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            cloth.transform.localScale = new Vector3(0.92f, 0.49f, 0.96f);
            Collider shape = cloth.GetComponent<Collider>();
            shape.enabled = false;
            Destroy(shape);
            Renderer renderer = cloth.GetComponent<Renderer>();
            renderer.sharedMaterial = _stage.SandbagMaterial;
            renderer.SetPropertyBlock(_sandbagTint);
        }

        /// <summary>Finds an editable body by its stable snapshot identifier.</summary>
        private Flood2Piece FindPiece(int id)
        {
            foreach (var piece in _pieces) if (piece.Id == id) return piece;
            return null;
        }

        /// <summary>Returns the current world position of a local adhesive sample.</summary>
        private Vector3 WorldPoint(Patch patch, Vector3 point)
        {
            Flood2Piece owner = FindPiece(patch.Owner);
            return owner != null ? owner.transform.TransformPoint(point) : point;
        }

        /// <summary>Starts one paid gesture, charges only drawn distance and retains the stroke when stock runs out.</summary>
        public void Paint(Collider surface, Vector3 point)
        {
            if (!_stage.CanEdit) return;
            Flood2Piece owner = surface.GetComponent<Flood2Piece>();
            if (owner == null && surface != _stage.Ground) { Hint = "설치 자재 또는 지면에만 도포할 수 있습니다."; return; }
            float distance = _hasPreviousPaint ? Vector3.Distance(_previousPaint, point) : 0f;
            if (_hasPreviousPaint && distance < 0.18f) return;
            bool newPatch = _currentPatch == null || _currentPatch.Owner != (owner != null ? owner.Id : -1) || distance > 1.5f;
            if (newPatch && _patches.Count >= MAX_PATCHES) { Hint = "도포 구간 한도 96개에 도달했습니다. Undo로 조정하세요."; return; }
            float cost = !_painting ? 0.5f : distance > 1.5f ? 0f : distance;
            if (Adhesive + 0.0001f < cost) { Hint = "접합재가 부족합니다. 이미 도포한 부분은 유지됩니다."; return; }
            if (!_painting) { _undo.Push(Capture()); _painting = true; }
            if (newPatch)
            {
                _currentPatch = new Patch { Id = _nextPatchId++, Owner = owner != null ? owner.Id : -1 };
                _patches.Add(_currentPatch);
                CreatePatchLine(_currentPatch);
            }
            Vector3 local = owner != null ? owner.transform.InverseTransformPoint(point) : point;
            _currentPatch.Points.Add(local);
            Adhesive = Mathf.Max(0f, Adhesive - cost);
            _previousPaint = point;
            _hasPreviousPaint = true;
            RefreshPatchLine(_currentPatch);
            ResolveContacts();
            Hint = $"접합재 {Adhesive:0.0}m · 젖은 선은 나중에 닿는 자재도 연결합니다.";
        }

        /// <summary>Creates the persistent visible adhesive line attached to its painted surface.</summary>
        private void CreatePatchLine(Patch patch)
        {
            GameObject go = new GameObject($"Adhesive {patch.Id}");
            Flood2Piece owner = FindPiece(patch.Owner);
            go.transform.SetParent(owner != null ? owner.transform : transform, false);
            patch.Line = go.AddComponent<LineRenderer>();
            patch.Line.sharedMaterial = _stage.LineMaterial;
            patch.Line.useWorldSpace = true;
            patch.Line.startWidth = patch.Line.endWidth = 0.13f;
            patch.Line.numCapVertices = 3;
            RefreshPatchLine(patch);
        }

        /// <summary>Updates paint appearance and follows the owner without physics trigger colliders.</summary>
        private void RefreshPatchLine(Patch patch)
        {
            int count = patch.Points.Count;
            patch.Line.positionCount = Mathf.Max(2, count);
            if (count == 0) return;
            Color color = patch.Dry ? new Color(1f, 0.76f, 0.27f) : new Color(0.46f, 0.24f, 0.1f);
            patch.Line.startColor = patch.Line.endColor = color;
            for (int i = 0; i < patch.Line.positionCount; i++)
                patch.Line.SetPosition(i, WorldPoint(patch, patch.Points[Mathf.Min(i, count - 1)]) + Vector3.up * 0.06f + (count == 1 && i == 1 ? Vector3.right * 0.08f : Vector3.zero));
        }

        /// <summary>Cures the nearest painted patch after one second; existing joint limits double only once.</summary>
        public void CureAt(Vector3 point, float dt)
        {
            if (!_stage.CanEdit) return;
            Patch nearest = null;
            float best = 0.8f * 0.8f;
            foreach (var patch in _patches)
            {
                if (patch.Dry) continue;
                foreach (var sample in patch.Points)
                {
                    float distance = (WorldPoint(patch, sample) - point).sqrMagnitude;
                    if (distance < best) { best = distance; nearest = patch; }
                }
            }
            if (nearest != _curingPatch) { _curingPatch = nearest; _cureTime = 0f; }
            if (nearest == null) return;
            _cureTime += dt;
            if (_cureTime < 1f) return;
            _undo.Push(Capture());
            nearest.Dry = true;
            foreach (var piece in _pieces) if (piece.PatchId == nearest.Id) piece.Cure();
            RefreshPatchLine(nearest);
            _curingPatch = null;
            _cureTime = 0f;
            Hint = "경화 완료 · 기존 접합 강도 2배, 이 선은 새로운 자재를 받지 않습니다.";
        }

        /// <summary>Finalizes all pending construction input before pause, tool changes or the flood checkpoint.</summary>
        public void FinishInput()
        {
            _painting = _hasPreviousPaint = false;
            _currentPatch = _curingPatch = null;
            _cureTime = 0f;
        }

        /// <summary>Processes only wet samples at 10Hz, rejecting used bodies before geometric contact checks.</summary>
        public void TickContacts(float dt)
        {
            _contactClock += dt;
            if (_contactClock < 0.1f) return;
            _contactClock = 0f;
            ResolveContacts();
        }

        /// <summary>Connects touching bodies without ever reattaching a previously bonded body.</summary>
        private void ResolveContacts()
        {
            foreach (var patch in _patches)
            {
                if (patch.Dry) continue;
                Flood2Piece owner = FindPiece(patch.Owner);
                foreach (Vector3 local in patch.Points)
                {
                    Vector3 point = WorldPoint(patch, local);
                    if (owner != null && !owner.EverBonded && point.y - Flood2Terrain.Height(point.x, point.z) < BRUSH_RADIUS)
                        owner.Bond(null, point, patch.Id, false);
                    foreach (var target in _pieces)
                    {
                        if (target == owner || target.EverBonded || !target.gameObject.activeSelf) continue;
                        Vector3 closest = target.Shape.ClosestPoint(point);
                        if ((closest - point).sqrMagnitude > BRUSH_RADIUS * BRUSH_RADIUS) continue;
                        if (owner == null && closest.y - Flood2Terrain.Height(closest.x, closest.z) > BRUSH_RADIUS) continue;
                        target.Bond(owner, closest, patch.Id, false);
                    }
                }
            }
        }

        private void LateUpdate()
        {
            foreach (var patch in _patches) if (patch.Owner >= 0) RefreshPatchLine(patch);
        }

        /// <summary>Returns a placed material to inventory; only user-created objects can be removed.</summary>
        public void Remove(Flood2Piece piece)
        {
            if (!_stage.CanEdit || piece == null) return;
            _undo.Push(Capture());
            if (piece.Kind == Flood2Material.Panel) Panels++; else Sandbags++;
            _patches.RemoveAll(patch => patch.Owner == piece.Id);
            foreach (var other in _pieces) other.RemoveSupport(piece);
            _pieces.Remove(piece);
            piece.gameObject.SetActive(false);
            Destroy(piece.gameObject);
        }

        /// <summary>Captures physical poses, joint topology, paint, stock and identifier counters.</summary>
        private Snapshot Capture()
        {
            var snapshot = new Snapshot { Panels = Panels, Sandbags = Sandbags, Adhesive = Adhesive, NextId = _nextId, NextPatchId = _nextPatchId };
            foreach (var piece in _pieces)
                snapshot.Pieces.Add(new PieceState { Id = piece.Id, Kind = piece.Kind, Position = piece.transform.position, Rotation = piece.transform.rotation,
                    Velocity = piece.Body.linearVelocity, Angular = piece.Body.angularVelocity, Bonded = piece.IsBonded, EverBonded = piece.EverBonded,
                    Dry = piece.IsDry, Active = piece.gameObject.activeSelf, Connected = piece.ConnectedId, PatchId = piece.PatchId, Anchor = piece.WorldAnchor });
            foreach (var patch in _patches) snapshot.Patches.Add(new Patch { Id = patch.Id, Owner = patch.Owner, Dry = patch.Dry, Points = new List<Vector3>(patch.Points) });
            return snapshot;
        }

        /// <summary>Recreates a snapshot in two passes so every restored joint targets a restored body.</summary>
        private void Restore(Snapshot snapshot)
        {
            FinishInput();
            foreach (var patch in _patches) { patch.Line.gameObject.SetActive(false); Destroy(patch.Line.gameObject); }
            foreach (var piece in _pieces) { piece.gameObject.SetActive(false); Destroy(piece.gameObject); }
            _patches.Clear(); _pieces.Clear();
            Panels = snapshot.Panels; Sandbags = snapshot.Sandbags; Adhesive = snapshot.Adhesive;
            _nextId = snapshot.NextId; _nextPatchId = snapshot.NextPatchId;
            _contactClock = 0f;
            foreach (var state in snapshot.Pieces)
            {
                Flood2Piece piece = Spawn(state.Id, state.Kind, state.Position, state.Rotation);
                piece.Body.linearVelocity = state.Velocity; piece.Body.angularVelocity = state.Angular;
                piece.gameObject.SetActive(state.Active);
            }
            Physics.SyncTransforms();
            foreach (var state in snapshot.Pieces)
            {
                Flood2Piece piece = FindPiece(state.Id);
                if (state.Bonded) piece.Bond(FindPiece(state.Connected), state.Anchor, state.PatchId, state.Dry);
                else if (state.EverBonded) piece.RestoreBroken(state.PatchId, state.Dry);
            }
            foreach (var original in snapshot.Patches)
            {
                var patch = new Patch { Id = original.Id, Owner = original.Owner, Dry = original.Dry, Points = new List<Vector3>(original.Points) };
                _patches.Add(patch); CreatePatchLine(patch);
            }
        }

        /// <summary>Undoes one complete edit, including its inventory and joint changes.</summary>
        public void Undo()
        {
            if (!_stage.CanEdit || _undo.Count == 0) return;
            Restore(_undo.Pop());
            Hint = "직전 편집과 재고를 복원했습니다.";
        }

        /// <summary>Saves the finalized pre-warning construction checkpoint.</summary>
        public void SaveCheckpoint() { FinishInput(); _checkpoint = Capture(); }

        /// <summary>Restores the last flood layout, or an empty new layout when requested.</summary>
        public void ResetLayout(bool empty)
        {
            if (empty || _checkpoint == null)
                Restore(new Snapshot { Panels = _startingPanels, Sandbags = _startingSandbags, Adhesive = _startingAdhesive });
            else Restore(_checkpoint);
            if (empty) _checkpoint = null;
            _undo.Clear();
        }

        private void OnDestroy()
        {
            if (_preview != null) Destroy(_preview);
        }
    }
}
