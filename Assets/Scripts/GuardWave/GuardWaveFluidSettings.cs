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
        [SerializeField, Range(1000, 100000)] private int _maxParticles = 24000;
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
    }
}
