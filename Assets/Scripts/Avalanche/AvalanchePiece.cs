using System;

using UnityEngine;

using GuardWave;

namespace Avalanche
{
    public enum AvalancheMaterial { Panel, Sandbag }

    /// <summary>One dynamic building material and its single outgoing adhesive joint.</summary>
    [RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
    public sealed class AvalanchePiece : MonoBehaviour
    {
        private const float GRAVITY = 9.81f;
        private static readonly Color BOND_COLOR = new Color(0.95f, 0.8f, 0.32f);
        private PhysicsSettings _settings;
        private Rigidbody _body;
        private BoxCollider _shape;
        private FixedJoint _joint;
        private LineRenderer _connection;
        private AvalanchePiece _connectedPiece;
        private Vector3 _anchor;
        private bool _wasConnected;
        private readonly Vector3[] _snowForces = new Vector3[4];
        private readonly Vector3[] _snowPoints = new Vector3[4];
        public int Id { get; private set; }
        public AvalancheMaterial Kind { get; private set; }
        public Rigidbody Body => _body;
        public BoxCollider Shape => _shape;
        public bool EverBonded { get; private set; }
        public bool IsBonded => _joint != null;
        public int AdhesiveId { get; private set; } = -1;
        public int ConnectedId => _connectedPiece != null ? _connectedPiece.Id : -1;
        public Vector3 WorldAnchor => transform.TransformPoint(_anchor);
        public bool Broken => EverBonded && !IsBonded;

        [Serializable]
        internal sealed class PhysicsSettings
        {
            [Header("Mass and damping")]
            [SerializeField, Min(0.01f)] private float _materialDensity = 100f;
            [SerializeField, Min(0.01f)] private float _panelRelativeDensity = 0.6f;
            [SerializeField, Min(1f)] private float _sandbagRelativeDensity = 2f;
            [SerializeField, Min(0f)] private float _linearDamping = 0.4f;
            [SerializeField, Min(0f)] private float _angularDamping = 1.5f;

            [Header("Joint break limits")]
            [SerializeField, Min(0f)] private float _breakForce = 9000f;
            [SerializeField, Min(0f)] private float _breakTorque = 5200f;
            public float MaterialDensity => _materialDensity;
            public float PanelRelativeDensity => _panelRelativeDensity;
            public float SandbagRelativeDensity => _sandbagRelativeDensity;
            public float LinearDamping => _linearDamping;
            public float AngularDamping => _angularDamping;
            public float BreakForce => _breakForce;
            public float BreakTorque => _breakTorque;
        }

        /// <summary>Configures the material's real collider, mass, friction and connection indicator.</summary>
        internal void Initialize(int id, AvalancheMaterial kind, PhysicsMaterial friction, Material lineMaterial, PhysicsSettings settings)
        {
            _settings = settings;
            Id = id;
            Kind = kind;
            _body = GetComponent<Rigidbody>();
            _body.useGravity = false;
            _shape = GetComponent<BoxCollider>();
            _shape.sharedMaterial = friction;
            Vector3 size = transform.localScale;
            _body.mass = size.x * size.y * size.z * settings.MaterialDensity * (kind == AvalancheMaterial.Panel ? settings.PanelRelativeDensity : settings.SandbagRelativeDensity);
            _body.linearDamping = settings.LinearDamping;
            _body.angularDamping = settings.AngularDamping;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.solverIterations = 12;
            _body.solverVelocityIterations = 4;
            _connection = new GameObject("Connection").AddComponent<LineRenderer>();
            _connection.transform.SetParent(transform, false);
            _connection.sharedMaterial = lineMaterial;
            _connection.startWidth = _connection.endWidth = 0.075f;
            _connection.startColor = _connection.endColor = BOND_COLOR;
            _connection.positionCount = 2;
            _connection.enabled = false;
        }

        /// <summary>Creates one full-strength joint at the adhesive contact. Broken pieces cannot attach again.</summary>
        public bool Bond(AvalanchePiece target, Vector3 point, int adhesiveId)
        {
            if (EverBonded || target == this) return false;
            EverBonded = true;
            AdhesiveId = adhesiveId;
            _connectedPiece = target;
            _anchor = transform.InverseTransformPoint(point);
            _joint = StageJoint.Create(gameObject, target != null ? target.Body : null, _anchor,
                target != null ? target.transform.InverseTransformPoint(point) : point,
                _settings.BreakForce, _settings.BreakTorque, true);
            _wasConnected = true;
            return true;
        }

        /// <summary>Releases a joint when its block is retrieved so the piece can attach again.</summary>
        internal void RemoveAdhesive(int adhesiveId)
        {
            if (AdhesiveId != adhesiveId || !IsBonded) return;
            DetachJoint();
            AdhesiveId = -1;
            EverBonded = false;
        }

        /// <summary>Restores consumed adhesion history when a snapshot contained a broken joint.</summary>
        public void RestoreBroken(int adhesiveId)
        {
            EverBonded = true;
            AdhesiveId = adhesiveId;
        }

        /// <summary>Breaks an incoming connection when its support is removed, retaining the used-bond history.</summary>
        public void RemoveSupport(AvalanchePiece support)
        {
            if (_connectedPiece != support) return;
            DetachJoint();
        }

        /// <summary>Destroys the current joint and clears its live target and indicator state.</summary>
        private void DetachJoint()
        {
            if (_joint != null) Destroy(_joint);
            _joint = null;
            _connectedPiece = null;
            _wasConnected = false;
        }

        private void FixedUpdate()
        {
            if (!_body.IsSleeping()) _body.AddForce(Vector3.down * GRAVITY, ForceMode.Acceleration);
            if (transform.position.y < -15f || transform.position.z < -4f || Mathf.Abs(transform.position.x) > 26f)
                gameObject.SetActive(false);
        }

        /// <summary>Applies capped snow impact and stationary pressure at the actual contact height of each box face.</summary>
        internal void ApplySnow(AvalancheSnow snow, AvalancheSnow.Settings settings)
        {
            if (!gameObject.activeSelf || _body.isKinematic) return;
            Vector3 size = transform.localScale;
            Bounds bounds = _shape.bounds;
            Vector3[] forces = _snowForces;
            Vector3[] points = _snowPoints;
            Array.Clear(forces, 0, forces.Length);
            float sum = 0f;
            for (int side = 0; side < 4; side++)
            {
                Vector3 normal = side < 2 ? transform.right : transform.forward;
                if (side % 2 == 1) normal = -normal;
                normal.y = 0f;
                normal.Normalize();
                float extent = (side < 2 ? size.x : size.z) * 0.5f;
                Vector3 probe = transform.position + normal * (extent + 0.35f);
                if (!snow.Sample(probe, out float moving, out float deposited, out Vector2 velocity)) continue;
                float surface = AvalancheTerrain.Height(probe.x, probe.z) + moving + deposited;
                float height = Mathf.Clamp(surface - bounds.min.y, 0f, bounds.size.y);
                if (height <= 0f) continue;
                Vector3 relative = new Vector3(velocity.x, 0f, velocity.y) - _body.linearVelocity;
                float approach = Mathf.Max(0f, Vector3.Dot(relative, -normal));
                float area = height * (side < 2 ? size.z : size.x);
                float load = area * settings.Density * (approach * approach + 0.5f * settings.PressureCoefficient * GRAVITY * height);
                forces[side] = -normal * load;
                points[side] = transform.position + normal * extent;
                points[side].y = bounds.min.y + height * 0.5f;
                sum += load;
            }
            float scale = sum > settings.MaximumLoad ? settings.MaximumLoad / sum : 1f;
            for (int side = 0; side < 4; side++)
                if (forces[side].sqrMagnitude > 0f) _body.AddForceAtPosition(forces[side] * scale, points[side]);
        }

        private void LateUpdate()
        {
            _connection.enabled = IsBonded;
            if (!IsBonded) return;
            _connection.SetPosition(0, transform.position + Vector3.up * 0.15f);
            _connection.SetPosition(1, _connectedPiece != null ? _connectedPiece.transform.position : _joint.connectedAnchor);
        }

        private void OnJointBreak(float force)
        {
            if (!_wasConnected) return;
            _wasConnected = false;
            _joint = null;
        }
    }
}
