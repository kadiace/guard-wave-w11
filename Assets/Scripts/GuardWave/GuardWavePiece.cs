using UnityEngine;

namespace GuardWave
{
    /// <summary>Selectable Rigidbody that supports outline highlighting, removal and scene rollback.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class GuardWavePiece : MonoBehaviour
    {
        [SerializeField] private string materialId;
        [SerializeField] private bool removeOutsideBoundary = true;

        [Header("Fluid interaction")]
        [Tooltip("Relative to the stage fluid: below 1 floats, 1 neutral, above 1 sinks.")]
        [SerializeField, Min(0.01f)] private float relativeFluidDensity = 0.6f;
        // Missing serialized fields in older scene objects resolve to the log default.
        public float RelativeFluidDensity =>
            Mathf.Max(0.01f, relativeFluidDensity > 0f ? relativeFluidDensity : 0.6f);

        // Static supports reduce current following only; buoyancy stays active.
        private float _lastGroundContactTime = float.NegativeInfinity;
        public bool IsRestingOnGround => Time.fixedTime - _lastGroundContactTime
            <= Time.fixedDeltaTime * 1.5f;

        private GuardWaveGame _game;
        private Rigidbody _body;
        private Renderer[] _renderers;

        public string MaterialId => materialId;
        // Awake is not guaranteed to run before another component queries an
        // object that starts inactive in the authored scene.
        public Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());

        private void Awake()
        {
            CacheComponents();
            SetOutlineLayer(0);
        }

        private void CacheComponents()
        {
            _ = Body;
            if (_renderers == null)
                _renderers = GetComponentsInChildren<Renderer>(true);
        }

        /// <summary>Registers both runtime-placed pieces and authored scene sticks.</summary>
        public void Initialize(GuardWaveGame game)
        {
            CacheComponents();
            _game = game;
        }

        /// <summary>Only change the hover rendering bit, preserving the original rendering layers.</summary>
        public void SetOutlineLayer(uint layer)
        {
            CacheComponents();
            foreach (Renderer renderer in _renderers)
                if (renderer != null)
                    renderer.renderingLayerMask = (renderer.renderingLayerMask & ~2u) | layer;
        }

        /// <summary>Removes any registered piece that crosses the Forest destruction boundary.</summary>
        private void FixedUpdate()
        {
            // The flag has a dedicated failure lifecycle; do not remove it
            // as an ordinary piece at the outflow boundary.
            if (_game == null || !removeOutsideBoundary || materialId == "foundation" ||
                materialId == "default_flag" || !_game.IsPlacedPiece(this)) return;
            if (transform.position.z <= _game.DestructionBoundaryZ)
                _game.RemoveOutsideBoundary(this);
        }

        private void OnCollisionEnter(Collision collision) => RecordGroundContact(collision);
        private void OnCollisionStay(Collision collision) => RecordGroundContact(collision);

        private void RecordGroundContact(Collision collision)
        {
            Rigidbody other = collision.rigidbody;
            if (other != null && !other.isKinematic) return;
            for (int i = 0; i < collision.contactCount; i++)
            {
                if (collision.GetContact(i).normal.y <= 0.5f) continue;
                _lastGroundContactTime = Time.fixedTime;
                break;
            }
        }

        private void OnDisable() => _lastGroundContactTime = float.NegativeInfinity;

        /// <summary>Invoked on a completed right-click; dragging continues to pan the camera.</summary>
        public void RemoveByRightClick()
        {
            _game?.RemovePieceByRightClick(this);
        }
    }
}
