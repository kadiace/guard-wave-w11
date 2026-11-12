using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// A stationary high-rise with a continuously operating outdoor AC unit.
    /// The gas solver samples these outlets in world space; no fake particle impulse
    /// or rigidbody force is used to move the 3D fog.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FogBuildingWind : MonoBehaviour
    {
        [Header("Material catalog")]
        [SerializeField] private string materialId = "building";

        [Header("Outdoor AC: local +Z wall")]
        [SerializeField] private Vector3 outletLocalPosition = new Vector3(0f, 4f, 7.15f);
        [SerializeField] private Vector3 outletLocalDirection = Vector3.forward;
        [SerializeField, Min(0f)] private float windAcceleration = 28f;
        [SerializeField, Min(0.1f)] private float reach = 14f;
        [SerializeField, Min(0.1f)] private float outletRadius = 16f;

        public string MaterialId => materialId;
        public Vector3 OutletPosition => transform.TransformPoint(outletLocalPosition);
        public Vector3 OutletDirection => transform.TransformDirection(outletLocalDirection).normalized;
        public float WindAcceleration => Mathf.Max(0f, windAcceleration);
        public float Reach => Mathf.Max(0.1f, reach);
        public float OutletRadius => Mathf.Max(0.1f, outletRadius);

        private void OnDrawGizmosSelected()
        {
            Vector3 start = OutletPosition;
            Vector3 dir = OutletDirection;
            Gizmos.color = new Color(0.25f, 0.88f, 1f, 0.85f);
            Gizmos.DrawLine(start, start + dir * Reach);
            Gizmos.DrawWireSphere(start, OutletRadius * 0.25f);
            Gizmos.DrawWireSphere(start + dir * Reach, OutletRadius + Reach * 0.22f);
        }
    }
}
