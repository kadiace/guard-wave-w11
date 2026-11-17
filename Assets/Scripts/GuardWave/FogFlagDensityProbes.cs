using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Density detection dots around the three child colliders of the Defense Flag.
    /// Probe coordinates are rebuilt in world space, so moving/rotating the flag
    /// also moves its samples. No physics triggers or runtime GameObjects are needed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FogFlagDensityProbes : MonoBehaviour
    {
        public const int MaxSamples = 40;

        [Header("Density probe dots (outside the gas solver's solid voxels)")]
        [SerializeField, Min(0.15f)] private float _surfaceClearance = 0.55f;
        [SerializeField] private bool _drawGizmos = true;

        private Collider[] _colliders;

        private void Awake() => CacheColliders();

        private void CacheColliders()
        {
            _colliders = GetComponentsInChildren<Collider>(true);
        }

        /// <summary>
        /// 14 Pole dots, 10 Banner dots, and 5 Base dots for the authored Fog flag.
        /// WorldBounds rejects dots outside the simulated domain (e.g. under ground).
        /// </summary>
        public int Collect(Vector3[] positions, Bounds worldBounds)
        {
            if (positions == null || positions.Length < MaxSamples)
                throw new System.ArgumentException("Fog flag requires a 40-point probe buffer.");
            if (_colliders == null || _colliders.Length == 0) CacheColliders();

            int count = 0;
            foreach (Collider collider in _colliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy)
                    continue;

                if (collider is CapsuleCollider capsule)
                    AddCapsule(capsule, positions, worldBounds, ref count);
                else if (collider is BoxCollider box)
                    AddBox(box, positions, worldBounds, ref count);
            }
            return count;
        }

        private void AddDot(Collider collider, Vector3 localSurface, Vector3 localOutward,
            Vector3[] positions, Bounds worldBounds, ref int count)
        {
            if (count >= MaxSamples) return;
            Transform t = collider.transform;
            Vector3 normal = t.TransformDirection(localOutward).normalized;
            Vector3 world = t.TransformPoint(localSurface) + normal * _surfaceClearance;
            if (worldBounds.Contains(world)) positions[count++] = world;
        }

        private void AddCapsule(CapsuleCollider capsule, Vector3[] positions,
            Bounds bounds, ref int count)
        {
            Vector3 axis = capsule.direction == 0 ? Vector3.right :
                capsule.direction == 2 ? Vector3.forward : Vector3.up;
            Vector3 sideA = capsule.direction == 0 ? Vector3.up : Vector3.right;
            Vector3 sideB = Vector3.Cross(axis, sideA);
            float halfCylinder = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            Vector3[] directions = { sideA, -sideA, sideB, -sideB };
            for (int ring = -1; ring <= 1; ring++)
            {
                foreach (Vector3 radial in directions)
                {
                    AddDot(capsule, capsule.center + axis * (ring * halfCylinder) +
                        radial * capsule.radius, radial, positions, bounds, ref count);
                }
            }
            AddDot(capsule, capsule.center + axis * (capsule.height * 0.5f),
                axis, positions, bounds, ref count);
            AddDot(capsule, capsule.center - axis * (capsule.height * 0.5f),
                -axis, positions, bounds, ref count);
        }

        private void AddBox(BoxCollider box, Vector3[] positions, Bounds bounds, ref int count)
        {
            Vector3 center = box.center;
            Vector3 h = box.size * 0.5f;
            if (box.gameObject.name == "Base")
            {
                // Exclude the underside: it generally faces the asphalt, not the plume.
                AddDot(box, center + Vector3.up * h.y, Vector3.up,
                    positions, bounds, ref count);
                AddDot(box, center + Vector3.right * h.x, Vector3.right,
                    positions, bounds, ref count);
                AddDot(box, center - Vector3.right * h.x, Vector3.left,
                    positions, bounds, ref count);
                AddDot(box, center + Vector3.forward * h.z, Vector3.forward,
                    positions, bounds, ref count);
                AddDot(box, center - Vector3.forward * h.z, Vector3.back,
                    positions, bounds, ref count);
                return;
            }

            // Four spaced dots on each broad face of the banner.
            for (int z = -1; z <= 1; z += 2)
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        AddDot(box, center + new Vector3(x * h.x * 0.6f,
                            y * h.y * 0.6f, z * h.z), Vector3.forward * z,
                            positions, bounds, ref count);

            AddDot(box, center + Vector3.right * h.x, Vector3.right,
                positions, bounds, ref count);
            AddDot(box, center - Vector3.right * h.x, Vector3.left,
                positions, bounds, ref count);
        }

        private void OnDrawGizmosSelected()
        {
            if (!_drawGizmos) return;
            Vector3[] dots = new Vector3[MaxSamples];
            int count = Collect(dots, new Bounds(transform.position, Vector3.one * 100000f));
            Gizmos.color = new Color(1f, 0.62f, 0.18f, 0.95f);
            for (int i = 0; i < count; i++)
                Gizmos.DrawSphere(dots[i], 0.075f);
        }
    }
}
