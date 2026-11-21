using System;

using UnityEngine;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>Runs the sloped channel and shares one durability pool across its outlet logs.</summary>
    public sealed class DefenseSceneController : MonoBehaviour
    {
        private const int MAX_DURABILITY = 100;

        [Header("Channel")]
        [SerializeField] private Transform _inlet;
        [SerializeField] private Transform _outlet;
        [SerializeField] private GuardWaveFluidSettings _fluidSettings;
        [SerializeField] private Transform _fence;

        [Header("Durability HUD")]
        [SerializeField] private RectTransform _fill;
        [SerializeField] private Text _label;

        private GuardWaveFlipFluid _fluid;
        private GuardWaveFlipRenderer _renderer;
        private Rigidbody[] _logs;
        private int _durability = MAX_DURABILITY;

        private void Start()
        {
            Vector3 inlet = _inlet.position;
            Vector3 outlet = _outlet.position;
            float slope = (inlet.y - outlet.y) / (inlet.z - outlet.z);
            _logs = _fence.GetComponentsInChildren<Rigidbody>();
            _fluid = new GuardWaveFlipFluid(inlet.z, outlet.z, outlet.y, outlet.y - 8f,
                _fluidSettings.AmbientWaterDepth, 0f, _fence.GetComponentsInChildren<GuardWavePiece>(),
                null, _fluidSettings, true, slope, _fence);
            _renderer = new GuardWaveFlipRenderer(_fluidSettings);
            _renderer.Render(_fluid);
        }

        private void FixedUpdate()
        {
            _fluid.ApplyPendingImpulses();
            _fluid.Step(Time.fixedDeltaTime, 1f);
            int remaining = MAX_DURABILITY - (int)Math.Min((uint)MAX_DURABILITY, _fluid.FenceParticleHits);
            if (remaining == _durability) return;
            _durability = remaining;
            _fill.anchorMax = new Vector2((float)remaining / MAX_DURABILITY, 1f);
            _label.text = $"{remaining}/{MAX_DURABILITY}";
            if (remaining == 0) CollapseFence();
        }

        /// <summary>Releases the rigid fence into individual logs that tumble downstream before despawning.</summary>
        private void CollapseFence()
        {
            foreach (Rigidbody log in _logs)
            {
                log.isKinematic = false;
                log.AddForceAtPosition(Vector3.back * 3f, log.worldCenterOfMass + Vector3.up * 0.6f,
                    ForceMode.VelocityChange);
                Destroy(log.gameObject, 3f);
            }
        }

        private void OnDestroy()
        {
            _renderer?.Dispose();
            _fluid?.Dispose();
        }
    }
}
