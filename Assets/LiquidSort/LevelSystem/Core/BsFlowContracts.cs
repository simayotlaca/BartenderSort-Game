using System;

namespace BartenderSort.Core
{

    public enum BsFlowState
    {
        Menu = 0,
        Loading = 1,
        Playing = 2,
        Paused = 4,
        Won = 5,
        Failed = 6,
    }

    public enum BsRoundOutcome
    {
        Won,
        Failed,
    }

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
