using UnityEngine;

namespace GuardWave
{
    /// <summary>Runs Flood2 with a starting budget and player-purchased walls.</summary>
    public sealed class Flood2Game : GuardWaveGame
    {
        protected override bool UsesFloodWaves => true;
        protected override int StartingBudget => FloodLogWall.STARTING_BUDGET;
        protected override int WallSlots => StartingBudget / FloodLogWall.PRICE;
        public override bool IsFlood2 => true;

        /// <summary>Limits wall purchases to the remaining budget.</summary>
        public override int GetRemainingPlacements(string materialId)
        {
            return materialId == "log_wall" ? Budget / FloodLogWall.PRICE
                : base.GetRemainingPlacements(materialId);
        }

        /// <summary>Anchors the purchased wall and assigns its damage slot before fluid registration.</summary>
        protected override void ConfigureSpawnedPiece(GuardWavePiece piece)
        {
            if (piece.MaterialId != "log_wall") return;
            piece.Body.isKinematic = true;
            piece.Body.useGravity = false;
            piece.GetComponent<FloodLogWall>().Place(this, (StartingBudget - Budget) / FloodLogWall.PRICE);
            Budget -= FloodLogWall.PRICE;
        }
    }
}
