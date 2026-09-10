using System;

namespace BartenderSort.Core
{
    // BsRoundCoordinator owns round state. These are only UI projections and callback IDs, with no separate
    // transitions.

    /// <summary>Maps coordinator state for older UI code.</summary>
    public enum BsFlowState
    {
        Menu = 0,
        Loading = 1,
        Playing = 2,
        // Keep value 3 reserved for the old Busy state so saves and telemetry still match.
        Paused = 4,
        Won = 5,
        Failed = 6,
    }

    /// <summary>Win or failure for result views; this does not own round state.</summary>
    public enum BsRoundOutcome
    {
        Won,
        Failed,
    }

    /// <summary>
    /// RoundId changes per level instance; GameplayEpoch changes on load or terminal invalidation. Together
    /// they block old callbacks from changing a new round.
    /// </summary>
    public readonly struct BsRoundToken : IEquatable<BsRoundToken>
    {
        public int RoundId { get; }
        public int GameplayEpoch { get; }

        public BsRoundToken(int roundId, int gameplayEpoch)
        {
            RoundId = roundId;
            GameplayEpoch = gameplayEpoch;
        }

        public bool Equals(BsRoundToken other) =>
            RoundId == other.RoundId && GameplayEpoch == other.GameplayEpoch;

        public override bool Equals(object obj) => obj is BsRoundToken other && Equals(other);
        public override int GetHashCode() => (RoundId * 397) ^ GameplayEpoch;
        public static bool operator ==(BsRoundToken left, BsRoundToken right) => left.Equals(right);
        public static bool operator !=(BsRoundToken left, BsRoundToken right) => !left.Equals(right);
        public override string ToString() => $"Round {RoundId} / Epoch {GameplayEpoch}";
    }
}
