using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Inspector-authored FixedJoint physics shared by clicked mud bonds and
    /// Flood's 13 pre-placed log bonds. Anchor positions are generated per contact.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveMudJointSettings : MonoBehaviour
    {
        private const string ResourcePath = "Prefabs/Objects/mud";

        [Header("Mud FixedJoint - breaking")]
        [SerializeField, Min(0f)] private float breakForce = 300f;
        [Tooltip("Enter Infinity for no torque breakage, or a nonnegative numeric limit.")]
        [SerializeField, Min(0f)] private float breakTorque = Mathf.Infinity;

        [Header("Mud FixedJoint - stability")]
        [Tooltip("Collision between the joined bodies is always disabled.")]
        [SerializeField] private bool enablePreprocessing = true;
        [SerializeField, Min(0.01f)] private float massScale = 1f;
        [SerializeField, Min(0.01f)] private float connectedMassScale = 1f;

        [Header("Connected Rigidbody solver")]
        [SerializeField, Min(1)] private int solverIterations = 16;
        [SerializeField, Min(1)] private int solverVelocityIterations = 8;

        public float BreakForce => breakForce;
        public float BreakTorque => breakTorque;
        public int SolverIterations => Mathf.Max(1, solverIterations);
        public int SolverVelocityIterations => Mathf.Max(1, solverVelocityIterations);

        public static GuardWaveMudJointSettings LoadPrefabSettings()
        {
            GameObject prefab = Resources.Load<GameObject>(ResourcePath);
            return prefab == null ? null : prefab.GetComponent<GuardWaveMudJointSettings>();
        }

        public void ApplyTo(FixedJoint joint)
        {
            if (joint == null) return;
            joint.breakForce = BreakForce;
            joint.breakTorque = BreakTorque;
            joint.enableCollision = false;
            joint.enablePreprocessing = enablePreprocessing;
            joint.massScale = Mathf.Max(0.01f, massScale);
            joint.connectedMassScale = Mathf.Max(0.01f, connectedMassScale);
            // FixedJoint constrains all relative motion. GuardWaveMudPatch
            // computes the correct local anchors separately for each contact.
        }
    }
}
