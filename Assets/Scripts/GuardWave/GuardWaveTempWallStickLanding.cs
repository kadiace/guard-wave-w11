using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Forest/Temp Wall only: pre-authored ConfigurableJoint starts unconstrained.
    /// The stick falls under Rigidbody gravity, then locks its bottom anchor to
    /// the world when its lower end actually contacts the Creek bed.
    /// Other Stick prefab instances are not modified at runtime.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(ConfigurableJoint))]
    [DisallowMultipleComponent]
    public sealed class GuardWaveTempWallStickLanding : MonoBehaviour
    {
        private const string TEMP_WALL_NAME = "Temp Wall";
        private const string CREEK_BED_NAME = "Flat Muddy Creek Bed";
        private const float MAX_FOOT_CONTACT_DISTANCE = 0.55f;
        private const float MIN_FLOOR_NORMAL_Y = 0.6f;

        private ConfigurableJoint _joint;
        private bool _landed;

        private void Awake()
        {
            // The Stick prefab can be used elsewhere: touch only the authored
            // 13 children under Forest's Temp Wall.
            if (transform.parent == null || transform.parent.name != TEMP_WALL_NAME)
            {
                enabled = false;
                return;
            }

            _joint = GetComponent<ConfigurableJoint>();
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (_joint == null || mesh == null)
            {
                enabled = false;
                return;
            }

            // The prefab's built-in Cylinder mesh is 2 units tall in local space
            // (min.y = -1). A BoxCollider with size.y = 1 incorrectly put the
            // former anchor at -0.5, halfway up the actual Stick. Read the visual
            // mesh bounds instead; TransformPoint applies the instance's scale.
            Bounds meshBounds = mesh.bounds;
            _joint.anchor = new Vector3(
                meshBounds.center.x, meshBounds.min.y, meshBounds.center.z);
            _joint.connectedBody = null;
            _joint.autoConfigureConnectedAnchor = false;
            _joint.connectedAnchor = transform.TransformPoint(_joint.anchor);
            SetMotion(ConfigurableJointMotion.Free);
        }

        private void OnCollisionEnter(Collision collision)
        {
            TryAttachToCreek(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            TryAttachToCreek(collision);
        }

        private void TryAttachToCreek(Collision collision)
        {
            if (_landed || _joint == null || collision.collider == null ||
                collision.collider.gameObject.name != CREEK_BED_NAME)
                return;

            Vector3 foot = transform.TransformPoint(_joint.anchor);
            for (int i = 0; i < collision.contactCount; i++)
            {
                ContactPoint contact = collision.GetContact(i);
                // Only pin on actual ground contact near the bottom of this
                // stick. A side-on collision must not anchor it in mid-air.
                if (Mathf.Abs(contact.normal.y) < MIN_FLOOR_NORMAL_Y ||
                    (foot - contact.point).sqrMagnitude >
                    MAX_FOOT_CONTACT_DISTANCE * MAX_FOOT_CONTACT_DISTANCE)
                    continue;

                // connectedBody=null means a world-space connectedAnchor.
                // Match the real impact point before activating constraints so
                // we don't pull the stick back to the old editor-space anchor.
                _joint.connectedAnchor = contact.point;
                SetMotion(ConfigurableJointMotion.Locked);
                _landed = true;
                return;
            }
        }

        private void SetMotion(ConfigurableJointMotion motion)
        {
            _joint.xMotion = motion;
            _joint.yMotion = motion;
            _joint.zMotion = motion;
            _joint.angularXMotion = motion;
            _joint.angularYMotion = motion;
            _joint.angularZMotion = motion;
        }
    }
}
