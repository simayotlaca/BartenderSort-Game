using System;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    /// <summary>Daily order targets and coin rewards.</summary>
    public static class BartenderDailyOrdersTuning
    {
        public const int DeliveredOrderTarget = 5;
        public const int WonLevelTarget = 2;
        public const int ServedUnitTarget = 10;
        [Obsolete("Use ServedUnitTarget; transfers no longer count as service.")]
        public const int PouredUnitTarget = ServedUnitTarget;

        public const int RewardPerTask = 100;
        public const int CompletionBonus = 200;
        public const int TotalRewardCoins =
            RewardPerTask * 3 + CompletionBonus;
    }

    /// <summary>
    /// Immutable projection used by the menu. Persistence remains owned by
    /// <see cref="BartenderProgressService"/>.
    /// </summary>
    public readonly struct BartenderDailyOrdersSnapshot
        : IEquatable<BartenderDailyOrdersSnapshot>
    {
        public long UtcDayKey { get; }
        public int DeliveredOrders { get; }
        public int WonLevels { get; }
        public int ServedUnits { get; }
        [Obsolete("Use ServedUnits; only delivered drinks count.")]
        public int PouredUnits => ServedUnits;
        public bool RewardClaimed { get; }
        public long NextResetUtcTicks { get; }

        internal BartenderDailyOrdersSnapshot(
            long utcDayKey,
            int deliveredOrders,
            int wonLevels,
            int servedUnits,
            bool rewardClaimed)
        {
            UtcDayKey = utcDayKey;
            DeliveredOrders = Math.Max(0, Math.Min(
                BartenderDailyOrdersTuning.DeliveredOrderTarget,
                deliveredOrders));
            WonLevels = Math.Max(0, Math.Min(
                BartenderDailyOrdersTuning.WonLevelTarget,
                wonLevels));
            ServedUnits = Math.Max(0, Math.Min(
                BartenderDailyOrdersTuning.ServedUnitTarget,
                servedUnits));
            RewardClaimed = rewardClaimed;
            NextResetUtcTicks = utcDayKey >= 0L
                && utcDayKey < DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
                ? (utcDayKey + 1L) * TimeSpan.TicksPerDay
                : DateTime.MaxValue.Ticks;
        }

        public int CompletedTaskCount =>
            (DeliveredOrders >= BartenderDailyOrdersTuning.DeliveredOrderTarget ? 1 : 0)
            + (WonLevels >= BartenderDailyOrdersTuning.WonLevelTarget ? 1 : 0)
            + (ServedUnits >= BartenderDailyOrdersTuning.ServedUnitTarget ? 1 : 0);

        public bool IsComplete => CompletedTaskCount == 3;
        public bool CanClaim => IsComplete && !RewardClaimed;

        public TimeSpan RemainingUntilReset(long utcNowTicks)
        {
            if (NextResetUtcTicks <= utcNowTicks) return TimeSpan.Zero;
            return TimeSpan.FromTicks(NextResetUtcTicks - utcNowTicks);
        }

        public bool Equals(BartenderDailyOrdersSnapshot other) =>
            UtcDayKey == other.UtcDayKey
            && DeliveredOrders == other.DeliveredOrders
            && WonLevels == other.WonLevels
            && ServedUnits == other.ServedUnits
            && RewardClaimed == other.RewardClaimed;

        public override bool Equals(object obj) =>
            obj is BartenderDailyOrdersSnapshot other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = UtcDayKey.GetHashCode();
                hash = (hash * 397) ^ DeliveredOrders;
                hash = (hash * 397) ^ WonLevels;
                hash = (hash * 397) ^ ServedUnits;
                return (hash * 397) ^ (RewardClaimed ? 1 : 0);
            }
        }

        public static bool operator ==(
            BartenderDailyOrdersSnapshot left,
            BartenderDailyOrdersSnapshot right) => left.Equals(right);

        public static bool operator !=(
            BartenderDailyOrdersSnapshot left,
            BartenderDailyOrdersSnapshot right) => !left.Equals(right);
    }

    internal enum BartenderDailyActivityKind
    {
        None = 0,
        PouredUnits = 1,
        DeliveredOrder = 2,
        // v5: one logical delivery credits both tasks and the permanent recipe book.
        DeliveredRecipe = 3,
    }

    /// <summary>
    /// Exact accepted board activity that may be folded into the same durable progress
    /// transaction as its prospective active-round snapshot.
    /// </summary>
    internal readonly struct BartenderDailyActivityReceipt
    {
        internal BartenderDailyActivityKind Kind { get; }
        internal BsAttemptId AttemptId { get; }
        internal BsOperationId OperationId { get; }
        internal long DomainRevision { get; }
        internal int BoardRevision { get; }
        internal int Amount { get; }
        internal string RecipeKey { get; }
        internal int OrderIndex { get; }

        internal bool IsValid =>
            Kind == BartenderDailyActivityKind.DeliveredRecipe
            && AttemptId.IsValid
            && OperationId.IsValid
            && DomainRevision > 0L
            && BoardRevision >= 0
            && Amount > 0
            && OrderIndex >= 0
            && BartenderRecipeKey.TryDecode(RecipeKey, out OrderDef recipe)
            && recipe.Contents.Count == Amount;

        private BartenderDailyActivityReceipt(
            BartenderDailyActivityKind kind,
            BsAttemptId attemptId,
            BsOperationId operationId,
            long domainRevision,
            int boardRevision,
            int amount,
            string recipeKey,
            int orderIndex)
        {
            Kind = kind;
            AttemptId = attemptId;
            OperationId = operationId;
            DomainRevision = domainRevision;
            BoardRevision = boardRevision;
            Amount = amount;
            RecipeKey = recipeKey;
            OrderIndex = orderIndex;
        }

        internal static BartenderDailyActivityReceipt From(
            BsStagedBoardMutation staged)
        {
            if (staged == null || staged.Kind != BsBoardMutationKind.Delivery
                || staged.DeliveryEvidence == null) return default;
            OrderDef order = staged.DeliveryEvidence.DeliveredOrder;
            RtGlass glass = staged.DeliveryEvidence.DeliveredGlass;
            return new BartenderDailyActivityReceipt(
                BartenderDailyActivityKind.DeliveredRecipe,
                staged.AttemptId, staged.OperationId, staged.Revision,
                staged.BoardRevision, glass.Layers.Count,
                BartenderRecipeKey.From(order), order?.RuntimeOrderIndex ?? -1);
        }
    }
}
