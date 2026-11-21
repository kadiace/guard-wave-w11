using System;

using UnityEngine;

namespace GuardWave
{
    /// <summary>Sets up the original Flood stage and its V-shaped initial log wall.</summary>
    public sealed class FloodGame : GuardWaveGame
    {
        protected override bool UsesFloodWaves => true;

        /// <summary>
        /// Recreate the original Flood V wall by normally spawning 13 logs.
        /// They have the same Rigidbody, buoyancy and auto-glue behavior
        /// as a log the player places, with no pre-authored FixedJoints.
        /// World positions match the former Initial Wall hierarchy exactly.
        /// </summary>
        protected override GameObject CreateInitialWall()
        {
            GuardWaveMaterial log = Catalog.Get("log");
            if (log == null)
                throw new InvalidOperationException("Flood Initial Wall requires log material");

            GameObject wall = new GameObject("Initial Wall (Runtime)");
            // Original parent (0,-5,6), child Y=-0.8, X=-1.8..1.8
            // in 0.3m steps, local Z=-abs(X), all at zero rotation.
            for (int i = -6; i <= 6; i++)
            {
                float x = i * 0.3f;
                Vector3 worldPosition = new Vector3(x, -4f, 6f - Mathf.Abs(x));
                GuardWavePiece piece = Spawn(log, worldPosition);
                piece.name = "Log (" + (i + 7) + ")";
                piece.transform.SetParent(wall.transform, true);
            }
            StagePlacement.SyncPhysics();
            return wall;
        }
    }
}
