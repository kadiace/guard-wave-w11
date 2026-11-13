using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Diagnostic-only observer attached to the Forest Defense Flag.
    /// Reports the REAL inputs to floating-body lift without changing physics.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    public sealed class GuardWaveBuoyancyProbe : MonoBehaviour
    {
        [SerializeField, Min(0.25f)] private float reportIntervalSeconds = 1f;
        [SerializeField] private bool includeNearbyFloatingPieces = true;
        [SerializeField, Min(0f)] private float nearbyRadius = 25f;
        [SerializeField, Range(0, 20)] private int maxNearbyPieces = 8;

        private readonly List<GuardWavePiece> _nearby = new List<GuardWavePiece>();
        private GuardWaveGame _game;
        private GuardWavePiece _flag;
        private float _nextReportTime;

        private void Awake()
        {
            _flag = GetComponent<GuardWavePiece>();
        }

        private void Start()
        {
            _game = FindFirstObjectByType<GuardWaveGame>();
            if (_game == null)
                Debug.LogWarning("[Forest Buoyancy] GuardWaveGame not found; diagnostics disabled.", this);
        }

        private void FixedUpdate()
        {
            if (_game == null || Time.fixedTime < _nextReportTime) return;
            _nextReportTime = Time.fixedTime + Mathf.Max(0.25f, reportIntervalSeconds);

            Report(_flag);
            if (!includeNearbyFloatingPieces || maxNearbyPieces <= 0) return;

            _nearby.Clear();
            float radiusSq = nearbyRadius * nearbyRadius;
            foreach (GuardWavePiece candidate in FindObjectsByType<GuardWavePiece>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate == null || candidate == _flag || !candidate.enabled ||
                    candidate.RelativeFluidDensity >= 1f) continue;
                Vector3 distance = candidate.transform.position - transform.position;
                distance.y = 0f;
                if (distance.sqrMagnitude <= radiusSq) _nearby.Add(candidate);
            }

            _nearby.Sort((a, b) =>
            {
                Vector3 da = a.transform.position - transform.position;
                Vector3 db = b.transform.position - transform.position;
                da.y = 0f;
                db.y = 0f;
                return da.sqrMagnitude.CompareTo(db.sqrMagnitude);
            });

            for (int i = 0; i < Mathf.Min(maxNearbyPieces, _nearby.Count); ++i)
                Report(_nearby[i]);
        }

        private void Report(GuardWavePiece piece)
        {
            if (piece == null || !piece.gameObject.activeInHierarchy) return;

            if (!_game.TryGetBuoyancyProbeSample(piece.Body, out var sample))
            {
                Debug.Log(
                    $"[Forest Buoyancy] t={Time.fixedTime:F2}s object={piece.name} " +
                    "status=NOT_REGISTERED_IN_FLIP (no solver/body index)", piece);
                return;
            }

            // This is a render-mesh AABB estimate against the *physics* surface.
            // It is NOT the SSFR visual water surface and NOT submerged volume.
            float visibleHeightFraction = -1f;
            Bounds visualBounds = default;
            bool hasRenderer = false;
            foreach (Renderer renderer in piece.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled) continue;
                if (!hasRenderer) { visualBounds = renderer.bounds; hasRenderer = true; }
                else visualBounds.Encapsulate(renderer.bounds);
            }
            if (hasRenderer && sample.WaterlineSampleAgeSeconds >= 0f)
                visibleHeightFraction = Mathf.Clamp01(
                    (sample.PhysicsWaterlineY - visualBounds.min.y) /
                    Mathf.Max(0.0001f, visualBounds.size.y));

            Debug.Log(
                $"[Forest Buoyancy] t={Time.fixedTime:F2}s object={piece.name} " +
                $"material={piece.MaterialId} state={sample.State} " +
                $"density={sample.RelativeDensity:F3} mass={sample.MassKg:F3}kg " +
                $"waterlineRawY={sample.RawWaterlineY:F3} " +
                $"waterlinePhysicsY={sample.PhysicsWaterlineY:F3} " +
                $"readbackAge={sample.WaterlineSampleAgeSeconds:F3}s " +
                $"coverage={sample.FluidCoverage:F3} " +
                $"shapeSubmerged={sample.ShapeSubmergedFraction:F3} " +
                $"effectiveSubmerged={sample.EffectiveSubmergedFraction:F3} " +
                $"renderAABBHeightSubmerged={visibleHeightFraction:F3} (NOT SSFR) " +
                $"colliders={sample.SampledColliders} " +
                $"buoyancyOverWeight={sample.BuoyancyRatio:F3} " +
                $"buoyancy={sample.BuoyancyForceN:F2}N " +
                $"gravity={sample.GravityForceN:F2}N " +
                $"verticalDamping={sample.VerticalDampingForceN:F2}N " +
                $"netYWithoutContacts={sample.NetVerticalForceN:F2}N " +
                $"velocityY={sample.VerticalVelocity:F3}m/s " +
                $"flowXZ=({sample.FlowXZ.x:F2},{sample.FlowXZ.y:F2})m/s",
                piece);
        }
    }
}
