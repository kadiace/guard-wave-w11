using System.Collections.Generic;

using UnityEngine;

using UnityEngine.InputSystem;

using GuardWave;

namespace Avalanche
{
    public enum AvalancheTool { None = -1, Panel, Sandbag, Adhesive }

    /// <summary>Manages placement, contact adhesion and editable-layout snapshots.</summary>
    public sealed class AvalancheConstruction : MonoBehaviour
    {
        private static readonly Color SANDBAG_COLOR = new Color(0.66f, 0.54f, 0.30f);
        private static readonly Color ADHESIVE_COLOR = new Color(0.95f, 0.68f, 0.18f);

        [Header("Placement")]
        [SerializeField] private Vector3 _panelSize = new Vector3(3.6f, 1.2f, 0.6f);
        [SerializeField] private Vector3 _sandbagSize = new Vector3(1.4f, 0.55f, 1.1f);
        [SerializeField, Min(0.01f)] private float _placementGrid = 0.3f;
        [SerializeField, Range(1f, 180f)] private float _rotationStep = 15f;
        [SerializeField, Min(0.01f)] private float _continuousPlacementSeconds = 0.1f;

        [Header("Citizen protection")]
        [SerializeField, Min(0f)] private float _citizenClearance = 0.6f;

        [Header("Adhesive blocks")]
        [SerializeField, Min(0.01f)] private float _adhesiveRadius = 0.48f;
        [SerializeField, Min(0.02f)] private float _contactCheckSeconds = 0.1f;

        [Header("Rigidbody and joint settings")]
        [SerializeField] private AvalanchePiece.PhysicsSettings _piecePhysics = new AvalanchePiece.PhysicsSettings();
        private MaterialPropertyBlock _sandbagTint;
        private MaterialPropertyBlock _adhesiveTint;
        private readonly List<AvalanchePiece> _pieces = new List<AvalanchePiece>();
        private readonly List<AdhesiveBlock> _adhesives = new List<AdhesiveBlock>();
        private readonly StageEditHistory<Snapshot> _undo = new StageEditHistory<Snapshot>();
        private readonly Collider[] _overlaps = new Collider[64];
        private Collider[] _contacts = new Collider[64];
        private AvalancheStage _stage;
        private int _startingPanels;
        private int _startingSandbags;
        private int _startingAdhesive;
        private GameObject _preview;
        private Renderer _previewRenderer;
        private MaterialPropertyBlock _previewTint;
        private Snapshot _checkpoint;
        private int _nextId;
        private int _nextAdhesiveId;
        private float _yaw;
        private float _contactClock;
        private Vector3 _candidate;
        private bool _candidateValid;
        private bool _rightGesture;
        private bool _rightMoved;
        private Vector2 _rightOrigin;
        private float _lastPlaced;
        public IReadOnlyList<AvalanchePiece> Pieces => _pieces;
        public AvalancheTool Tool { get; private set; } = AvalancheTool.None;
        public int Panels { get; private set; }
        public int Sandbags { get; private set; }
        public int Adhesive { get; private set; }
        public int StartingPanels => _startingPanels;
        public int StartingSandbags => _startingSandbags;
        public int StartingAdhesive => _startingAdhesive;
        public int BondCount { get { int n = 0; foreach (var p in _pieces) if (p.IsBonded) n++; return n; } }
        public string Hint { get; private set; } = "유도 방벽으로 눈사태를 돌리고 하중 받침으로 방어선을 보강하세요.";
        public bool HasCheckpoint => _checkpoint != null;

        private sealed class AdhesiveBlock
        {
            public int Id { get; set; }
            public AvalanchePiece Owner { get; set; }
            public Vector3 Point { get; set; }
            public GameObject Visual { get; set; }
            public bool Active => Owner == null || Owner.gameObject.activeSelf;
            public Vector3 WorldPoint => Owner != null ? Owner.transform.TransformPoint(Point) : Point;
        }

        private sealed class AdhesiveState
        {
            public int Id { get; set; }
            public int Owner { get; set; }
            public Vector3 Point { get; set; }
        }

        private sealed class PieceState
        {
            public int Id { get; set; }
            public AvalancheMaterial Kind { get; set; }
            public Vector3 Position { get; set; }
            public Quaternion Rotation { get; set; }
            public Vector3 Velocity { get; set; }
            public Vector3 Angular { get; set; }
            public bool Bonded { get; set; }
            public bool EverBonded { get; set; }
            public bool Active { get; set; }
            public int Connected { get; set; }
            public int AdhesiveId { get; set; }
            public Vector3 Anchor { get; set; }
        }

        private sealed class Snapshot
        {
            public List<PieceState> Pieces { get; set; } = new List<PieceState>();
            public List<AdhesiveState> Adhesives { get; set; } = new List<AdhesiveState>();
            public int Panels { get; set; }
            public int Sandbags { get; set; }
            public int Adhesive { get; set; }
            public int NextId { get; set; }
            public int NextAdhesiveId { get; set; }
        }

        /// <summary>Creates the non-colliding placement preview and initializes the stage-specific tools.</summary>
        public void Initialize(AvalancheStage stage)
        {
            _stage = stage;
            _startingPanels = GetStartingInventory(stage.StageInfo, "panel");
            _startingSandbags = GetStartingInventory(stage.StageInfo, "sandbag");
            _startingAdhesive = GetStartingInventory(stage.StageInfo, "adhesive");
            Panels = _startingPanels;
            Sandbags = _startingSandbags;
            Adhesive = _startingAdhesive;
            _sandbagTint = new MaterialPropertyBlock();
            _sandbagTint.SetColor("_BaseColor", SANDBAG_COLOR);
            _adhesiveTint = new MaterialPropertyBlock();
            _adhesiveTint.SetColor("_BaseColor", ADHESIVE_COLOR);
            _preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _preview.name = "Placement preview";
            _preview.layer = 2;
            _preview.GetComponent<Collider>().enabled = false;
            _previewRenderer = _preview.GetComponent<Renderer>();
            _previewRenderer.sharedMaterial = stage.PreviewMaterial;
            _previewTint = new MaterialPropertyBlock();
            _preview.SetActive(false);
        }

        /// <summary>Returns the configured finite inventory for an Avalanche material.</summary>
        private static int GetStartingInventory(GuardWaveStageInfo stage, string materialId)
        {
            int limit = stage.GetPlacementLimit(materialId);
            if (limit >= 0) return limit;
            throw new System.InvalidOperationException("Missing finite Avalanche inventory: " + materialId);
        }

        /// <summary>Returns the remaining stock for a shared construction card.</summary>
        public int GetRemainingPlacements(string materialId)
        {
            if (materialId == "panel") return Panels;
            if (materialId == "sandbag") return Sandbags;
            if (materialId == "adhesive") return Adhesive;
            throw new System.InvalidOperationException("Unsupported Avalanche material: " + materialId);
        }

        /// <summary>Clears the active palette selection and hides the placement projection.</summary>
        public bool CancelSelection()
        {
            if (Tool == AvalancheTool.None) return false;
            Tool = AvalancheTool.None;
            _preview.SetActive(false);
            return true;
        }

        /// <summary>Returns the world dimensions of a catalog item.</summary>
        private Vector3 Size(AvalancheMaterial kind) =>
            Vector3.Max(kind == AvalancheMaterial.Panel ? _panelSize : _sandbagSize, Vector3.one * 0.1f);

        /// <summary>Switches tools and explains the selected placement or removal action.</summary>
        public void SelectTool(AvalancheTool tool)
        {
            Tool = tool;
            Hint = tool == AvalancheTool.Adhesive ? "지면이나 자재를 클릭해 접합 블록을 설치하세요. 닿는 자재를 바로 고정합니다."
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
                return;
            }
            if (keyboard != null)
            {
                if (keyboard.digit1Key.wasPressedThisFrame) SelectTool(AvalancheTool.Panel);
                if (keyboard.digit2Key.wasPressedThisFrame) SelectTool(AvalancheTool.Sandbag);
                if (keyboard.digit3Key.wasPressedThisFrame) SelectTool(AvalancheTool.Adhesive);
                if (keyboard.qKey.wasPressedThisFrame) _yaw -= _rotationStep;
                if (keyboard.eKey.wasPressedThisFrame) _yaw += _rotationStep;
                if (keyboard.zKey.wasPressedThisFrame && keyboard.ctrlKey.isPressed) Undo();
            }
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _rightGesture = !_stage.Hud.BlocksPointer(mouse.position.ReadValue());
                _rightMoved = false;
                _rightOrigin = mouse.position.ReadValue();
            }
            if (_rightGesture && ((mouse.position.ReadValue() - _rightOrigin).sqrMagnitude > 36f || Mathf.Abs(mouse.scroll.ReadValue().y) > 0.01f)) _rightMoved = true;
            if (_stage.Hud.BlocksPointer(mouse.position.ReadValue())) { _preview.SetActive(false); if (mouse.rightButton.wasReleasedThisFrame) _rightGesture = false; return; }
            Ray ray = _stage.ViewCamera.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = Physics.Raycast(ray, out RaycastHit hit, 300f, ~4, QueryTriggerInteraction.Ignore);
            if (mouse.rightButton.wasReleasedThisFrame)
            {
                if (_rightGesture && !_rightMoved)
                {
                    if (!CancelSelection()) RemoveAt(ray, hasHit, hit);
                }
                _rightGesture = false;
                return;
            }
            if (mouse.rightButton.isPressed || Tool == AvalancheTool.None) { _preview.SetActive(false); return; }
            if (!hasHit) { _preview.SetActive(false); return; }
            if (Tool == AvalancheTool.Panel || Tool == AvalancheTool.Sandbag)
            {
                AvalancheMaterial kind = Tool == AvalancheTool.Panel ? AvalancheMaterial.Panel : AvalancheMaterial.Sandbag;
                Vector3 size = Size(kind);
                _candidate = hit.point;
                if (keyboard == null || !keyboard.shiftKey.isPressed)
                {
                    _candidate.x = Mathf.Round(_candidate.x / _placementGrid) * _placementGrid;
                    _candidate.z = Mathf.Round(_candidate.z / _placementGrid) * _placementGrid;
                }
                float support = hit.point.y;
                Quaternion rotation = Quaternion.Euler(0f, _yaw, 0f);
                for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    Vector3 p = _candidate + rotation * new Vector3(x * size.x * 0.5f, 0, z * size.z * 0.5f);
                    support = Mathf.Max(support, AvalancheTerrain.Height(p.x, p.z));
                }
                _candidate.y = support + size.y * 0.5f + 0.035f;
                _candidateValid = CanPlace(kind, _candidate, rotation, out string reason);
                Hint = _candidateValid ? $"클릭: 설치  /  Q·E: {_rotationStep:0.#}° 회전  /  Shift: 정밀 배치" : reason;
                StagePlacement.ShowPreview(_preview, _candidate, rotation);
                _preview.transform.localScale = size;
                _previewTint.SetColor("_BaseColor", _candidateValid ? new Color(0.2f, 0.95f, 0.7f, 0.45f) : new Color(1f, 0.25f, 0.2f, 0.45f));
                _previewRenderer.SetPropertyBlock(_previewTint);
                if (mouse.leftButton.isPressed && Time.unscaledTime - _lastPlaced >= _continuousPlacementSeconds && TryPlace(kind, _candidate, _yaw)) _lastPlaced = Time.unscaledTime;
            }
            else
            {
                _preview.SetActive(false);
                if (mouse.leftButton.wasPressedThisFrame) TryPlaceAdhesive(hit.collider, hit.point);
            }
        }

        /// <summary>Checks inventory, map limits, citizen clearance and actual collider penetration.</summary>
        private bool CanPlace(AvalancheMaterial kind, Vector3 center, Quaternion rotation, out string reason)
        {
            reason = "";
            if (kind == AvalancheMaterial.Panel ? Panels <= 0 : Sandbags <= 0) { reason = "이 자재의 재고가 없습니다."; return false; }
            Vector3 size = Size(kind);
            Vector3 extents = new Vector3(Mathf.Abs((rotation * Vector3.right).x) * size.x + Mathf.Abs((rotation * Vector3.forward).x) * size.z, 0f,
                Mathf.Abs((rotation * Vector3.right).z) * size.x + Mathf.Abs((rotation * Vector3.forward).z) * size.z) * 0.5f;
            if (Mathf.Abs(center.x) + extents.x > 17.4f || center.z - extents.z < 1f || center.z + extents.z >= 48f)
            { reason = "표시된 마을 안에 설치하세요. 상류 유입부는 비워 둡니다."; return false; }
            foreach (var citizen in _stage.Citizens)
            {
                Vector3 local = Quaternion.Inverse(rotation) * (citizen.transform.position - center);
                if (Mathf.Abs(local.x) < size.x * 0.5f + _citizenClearance && Mathf.Abs(local.z) < size.z * 0.5f + _citizenClearance && center.y - size.y / 2f < citizen.transform.position.y + 1.8f)
                { reason = $"{_stage.TargetName}의 대기 공간을 비워 주세요."; return false; }
            }
            int count = Physics.OverlapBoxNonAlloc(center, size * 0.5f - Vector3.one * 0.025f, _overlaps, rotation, ~4, QueryTriggerInteraction.Ignore);
            if (count > 0) { reason = "자재나 지형과 겹칩니다. 위 또는 옆으로 옮겨 주세요."; return false; }
            return true;
        }

        /// <summary>Places a validated real Rigidbody and records a resource-aware undo snapshot.</summary>
        public bool TryPlace(AvalancheMaterial kind, Vector3 center, float yaw)
        {
            if (!_stage.CanEdit || !CanPlace(kind, center, Quaternion.Euler(0f, yaw, 0f), out string reason)) return false;
            _undo.Add(Capture());
            Spawn(_nextId++, kind, center, Quaternion.Euler(0f, yaw, 0f));
            if (kind == AvalancheMaterial.Panel) Panels--; else Sandbags--;
            StagePlacement.SyncPhysics();
            return true;
        }

        /// <summary>Instantiates a catalog body; callers handle validation and stock.</summary>
        private AvalanchePiece Spawn(int id, AvalancheMaterial kind, Vector3 position, Quaternion rotation)
        {
            GameObject go = StagePlacement.CreateCube(transform, position, rotation);
            go.name = $"{kind} {id:00}";
            go.transform.localScale = Size(kind);
            Renderer solid = go.GetComponent<Renderer>();
            if (kind == AvalancheMaterial.Panel) solid.sharedMaterial = _stage.PanelMaterial;
            else
            {
                solid.enabled = false;
                AddSandbagVisual(go.transform);
            }
            AvalanchePiece piece = go.AddComponent<AvalanchePiece>();
            piece.Initialize(id, kind, _stage.Friction, _stage.LineMaterial, _piecePhysics);
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
        private AvalanchePiece FindPiece(int id)
        {
            foreach (var piece in _pieces) if (piece.Id == id) return piece;
            return null;
        }

        /// <summary>Installs one adhesive block on an eligible surface and checks its immediate contacts.</summary>
        public bool TryPlaceAdhesive(Collider surface, Vector3 point)
        {
            if (!_stage.CanEdit) return false;
            if (Adhesive <= 0)
            {
                Hint = "접합 블록이 모두 사용되었습니다.";
                return false;
            }

            if (Mathf.Abs(point.x) > 17.4f || point.z < 1f || point.z >= 48f)
            {
                Hint = "접합 블록은 배치 허용 구역에만 설치할 수 있습니다.";
                return false;
            }

            AvalanchePiece owner = surface.GetComponent<AvalanchePiece>();
            if (owner == null && surface != _stage.Ground)
            {
                Hint = "지면이나 설치한 자재에 접합 블록을 놓으세요.";
                return false;
            }

            _undo.Add(Capture());
            Vector3 local = owner != null ? owner.transform.InverseTransformPoint(point) : point;
            AdhesiveBlock block = SpawnAdhesive(_nextAdhesiveId++, owner, local);
            Adhesive--;
            ResolveContacts(block);
            Hint = $"접합 블록 {Adhesive}개 남음 · 나중에 닿는 자재도 고정합니다.";
            return true;
        }

        /// <summary>Creates a visible connector with a spherical contact region that follows its mounting surface.</summary>
        private AdhesiveBlock SpawnAdhesive(int id, AvalanchePiece owner, Vector3 point)
        {
            var block = new AdhesiveBlock { Id = id, Owner = owner, Point = point };
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = $"Adhesive block {id:00}";
            visual.transform.SetParent(transform, false);
            visual.transform.position = block.WorldPoint;
            visual.transform.localScale = Vector3.one * (_adhesiveRadius * 2f);
            Collider collider = visual.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            Renderer renderer = visual.GetComponent<Renderer>();
            renderer.sharedMaterial = _stage.PanelMaterial;
            renderer.SetPropertyBlock(_adhesiveTint);
            visual.SetActive(block.Active);
            block.Visual = visual;
            _adhesives.Add(block);
            return block;
        }

        /// <summary>Checks existing blocks at the configured interval so moving pieces can attach later.</summary>
        public void TickContacts(float dt)
        {
            if (_adhesives.Count == 0 || _pieces.Count == 0) return;
            _contactClock += dt;
            if (_contactClock < _contactCheckSeconds) return;
            _contactClock = 0f;
            foreach (var block in _adhesives) ResolveContacts(block);
        }

        /// <summary>Checks solid overlaps near one block and immediately creates full-strength bonds.</summary>
        private void ResolveContacts(AdhesiveBlock block)
        {
            if (!block.Active || _pieces.Count == 0) return;
            AvalanchePiece owner = block.Owner;
            Vector3 point = block.WorldPoint;
            int count = Physics.OverlapSphereNonAlloc(point, _adhesiveRadius, _contacts, ~4, QueryTriggerInteraction.Ignore);
            while (count == _contacts.Length)
            {
                _contacts = new Collider[_contacts.Length * 2];
                count = Physics.OverlapSphereNonAlloc(point, _adhesiveRadius, _contacts, ~4, QueryTriggerInteraction.Ignore);
            }

            if (owner != null && !owner.EverBonded)
            {
                for (int i = 0; i < count; i++)
                {
                    if (_contacts[i] != _stage.Ground) continue;
                    owner.Bond(null, point, block.Id);
                    break;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (!_contacts[i].TryGetComponent(out AvalanchePiece target) || target == owner || target.EverBonded) continue;
                if (owner != null && owner.IsBonded && owner.ConnectedId == target.Id) continue;
                target.Bond(owner, target.Shape.ClosestPoint(point), block.Id);
            }
        }

        private void LateUpdate()
        {
            foreach (var block in _adhesives)
            {
                bool active = block.Active;
                if (block.Visual.activeSelf != active) block.Visual.SetActive(active);
                if (active && block.Owner != null) block.Visual.transform.position = block.WorldPoint;
            }
        }

        /// <summary>Selects the nearest visible connector or solid hit, allowing connectors to be retrieved separately.</summary>
        private void RemoveAt(Ray ray, bool hasHit, RaycastHit hit)
        {
            AdhesiveBlock nearest = null;
            float distance = hasHit ? hit.distance : 300f;
            foreach (var block in _adhesives)
            {
                if (!block.Active) continue;
                Vector3 offset = block.WorldPoint - ray.origin;
                float projection = Vector3.Dot(offset, ray.direction);
                float discriminant = _adhesiveRadius * _adhesiveRadius - (offset.sqrMagnitude - projection * projection);
                if (projection < 0f || discriminant < 0f) continue;
                float front = Mathf.Max(0f, projection - Mathf.Sqrt(discriminant));
                if (front >= distance) continue;
                nearest = block;
                distance = front;
            }

            if (nearest != null)
            {
                _undo.Add(Capture());
                RemoveAdhesive(nearest);
                Hint = "접합 블록과 해당 연결을 회수했습니다.";
            }
            else if (hasHit) Remove(hit.collider.GetComponent<AvalanchePiece>());
        }

        /// <summary>Returns one block to inventory and releases every bond it created.</summary>
        private void RemoveAdhesive(AdhesiveBlock block)
        {
            foreach (var piece in _pieces) piece.RemoveAdhesive(block.Id);
            _adhesives.Remove(block);
            block.Visual.SetActive(false);
            Destroy(block.Visual);
            Adhesive++;
        }

        /// <summary>Returns a placed material to inventory; only user-created objects can be removed.</summary>
        public void Remove(AvalanchePiece piece)
        {
            if (!_stage.CanEdit || piece == null) return;
            _undo.Add(Capture());
            if (piece.Kind == AvalancheMaterial.Panel) Panels++; else Sandbags++;
            for (int i = _adhesives.Count - 1; i >= 0; i--)
                if (_adhesives[i].Owner == piece) RemoveAdhesive(_adhesives[i]);
            foreach (var other in _pieces) other.RemoveSupport(piece);
            _pieces.Remove(piece);
            piece.gameObject.SetActive(false);
            Destroy(piece.gameObject);
        }

        /// <summary>Captures physical poses, joint topology, placed connectors and stock.</summary>
        private Snapshot Capture()
        {
            var snapshot = new Snapshot { Panels = Panels, Sandbags = Sandbags, Adhesive = Adhesive, NextId = _nextId, NextAdhesiveId = _nextAdhesiveId };
            foreach (var piece in _pieces)
                snapshot.Pieces.Add(new PieceState { Id = piece.Id, Kind = piece.Kind, Position = piece.transform.position, Rotation = piece.transform.rotation,
                    Velocity = piece.Body.linearVelocity, Angular = piece.Body.angularVelocity, Bonded = piece.IsBonded, EverBonded = piece.EverBonded,
                    Active = piece.gameObject.activeSelf, Connected = piece.ConnectedId, AdhesiveId = piece.AdhesiveId, Anchor = piece.WorldAnchor });
            foreach (var block in _adhesives)
                snapshot.Adhesives.Add(new AdhesiveState { Id = block.Id, Owner = block.Owner != null ? block.Owner.Id : -1, Point = block.Point });
            return snapshot;
        }

        /// <summary>Recreates a snapshot in two passes so every restored joint targets a restored body.</summary>
        private void Restore(Snapshot snapshot)
        {
            foreach (var block in _adhesives)
            {
                block.Visual.SetActive(false);
                Destroy(block.Visual);
            }
            foreach (var piece in _pieces) { piece.gameObject.SetActive(false); Destroy(piece.gameObject); }
            _adhesives.Clear(); _pieces.Clear();
            Panels = snapshot.Panels; Sandbags = snapshot.Sandbags; Adhesive = snapshot.Adhesive;
            _nextId = snapshot.NextId; _nextAdhesiveId = snapshot.NextAdhesiveId;
            _contactClock = 0f;
            foreach (var state in snapshot.Pieces)
            {
                AvalanchePiece piece = Spawn(state.Id, state.Kind, state.Position, state.Rotation);
                piece.Body.linearVelocity = state.Velocity; piece.Body.angularVelocity = state.Angular;
                piece.gameObject.SetActive(state.Active);
            }
            Physics.SyncTransforms();
            foreach (var state in snapshot.Pieces)
            {
                AvalanchePiece piece = FindPiece(state.Id);
                if (state.Bonded) piece.Bond(FindPiece(state.Connected), state.Anchor, state.AdhesiveId);
                else if (state.EverBonded) piece.RestoreBroken(state.AdhesiveId);
            }
            foreach (var original in snapshot.Adhesives)
                SpawnAdhesive(original.Id, FindPiece(original.Owner), original.Point);
        }

        /// <summary>Undoes one complete edit, including its inventory and joint changes.</summary>
        public void Undo()
        {
            if (!_stage.CanEdit || !_undo.TryUndo(false, out Snapshot snapshot)) return;
            Restore(snapshot);
            Hint = "직전 편집과 재고를 복원했습니다.";
        }

        /// <summary>Saves the finalized pre-warning construction checkpoint.</summary>
        public void SaveCheckpoint() => _checkpoint = Capture();

        /// <summary>Restores construction or empties player-built objects, leaving scene-authored citizens intact.</summary>
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
