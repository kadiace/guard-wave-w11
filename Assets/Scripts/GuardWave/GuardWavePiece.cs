using UnityEngine;

namespace GuardWave
{
    /// <summary>Selectable Rigidbody that supports outline highlighting, removal and scene rollback.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class GuardWavePiece : MonoBehaviour
    {
        [SerializeField] private string materialId;
        [SerializeField] private bool removeOutsideBoundary = true;

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
            if (_game == null || !removeOutsideBoundary || materialId == "foundation" ||
                !_game.IsPlacedPiece(this)) return;
            if (transform.position.z <= _game.DestructionBoundaryZ)
                _game.RemoveOutsideBoundary(this);
        }

        /// <summary>Invoked on a completed right-click; dragging continues to pan the camera.</summary>
        public void RemoveByRightClick()
        {
            _game?.RemovePieceByRightClick(this);
        }
    }
}
