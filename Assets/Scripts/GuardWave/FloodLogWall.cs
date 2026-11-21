using UnityEngine;

namespace GuardWave
{
    /// <summary>A purchased Flood2 fence shares durability across its posts.</summary>
    public sealed class FloodLogWall : MonoBehaviour
    {
        public const int STARTING_BUDGET = 120;
        public const int PRICE = 20;
        private const int MAX_HEALTH = 600;
        private const float PLACEMENT_GRACE_SECONDS = 0.5f;

        [Header("Wall")]
        [SerializeField] private Rigidbody _logPrefab;
        [SerializeField] private RectTransform _healthBar;
        [SerializeField] private RectTransform _fill;
        private GuardWaveGame _game;
        private Transform _camera;
        private DefenseLogHitFeedback[] _posts;
        private GameObject _debris;
        private double _damageStartsAt;

        internal bool IsPlacementProtected => Time.timeAsDouble < _damageStartsAt;

        public int Slot { get; private set; }
        public int Health { get; private set; } = MAX_HEALTH;

        /// <summary>Assigns the purchase slot and prepares a visual pulse for each post.</summary>
        public void Place(GuardWaveGame game, int slot)
        {
            _game = game;
            Slot = slot;
            _damageStartsAt = Time.timeAsDouble + PLACEMENT_GRACE_SECONDS;
            _camera = Camera.main.transform;
            MeshCollider[] posts = GetComponentsInChildren<MeshCollider>();
            _posts = new DefenseLogHitFeedback[posts.Length];
            for (int i = 0; i < posts.Length; i++)
            {
                _posts[i] = posts[i].gameObject.AddComponent<DefenseLogHitFeedback>();
                _posts[i].enabled = false;
            }
        }

        private void LateUpdate()
        {
            _healthBar.rotation = _camera.rotation;
        }

        /// <summary>Applies the newly reported particle contacts for this wall.</summary>
        public void TakeDamage(int amount)
        {
            if (amount <= 0 || Health == 0) return;
            SetHealth(Health - amount);
            foreach (DefenseLogHitFeedback post in _posts) post.Play();
            if (Health > 0) return;

            _debris = new GameObject("Collapsed Log Wall");
            foreach (DefenseLogHitFeedback post in _posts)
            {
                Transform pose = post.transform;
                Rigidbody log = Instantiate(_logPrefab, pose.position, pose.rotation, _debris.transform);
                log.transform.localScale = pose.lossyScale;
                // Keep the mesh collider as a FLIP proxy while excluding all PhysX contacts.
                log.excludeLayers = ~0;
                log.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");
                _game.RegisterFluidDebris(log.GetComponent<GuardWavePiece>());
                log.gameObject.AddComponent<DefenseLogHitFeedback>().Play();
            }
            Destroy(_debris, 3f);
            _game.RemoveBrokenWall(GetComponent<GuardWavePiece>());
        }

        /// <summary>Restores a saved wall and removes debris from the discarded timeline.</summary>
        public void RestoreHealth(int health)
        {
            if (_debris != null) Destroy(_debris);
            SetHealth(health);
        }

        /// <summary>Updates the world-space gauge from the remaining durability.</summary>
        private void SetHealth(int value)
        {
            Health = Mathf.Clamp(value, 0, MAX_HEALTH);
            _fill.anchorMax = new Vector2((float)Health / MAX_HEALTH, 1f);
        }

        private void OnDestroy()
        {
            if (_debris != null) Destroy(_debris);
        }
    }
}
