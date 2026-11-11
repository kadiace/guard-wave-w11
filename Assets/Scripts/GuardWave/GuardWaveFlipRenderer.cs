using System;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Registers the active GPU particles for URP screen-space fluid reconstruction.
    /// Water is no longer drawn as independent billboard discs in MonoBehaviour.Update.
    /// </summary>
    internal sealed class GuardWaveFlipRenderer : IDisposable
    {
        private readonly GuardWaveFluidSettings _settings;

        internal static GuardWaveFlipFluid ActiveFluid { get; private set; }
        internal static GuardWaveFluidSettings ActiveSettings { get; private set; }

        /// <summary>Retains the shared fluid appearance and quality settings.</summary>
        public GuardWaveFlipRenderer(GuardWaveFluidSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Supplies the GPU particle buffer to the next URP RenderGraph frame.</summary>
        public void Render(GuardWaveFlipFluid fluid)
        {
            ActiveFluid = fluid;
            ActiveSettings = fluid == null ? null : _settings;
        }

        /// <summary>Disables the registered fluid after game shutdown or scene teardown.</summary>
        public void Dispose()
        {
            ActiveFluid = null;
            ActiveSettings = null;
        }
    }
}
