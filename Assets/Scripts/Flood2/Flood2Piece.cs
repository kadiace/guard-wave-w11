using System;

using UnityEngine;

namespace Flood2
{
    public enum Flood2Material { Panel, Sandbag }

    /// <summary>One dynamic building material and its single outgoing adhesive joint.</summary>
    [RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
    public sealed class Flood2Piece : MonoBehaviour
    {
        private const float GRAVITY = 9.81f;
        private static readonly Color BOND_COLOR = new Color(0.95f, 0.8f, 0.32f);
        private PhysicsSettings _settings;
        private Rigidbody _body;
        private BoxCollider _shape;
        private FixedJoint _joint;
        private LineRenderer _connection;
        private Flood2Piece _connectedPiece;
        private Vector3 _anchor;
        private bool _wasConnected;
        public int Id { get; private set; }
        public Flood2Material Kind { get; private set; }
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
            [Header("Mass and water forces")]
            [SerializeField, Min(0.01f)] private float _waterDensity = 100f;
            [SerializeField, Min(0.01f)] private float _panelRelativeDensity = 0.6f;
            [SerializeField, Min(1f)] private float _sandbagRelativeDensity = 2f;
            [SerializeField, Min(0f)] private float _flowDrag = 0.45f;
            [SerializeField, Min(0f)] private float _linearDamping = 0.4f;
            [SerializeField, Min(0f)] private float _angularDamping = 1.5f;

            [Header("Joint break limits")]
            [SerializeField, Min(0f)] private float _breakForce = 9000f;
            [SerializeField, Min(0f)] private float _breakTorque = 5200f;
            public float WaterDensity => _waterDensity;
            public float PanelRelativeDensity => _panelRelativeDensity;
            public float SandbagRelativeDensity => _sandbagRelativeDensity;
            public float FlowDrag => _flowDrag;
            public float LinearDamping => _linearDamping;
            public float AngularDamping => _angularDamping;
            public float BreakForce => _breakForce;
            public float BreakTorque => _breakTorque;
        }

        /// <summary>Configures the material's real collider, mass, friction and connection indicator.</summary>
        internal void Initialize(int id, Flood2Material kind, PhysicsMaterial friction, Material lineMaterial, PhysicsSettings settings)
        {
            _settings = settings;
            Id = id;
            Kind = kind;
            _body = GetComponent<Rigidbody>();
            _body.useGravity = false;
            _shape = GetComponent<BoxCollider>();
            _shape.sharedMaterial = friction;
            Vector3 size = transform.localScale;
            _body.mass = size.x * size.y * size.z * settings.WaterDensity * (kind == Flood2Material.Panel ? settings.PanelRelativeDensity : settings.SandbagRelativeDensity);
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
        public bool Bond(Flood2Piece target, Vector3 point, int adhesiveId)
        {
            if (EverBonded || target == this) return false;
            EverBonded = true;
            AdhesiveId = adhesiveId;
            _connectedPiece = target;
            _anchor = transform.InverseTransformPoint(point);
            _joint = gameObject.AddComponent<FixedJoint>();
            _joint.connectedBody = target != null ? target.Body : null;
            _joint.autoConfigureConnectedAnchor = false;
            _joint.anchor = _anchor;
            _joint.connectedAnchor = target != null ? target.transform.InverseTransformPoint(point) : point;
            _joint.breakForce = _settings.BreakForce;
            _joint.breakTorque = _settings.BreakTorque;
            _joint.enableCollision = true;
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
        public void RemoveSupport(Flood2Piece support)
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

        /// <summary>Applies buoyancy, relative-flow drag and a pressure difference across each physical box face.</summary>
        public void ApplyWater(Flood2Water water)
        {
            if (!gameObject.activeSelf || _body.isKinematic) return;
            Vector3 size = transform.localScale;
            Bounds bounds = _shape.bounds;
            float surfaceSum = 0f;
            Vector2 flowSum = Vector2.zero;
            int wet = 0;
            for (int side = 0; side < 4; side++)
            {
                Vector3 normal = side < 2 ? transform.right : transform.forward;
                if (side % 2 == 1) normal = -normal;
                normal.y = 0f;
                normal.Normalize();
                float extent = side < 2 ? size.x * 0.5f : size.z * 0.5f;
                Vector3 probe = transform.position + normal * (extent + 0.65f);
                probe.y = Mathf.Max(bounds.min.y, Flood2Terrain.Height(probe.x, probe.z)) + 0.09f;
                if (!water.Sample(probe, out float surface, out Vector2 flow) || surface <= bounds.min.y) continue;
                wet++;
                surfaceSum += surface;
                flowSum += flow;
                float depth = Mathf.Clamp(surface - bounds.min.y, 0f, bounds.size.y);
                float faceWidth = side < 2 ? size.z : size.x;
                float pressure = _settings.WaterDensity * GRAVITY * faceWidth * depth * (surface - bounds.min.y - depth * 0.5f);
                Vector3 application = transform.position + normal * extent;
                application.y = bounds.min.y + depth * 0.4f;
                _body.AddForceAtPosition(-normal * Mathf.Max(0f, pressure), application);
            }
            if (wet == 0) return;
            float fraction = Mathf.Clamp01((surfaceSum / wet - bounds.min.y) / Mathf.Max(0.1f, bounds.size.y));
            float volume = size.x * size.y * size.z;
            _body.AddForce(Vector3.up * (_settings.WaterDensity * GRAVITY * volume * fraction - _body.linearVelocity.y * _body.mass * fraction * 2f));
            Vector2 flowAverage = flowSum / wet;
            Vector3 relative = new Vector3(flowAverage.x, 0f, flowAverage.y) - Vector3.ProjectOnPlane(_body.linearVelocity, Vector3.up);
            _body.AddForce(relative * (_settings.FlowDrag * _settings.WaterDensity * size.y * Mathf.Min(size.x, size.z) * fraction * relative.magnitude));
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
