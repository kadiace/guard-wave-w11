using UnityEngine;

namespace GuardWave
{
    /// <summary>Editable physical behavior and GPU budgets for new Forest FLIP/APIC waves.</summary>
    [CreateAssetMenu(fileName = "GuardWaveFluidSettings", menuName = "GuardWave/Fluid Settings")]
    public sealed class GuardWaveFluidSettings : ScriptableObject
    {
        [Header("Rigidbodies - impact")]
        [SerializeField, Min(0.001f)] private float _particleEffectiveMass = 0.14f;
        [SerializeField, Min(0f)] private float _hydroDensity = 0.7f;
        [SerializeField, Min(0f)] private float _maxImpulsePerBodyMass = 3f;
        [SerializeField, Min(0f)] private float _minImpulseLimit = 0.25f;
        [SerializeField, Min(0f)] private float _angularImpulseMultiplier = 3f;
        [SerializeField, Min(0.1f)] private float _pressureClamp = 50f;
        [SerializeField, Min(0f)] private float _pressureThreshold = 0.05f;

        [Header("Ambient creek (no wave)")]
        [SerializeField, Min(0.2f)] private float _ambientWaterDepth = 1f;
        [SerializeField, Min(0.01f)] private float _ambientFlowSpeed = 10f;
        // Higher count per meter of channel gives more real liquid particles
        // and automatically scales the inlet's loss-replacement throughput.
        [SerializeField, Range(1f, 4f)] private float _ambientFlowVolumeMultiplier = 2f;
        [SerializeField, Min(1f)] private float _ambientMaxSupplyParticlesPerSecond = 3700f;
        [SerializeField, Range(0f, 1f)] private float _ambientImpactScale = 0.01f;
        public float AmbientWaterDepth => _ambientWaterDepth;
        public float AmbientFlowSpeed => _ambientFlowSpeed;
        public float AmbientFlowVolumeMultiplier => _ambientFlowVolumeMultiplier;
        public float AmbientMaxSupplyParticlesPerSecond => _ambientMaxSupplyParticlesPerSecond;
        public float AmbientImpactScale => _ambientImpactScale;

        [Header("Relative-density buoyancy and current")]
        [SerializeField, Min(0.05f)] private float _waterFlowResponse = 6f;
        [SerializeField, Range(0f, 1f)] private float _verticalFlowResponseScale = 0.35f;
        [SerializeField, Range(0f, 1f)] private float _groundedFlowMultiplier = 0.15f;
        public float WaterFlowResponse => _waterFlowResponse;
        public float VerticalFlowResponseScale => _verticalFlowResponseScale;
        public float GroundedFlowMultiplier => _groundedFlowMultiplier;

        [Header("Floating body stabilization")]
        [SerializeField, Min(0f)] private float _floatingVerticalDamping = 7f;
        [Tooltip("Critical vertical damping multiplier based on immersed-volume stiffness. Values >= 1 settle without bobbing.")]
        [SerializeField, Range(0.5f, 2f)] private float _floatingEquilibriumDampingRatio = 1.25f;
        [Tooltip("Low-pass rate for the floating body's measured free surface. Higher values follow faster water changes.")]
        [SerializeField, Min(0.5f)] private float _floatingSurfaceSmoothingRate = 6f;
        [SerializeField, Range(0f, 1f)] private float _floatingTorqueScale = 0.12f;
        [SerializeField, Min(1f)] private float _floatingMaxBuoyancyRatio = 1.5f;
        [SerializeField, Min(0f)] private float _floatingUprightStrength = 1.5f;
        [SerializeField, Min(0f)] private float _floatingAngularDamping = 5f;
        [Tooltip("Steering torque per kg for floating logs and the flag, separate from noise damping.")]
        [SerializeField, Min(0f)] private float _floatingFlowAlignmentStrength = 10f;
        [Tooltip("Flow speed (m/s) where the alignment torque reaches full strength.")]
        [SerializeField, Min(0.1f)] private float _floatingFlowAlignmentFullSpeed = 4f;
        public float FloatingVerticalDamping => _floatingVerticalDamping;
        public float FloatingEquilibriumDampingRatio => _floatingEquilibriumDampingRatio;
        public float FloatingSurfaceSmoothingRate => _floatingSurfaceSmoothingRate;
        public float FloatingTorqueScale => _floatingTorqueScale;
        public float FloatingMaxBuoyancyRatio => _floatingMaxBuoyancyRatio;
        public float FloatingUprightStrength => _floatingUprightStrength;
        public float FloatingAngularDamping => _floatingAngularDamping;
        public float FloatingFlowAlignmentStrength => _floatingFlowAlignmentStrength;
        public float FloatingFlowAlignmentFullSpeed => _floatingFlowAlignmentFullSpeed;

        [Header("One-shot wave crest above ambient water")]
        [SerializeField, Min(0.1f)] private float _waveCrestAmplitude = 3f;
        [SerializeField, Min(0.5f)] private float _waveCrestWidth = 1.4f;
        [SerializeField, Min(2f)] private float _waveCrestLength = 8f;
        public float WaveCrestAmplitude => _waveCrestAmplitude;
        // The crest travels at the current ambient flow speed without acceleration.
        public float WaveCrestSpeed => _ambientFlowSpeed;
        public float WaveCrestWidth => _waveCrestWidth;
        public float WaveCrestLength => _waveCrestLength;

        [Header("Crest formation (release on obstacle contact)")]
        // Soft vertical support prevents the old FLIP projection from
        // compressing a newly formed 3m crest into the 1m background.
        [SerializeField, Min(0f)] private float _crestHeightStiffness = 36f;
        [SerializeField, Min(0f)] private float _crestHeightDamping = 12f;
        [SerializeField, Min(0f)] private float _crestForwardDamping = 5f;
        [SerializeField, Min(0f)] private float _crestReleaseDistance = 0.8f;
        public float CrestHeightStiffness => _crestHeightStiffness;
        public float CrestHeightDamping => _crestHeightDamping;
        public float CrestForwardDamping => _crestForwardDamping;
        public float CrestReleaseDistance => _crestReleaseDistance;

        [Header("Wave movement")]
        [SerializeField, Min(0.1f)] private float _minInletSpeed = 6f;
        [SerializeField, Min(0.1f)] private float _maxInletSpeed = 13f;
        [SerializeField, Min(0.1f)] private float _travelDurationMultiplier = 2f;
        [SerializeField, Range(0f, 3f)] private float _gravityMultiplier = 1f;
        [SerializeField, Min(0.1f)] private float _maxParticleSpeed = 24f;
        [SerializeField, Range(0f, 1f)] private float _flipFraction = 0.92f;

        [Header("Collisions")]
        [SerializeField, Range(0f, 2f)] private float _collisionResponse = 1.15f;
        [SerializeField, Min(0.001f)] private float _collisionSkin = 0.06f;

        [Header("GPU performance - applied on the next wave")]
        [SerializeField, Range(0.25f, 1.5f)] private float _cellSize = 0.6f;
        [SerializeField, Range(1000, 100000)] private int _maxParticles = 64000;
        [SerializeField, Range(1, 384)] private int _maxBodies = 384;
        [SerializeField, Range(1, 256)] private int _maxColliders = 256;
        [SerializeField, Range(1, 96)] private int _pressureIterations = 22;
        [SerializeField, Min(0.02f)] private float _spawnInterval = 0.12f;
        [SerializeField, Range(0.1f, 8f)] private float _spawnRateMultiplier = 3f;

        [Header("Visual radius - applies to particle view; SSFR uses its own surface")]
        [SerializeField, Min(0.05f)] private float _particleRenderRadius = 0.45f;

        public float ParticleEffectiveMass => _particleEffectiveMass;
        public float HydroDensity => _hydroDensity;
        public float MaxImpulsePerBodyMass => _maxImpulsePerBodyMass;
        public float MinImpulseLimit => _minImpulseLimit;
        public float AngularImpulseMultiplier => _angularImpulseMultiplier;
        public float PressureClamp => _pressureClamp;
        public float PressureThreshold => _pressureThreshold;
        public float MinInletSpeed => _minInletSpeed;
        public float MaxInletSpeed => Mathf.Max(_maxInletSpeed, _minInletSpeed);
        public float TravelDurationMultiplier => _travelDurationMultiplier;
        public float GravityMultiplier => _gravityMultiplier;
        public float MaxParticleSpeed => _maxParticleSpeed;
        public float FlipFraction => _flipFraction;
        public float CollisionResponse => _collisionResponse;
        public float CollisionSkin => _collisionSkin;
        public float CellSize => _cellSize;
        public int MaxParticles => _maxParticles;
        public int MaxBodies => _maxBodies;
        public int MaxColliders => _maxColliders;
        public int PressureIterations => _pressureIterations;
        public float SpawnInterval => _spawnInterval;
        public float SpawnRateMultiplier => _spawnRateMultiplier;
        public float ParticleRenderRadius => _particleRenderRadius;

        [Header("Screen-space liquid rendering")]
        [SerializeField, Range(0.25f, 1f)] private float _ssfrResolutionScale = 0.5f;
        [SerializeField, Range(0.5f, 1.7f)] private float _ssfrRadiusScale = 1.0f;
        [SerializeField, Range(0.1f, 5f)] private float _ssfrThicknessScale = 1.2f;
        [SerializeField, Range(0.2f, 4f)] private float _ssfrBlurRadius = 1.5f;
        [SerializeField, Range(0.05f, 3f)] private float _ssfrDepthSigma = 0.7f;
        [SerializeField, Range(0.1f, 5f)] private float _ssfrAbsorption = 0.8f;
        [SerializeField, Range(0f, 0.1f)] private float _ssfrRefraction = 0.014f;
        [SerializeField, Range(0.1f, 1f)] private float _ssfrOpacity = 0.9f;
        [SerializeField] private Color _ssfrShallowColor = new Color(0.13f, 0.60f, 0.87f, 1f);
        [SerializeField] private Color _ssfrDeepColor = new Color(0.015f, 0.24f, 0.52f, 1f);

        public float SsfrResolutionScale => _ssfrResolutionScale;
        public float SsfrRadiusScale => _ssfrRadiusScale;
        public float SsfrThicknessScale => _ssfrThicknessScale;
        public float SsfrBlurRadius => _ssfrBlurRadius;
        public float SsfrDepthSigma => _ssfrDepthSigma;
        public float SsfrAbsorption => _ssfrAbsorption;
        public float SsfrRefraction => _ssfrRefraction;
        public float SsfrOpacity => _ssfrOpacity;
        public Color SsfrShallowColor => _ssfrShallowColor;
        public Color SsfrDeepColor => _ssfrDeepColor;
    }
}
