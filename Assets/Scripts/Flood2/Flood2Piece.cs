using UnityEngine;

namespace Flood2
{
    public enum Flood2Material { Panel, Sandbag }

    /// <summary>One dynamic building material and its single outgoing adhesive joint.</summary>
    [RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
    public sealed class Flood2Piece : MonoBehaviour
    {
        private const float WATER_DENSITY = 100f;
        private const float GRAVITY = 9.81f;
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
        public bool IsDry { get; private set; }
        public int PatchId { get; private set; } = -1;
        public int ConnectedId => _connectedPiece != null ? _connectedPiece.Id : -1;
        public Vector3 WorldAnchor => transform.TransformPoint(_anchor);
        public bool Broken => EverBonded && !IsBonded;

        /// <summary>Configures the material's real collider, mass, friction and connection indicator.</summary>
        public void Initialize(int id, Flood2Material kind, PhysicsMaterial friction, Material lineMaterial, float relativeDensity)
        {
            Id = id;
            Kind = kind;
            _body = GetComponent<Rigidbody>();
            _body.useGravity = false;
            _shape = GetComponent<BoxCollider>();
            _shape.sharedMaterial = friction;
            Vector3 size = transform.localScale;
            _body.mass = size.x * size.y * size.z * WATER_DENSITY * relativeDensity;
            _body.linearDamping = 0.4f;
            _body.angularDamping = 1.5f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.solverIterations = 12;
            _body.solverVelocityIterations = 4;
            _connection = new GameObject("Connection").AddComponent<LineRenderer>();
            _connection.transform.SetParent(transform, false);
            _connection.sharedMaterial = lineMaterial;
            _connection.startWidth = _connection.endWidth = 0.075f;
            _connection.positionCount = 2;
            _connection.enabled = false;
        }

        /// <summary>Creates one finite-strength joint at the painted contact; broken pieces cannot attach again.</summary>
        public bool Bond(Flood2Piece target, Vector3 point, int patchId, bool dry)
        {
            if (EverBonded || target == this) return false;
            EverBonded = true;
            PatchId = patchId;
            _connectedPiece = target;
            _anchor = transform.InverseTransformPoint(point);
            _joint = gameObject.AddComponent<FixedJoint>();
            _joint.connectedBody = target != null ? target.Body : null;
            _joint.autoConfigureConnectedAnchor = false;
            _joint.anchor = _anchor;
            _joint.connectedAnchor = target != null ? target.transform.InverseTransformPoint(point) : point;
            _joint.breakForce = 4500f;
            _joint.breakTorque = 2600f;
            _joint.enableCollision = true;
            _wasConnected = true;
            if (dry) Cure();
            return true;
        }

        /// <summary>Doubles existing joint limits once; drying never creates a new attachment.</summary>
        public void Cure()
        {
            if (IsDry) return;
            IsDry = true;
            if (_joint != null) { _joint.breakForce *= 2f; _joint.breakTorque *= 2f; }
        }

        /// <summary>Restores consumed adhesion history when a snapshot contained a broken joint.</summary>
        public void RestoreBroken(int patchId, bool dry)
        {
            EverBonded = true;
            PatchId = patchId;
            IsDry = dry;
        }

        /// <summary>Breaks an incoming connection when its support is removed, retaining the used-bond history.</summary>
        public void RemoveSupport(Flood2Piece support)
        {
            if (_connectedPiece != support) return;
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
                float pressure = WATER_DENSITY * GRAVITY * faceWidth * depth * (surface - bounds.min.y - depth * 0.5f);
                Vector3 application = transform.position + normal * extent;
                application.y = bounds.min.y + depth * 0.4f;
                _body.AddForceAtPosition(-normal * Mathf.Max(0f, pressure), application);
            }
            if (wet == 0) return;
            float fraction = Mathf.Clamp01((surfaceSum / wet - bounds.min.y) / Mathf.Max(0.1f, bounds.size.y));
            float volume = size.x * size.y * size.z;
            _body.AddForce(Vector3.up * (WATER_DENSITY * GRAVITY * volume * fraction - _body.linearVelocity.y * _body.mass * fraction * 2f));
            Vector2 flowAverage = flowSum / wet;
            Vector3 relative = new Vector3(flowAverage.x, 0f, flowAverage.y) - Vector3.ProjectOnPlane(_body.linearVelocity, Vector3.up);
            _body.AddForce(relative * (0.45f * WATER_DENSITY * size.y * Mathf.Min(size.x, size.z) * fraction * relative.magnitude));
        }

        private void LateUpdate()
        {
            _connection.enabled = IsBonded;
            if (!IsBonded) return;
            Color tint = IsDry ? new Color(0.95f, 0.8f, 0.32f) : new Color(0.7f, 0.4f, 0.18f);
            _connection.startColor = _connection.endColor = tint;
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
