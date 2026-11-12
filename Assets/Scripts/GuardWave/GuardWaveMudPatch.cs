using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// A painted, surface-attached mud decal with a trigger footprint.
    /// A falling piece touching the patch gets a breakable FixedJoint to the
    /// hit surface Rigidbody, or to world space when painted on terrain.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveMudPatch : MonoBehaviour
    {
        private const float PatchWidth = 1.5f;
        private const float PatchLength = 1.2f;
        private readonly Dictionary<GuardWavePiece, FixedJoint> _bonds =
            new Dictionary<GuardWavePiece, FixedJoint>();

        private GuardWaveGame _game;
        private Rigidbody _surfaceBody;
        private float _breakForce;
        private float _breakTorque;
        private Material _material;

        public readonly struct MudBondState
        {
            public readonly GuardWaveMudPatch Patch;
            public readonly GuardWavePiece Piece;
            public readonly Vector3 Anchor;
            public readonly Vector3 ConnectedAnchor;

            public MudBondState(GuardWaveMudPatch patch, GuardWavePiece piece, FixedJoint joint)
            {
                Patch = patch;
                Piece = piece;
                Anchor = joint.anchor;
                ConnectedAnchor = joint.connectedAnchor;
            }
        }

        public void Initialize(GuardWaveGame game, Rigidbody surfaceBody,
            float breakForce, float breakTorque)
        {
            _game = game;
            _surfaceBody = surfaceBody;
            _breakForce = breakForce;
            _breakTorque = breakTorque;

            GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            surface.name = "Mud Surface";
            surface.transform.SetParent(transform, false);
            surface.transform.localPosition = Vector3.zero;
            surface.transform.localRotation = Quaternion.identity;
            surface.transform.localScale = new Vector3(PatchWidth, 0.012f, PatchLength);
            Collider oldCollider = surface.GetComponent<Collider>();
            oldCollider.enabled = false;
            Destroy(oldCollider);
            _material = CreateMudMaterial(false);
            surface.GetComponent<Renderer>().sharedMaterial = _material;

            BoxCollider trigger = gameObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(PatchWidth, 0.9f, PatchLength);
            trigger.center = new Vector3(0f, 0.18f, 0f);
        }

        public static GameObject CreatePreview()
        {
            GameObject preview = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            preview.name = "Mud Brush Preview";
            preview.transform.localScale = new Vector3(PatchWidth, 0.014f, PatchLength);
            Collider collider = preview.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            preview.GetComponent<Renderer>().sharedMaterial = CreateMudMaterial(true);
            return preview;
        }

        private static Material CreateMudMaterial(bool preview)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader);
            material.name = preview ? "Mud Preview (Runtime)" : "Mud (Runtime)";
            Color tint = preview ? new Color(0.59f, 0.40f, 0.24f, 1f)
                : new Color(0.32f, 0.19f, 0.10f, 1f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.2f);
            return material;
        }

        private void OnTriggerEnter(Collider other) => TryBond(other);

        // Also catches a piece that is already touching the ground when mud is painted.
        private void OnTriggerStay(Collider other) => TryBond(other);

        private void TryBond(Collider other)
        {
            if (_game == null) return;
            GuardWavePiece piece = other.GetComponentInParent<GuardWavePiece>();
            if (piece == null || !_game.IsPlacedPiece(piece) ||
                piece.Body == _surfaceBody || piece.Body.isKinematic ||
                _game.HasMudBond(piece))
                return;

            // A vertical downward projection should bond from above, not underneath.
            if (Vector3.Dot(piece.Body.worldCenterOfMass - transform.position,
                    transform.up) < -0.12f || piece.Body.linearVelocity.y > 0.2f)
                return;

            Vector3 worldAnchor = transform.position;
            Vector3 anchor = piece.transform.InverseTransformPoint(worldAnchor);
            Vector3 connectedAnchor = _surfaceBody == null ? worldAnchor
                : _surfaceBody.transform.InverseTransformPoint(worldAnchor);
            CreateBond(piece, anchor, connectedAnchor);
        }

        private void CreateBond(GuardWavePiece piece, Vector3 anchor,
            Vector3 connectedAnchor)
        {
            if (piece == null || _bonds.ContainsKey(piece)) return;
            FixedJoint joint = piece.gameObject.AddComponent<FixedJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedBody = _surfaceBody;
            joint.anchor = anchor;
            joint.connectedAnchor = connectedAnchor;
            joint.breakForce = _breakForce;
            joint.breakTorque = _breakTorque;
            joint.enableCollision = false;
            _bonds.Add(piece, joint);
        }

        public bool HasBond(GuardWavePiece piece) =>
            piece != null && _bonds.ContainsKey(piece);

        public void DetachPiece(GuardWavePiece piece)
        {
            if (piece == null) return;
            if (_surfaceBody == piece.Body)
            {
                ClearBonds();
                return;
            }
            if (!_bonds.TryGetValue(piece, out FixedJoint joint)) return;
            if (joint != null) Destroy(joint);
            _bonds.Remove(piece);
        }

        public void CaptureBonds(List<MudBondState> destination)
        {
            foreach (KeyValuePair<GuardWavePiece, FixedJoint> bond in _bonds)
                if (bond.Key != null && bond.Value != null)
                    destination.Add(new MudBondState(this, bond.Key, bond.Value));
        }

        public void ClearBonds()
        {
            foreach (FixedJoint joint in _bonds.Values)
                if (joint != null) Destroy(joint);
            _bonds.Clear();
        }

        public void RestoreBond(MudBondState state)
        {
            if (state.Patch != this || state.Piece == null ||
                !_game.IsPlacedPiece(state.Piece)) return;
            CreateBond(state.Piece, state.Anchor, state.ConnectedAnchor);
        }

        private void OnDestroy()
        {
            ClearBonds();
            if (_material != null) Destroy(_material);
        }
    }
}
