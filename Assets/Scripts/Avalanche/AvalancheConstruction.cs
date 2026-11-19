using System.Collections.Generic;
using UnityEngine;

namespace Avalanche
{
    /// <summary>
    /// Tracks scene-authored obstacles and their pre-flood physics state.
    /// Player construction, adhesion tools, inventories and Undo are removed.
    /// </summary>
    public sealed class AvalancheConstruction : MonoBehaviour
    {
        private readonly List<AvalanchePiece> _pieces = new List<AvalanchePiece>();
        private readonly List<BodyState> _original = new List<BodyState>();
        private readonly List<BodyState> _checkpoint = new List<BodyState>();

        public IReadOnlyList<AvalanchePiece> Pieces => _pieces;

        public void Initialize(AvalancheStage stage)
        {
            _pieces.Clear();
            _original.Clear();
            int id = 0;
            foreach (AvalanchePiece piece in FindObjectsByType<AvalanchePiece>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (piece == null || piece.gameObject.scene.handle != gameObject.scene.handle)
                    continue;
                // Register only authored scene objects; nothing is ever spawned.
                piece.Initialize(id++, piece.Kind, stage.Friction, stage.LineMaterial,
                    new AvalanchePiece.PhysicsSettings());
                _pieces.Add(piece);
                _original.Add(new BodyState(piece));
            }
            SaveCheckpoint();
        }

        public void TickContacts(float dt)
        {
            // No runtime adhesive blocks or contact-driven placement.
        }

        public void SaveCheckpoint()
        {
            _checkpoint.Clear();
            foreach (AvalanchePiece piece in _pieces)
                if (piece != null) _checkpoint.Add(new BodyState(piece));
        }

        public void ResetLayout(bool empty)
        {
            // Retrying never deletes or places structures.
            List<BodyState> baseline = empty ? _original : _checkpoint;
            foreach (BodyState state in baseline)
                state.RestorePose();
            Physics.SyncTransforms();
            foreach (BodyState state in baseline)
                state.RestoreMotion();
        }

        private readonly struct BodyState
        {
            private readonly AvalanchePiece _piece;
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;
            private readonly Vector3 _velocity;
            private readonly Vector3 _angularVelocity;
            private readonly bool _active;
            private readonly bool _kinematic;
            private readonly bool _useGravity;

            public BodyState(AvalanchePiece piece)
            {
                _piece = piece;
                Rigidbody body = piece.Body;
                _position = body.position;
                _rotation = body.rotation;
                _velocity = body.linearVelocity;
                _angularVelocity = body.angularVelocity;
                _active = piece.gameObject.activeSelf;
                _kinematic = body.isKinematic;
                _useGravity = body.useGravity;
            }

            public void RestorePose()
            {
                if (_piece == null) return;
                _piece.gameObject.SetActive(true);
                Rigidbody body = _piece.Body;
                body.isKinematic = true;
                body.useGravity = false;
                body.transform.SetPositionAndRotation(_position, _rotation);
                body.position = _position;
                body.rotation = _rotation;
            }

            public void RestoreMotion()
            {
                if (_piece == null) return;
                Rigidbody body = _piece.Body;
                body.useGravity = _useGravity;
                body.isKinematic = _kinematic;
                if (!_kinematic)
                {
                    body.linearVelocity = _velocity;
                    body.angularVelocity = _angularVelocity;
                    body.WakeUp();
                }
                _piece.gameObject.SetActive(_active);
            }
        }
    }
}
