using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RestoreCastle3D
{
    [RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
    public sealed class CastlePiece3D : MonoBehaviour
    {
        [SerializeField] private string materialId;

        private readonly HashSet<CastlePiece3D> _contacts = new HashSet<CastlePiece3D>();
        private readonly HashSet<CastlePiece3D> _attachedPieces = new HashSet<CastlePiece3D>();
        private readonly Dictionary<CastlePiece3D, FixedJoint> _glueJoints = new Dictionary<CastlePiece3D, FixedJoint>();
        private CastlePiece3D _attachmentTarget;
        private CastleGame3D _game;
        private Rigidbody _body;
        private Renderer[] _renderers;
        private bool _fuseStarted;
        private bool _fusePending;

        public string MaterialId => materialId;
        public Rigidbody Body => _body;
        public IEnumerable<CastlePiece3D> Contacts => _contacts;
        public IEnumerable<CastlePiece3D> AttachedPieces => _attachedPieces;
        public bool HasAttachmentTarget => _attachmentTarget != null;
        public bool IsVine => materialId == "growing_vine" || materialId == "vine";
        public bool AbilitySuppressed { get; private set; }
        public bool FuseStarted => _fuseStarted;
        public bool FusePending => _fusePending;
        public int PlacedTurn { get; private set; }

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            SetOutlineLayer(0);
        }

        /// <summary>자식 렌더러의 외곽선 레이어만 교체하고 기본 렌더링 레이어는 보존한다.</summary>
        public void SetOutlineLayer(uint layer)
        {
            foreach (Renderer renderer in _renderers)
                renderer.renderingLayerMask = (renderer.renderingLayerMask & ~6u) | layer;
        }

        /// <summary>생성된 기물에 게임 소유자 및 설치된 턴을 연결한다.</summary>
        public void Initialize(CastleGame3D game, int turn)
        {
            _game = game;
            PlacedTurn = turn;
        }

        private void FixedUpdate()
        {
            if (_game != null && materialId != "foundation" &&
                transform.position.z <= _game.DestructionBoundaryZ)
                _game.RemoveOutsideBoundary(this);
        }

        private void OnCollisionEnter(Collision collision)
        {
            Register(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            Register(collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            CastlePiece3D other = collision.collider.GetComponentInParent<CastlePiece3D>();
            if (other == null || other == this) return;
            _contacts.Remove(other);
            other._contacts.Remove(this);
        }

        private void OnDestroy()
        {
            foreach (CastlePiece3D other in _attachedPieces)
                if (other != null) other._attachedPieces.Remove(this);
            _attachedPieces.Clear();
            foreach (KeyValuePair<CastlePiece3D, FixedJoint> bond in _glueJoints)
            {
                if (bond.Key != null) bond.Key._glueJoints.Remove(this);
                if (bond.Value != null) Destroy(bond.Value);
            }
            _glueJoints.Clear();
            foreach (CastlePiece3D other in _contacts)
                if (other != null) other._contacts.Remove(this);
            _contacts.Clear();
        }

        /// <summary>파도 재시도 시 이전 충돌 접촉만 정리하고 기존 결합 조인트는 유지한다.</summary>
        public void ClearContacts()
        {
            foreach (CastlePiece3D other in _contacts)
                if (other != null) other._contacts.Remove(this);
            _contacts.Clear();
        }

        /// <summary>충돌한 두 기물을 양방향 접촉 그래프에 기록한다. 바닥 충돌은 제외한다.</summary>
        private void Register(Collision collision)
        {
            CastlePiece3D other = collision.collider.GetComponentInParent<CastlePiece3D>();
            if (other == null || other == this) return;
            _contacts.Add(other);
            other._contacts.Add(this);
            _game?.TryAttachGlue(this, other);
        }

        /// <summary>아직 같은 조인트 연결망에 속하지 않은 대상만 접착하고 결합을 양쪽에 등록한다.</summary>
        public bool TryGlueTo(CastlePiece3D target)
        {
            if (materialId != "all_purpose_glue" || target == null || target == this ||
                _body == null || target.Body == null || _glueJoints.ContainsKey(target) ||
                IsJointConnectedTo(target))
                return false;

            FixedJoint joint = gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = target.Body;
            // Keep collision contacts so adjacency-based durability effects still work.
            joint.enableCollision = true;
            joint.breakForce = Mathf.Infinity;
            joint.breakTorque = Mathf.Infinity;
            _glueJoints.Add(target, joint);
            target._glueJoints.Add(this, joint);
            return true;
        }

        /// <summary>접착제와 덩굴의 기존 조인트를 따라 대상까지 이어지는 경로가 있는지 확인한다.</summary>
        private bool IsJointConnectedTo(CastlePiece3D target)
        {
            HashSet<CastlePiece3D> visited = new HashSet<CastlePiece3D> { this };
            Stack<CastlePiece3D> pending = new Stack<CastlePiece3D>();
            pending.Push(this);

            while (pending.Count > 0)
            {
                CastlePiece3D current = pending.Pop();
                foreach (KeyValuePair<CastlePiece3D, FixedJoint> bond in current._glueJoints)
                {
                    CastlePiece3D neighbor = bond.Key;
                    if (neighbor == null || bond.Value == null) continue;
                    if (neighbor == target) return true;
                    if (visited.Add(neighbor)) pending.Push(neighbor);
                }

                foreach (CastlePiece3D neighbor in current._attachedPieces)
                {
                    if (neighbor == null) continue;
                    if (neighbor == target) return true;
                    if (visited.Add(neighbor)) pending.Push(neighbor);
                }
            }

            return false;
        }

        /// <summary>첫 접촉 기물과 한 번만 FixedJoint 결합하고 고유 능력을 봉인한다.</summary>
        public bool TryAttachTo(CastlePiece3D target)
        {
            if (!IsVine || AbilitySuppressed || HasAttachmentTarget ||
                target == null || target == this || target.MaterialId == "foundation" ||
                _body == null || target.Body == null)
                return false;

            _attachmentTarget = target;
            _attachedPieces.Add(target);
            target._attachedPieces.Add(this);

            FixedJoint joint = gameObject.AddComponent<FixedJoint>();
            joint.connectedBody = target.Body;
            joint.enableCollision = false;
            joint.breakForce = Mathf.Infinity;
            joint.breakTorque = Mathf.Infinity;

            target.SuppressAbilities();
            return true;
        }

        /// <summary>대상의 기본 보강력은 유지하되 기존·미래의 특수능력 효과를 없앤다.</summary>
        private void SuppressAbilities()
        {
            AbilitySuppressed = true;
            // An attached barrel cannot finish a fuse or detonate later.
            if (materialId == "barrel" && _fusePending)
            {
                StopAllCoroutines();
                _fusePending = false;
            }
        }

        /// <summary>첫 충돌부터 1초 경과 시 소유자에게 폭발을 요청한다.</summary>
        private IEnumerator Detonate()
        {
            yield return new WaitForSeconds(1f);
            if (_game != null && !_game.IsAbilityDisabled(this)) _game.Explode(this);
            // The barrel persists after exploding. Do not leave it flagged as
            // an unfinished fuse during every future round's settlement.
            _fusePending = false;
        }
    }
}
