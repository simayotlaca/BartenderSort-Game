using System;

namespace BartenderSort.Core
{
    internal enum BsShelfCoveredLoadState
    {
        Idle = 0,
        Armed = 1,
        Refreshing = 2,
    }

    internal enum BsShelfSynchronizationState
    {
        Live = 0,
        DeferredClean = 1,
        DeferredDirty = 2,
    }

    internal enum BsShelfGarnishRevealState
    {
        Idle = 0,
        Synchronizing = 1,
    }

    internal enum BsShelfGarnishSynchronizationStart
    {
        NoTransaction = 0,
        Started = 1,
        Rejected = 2,
    }

    internal enum BsShelfPresentationTrack
    {
        CoveredLoad = 0,
        Synchronization = 1,
        GarnishReveal = 2,
    }

    internal enum BsShelfSettleReason
    {
        Completed = 0,
        Cancelled = 1,
        Disabled = 2,
        LevelChanged = 3,
        TimedOut = 4,
    }

    /// <summary>Increasing callback IDs shared by the three shelf tracks.</summary>
    internal readonly struct BsShelfFlowToken : IEquatable<BsShelfFlowToken>
    {
        public long Value { get; }
        public bool IsValid => Value > 0L;

        internal BsShelfFlowToken(long value)
        {
            Value = value;
        }

        public bool Equals(BsShelfFlowToken other) => Value == other.Value;
        public override bool Equals(object obj) =>
            obj is BsShelfFlowToken other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(BsShelfFlowToken left, BsShelfFlowToken right) =>
            left.Equals(right);
        public static bool operator !=(BsShelfFlowToken left, BsShelfFlowToken right) =>
            !left.Equals(right);
    }

    /// <summary>Exact synchronization capability retained by pour presentation.</summary>
    public readonly struct BsShelfSynchronizationLease :
        IEquatable<BsShelfSynchronizationLease>
    {
        private readonly BsShelfFlowToken token;

        public long Value => token.Value;
        public bool IsValid => token.IsValid;
        internal BsShelfFlowToken Token => token;

        internal BsShelfSynchronizationLease(BsShelfFlowToken token)
        {
            this.token = token;
        }

        public bool Equals(BsShelfSynchronizationLease other) =>
            token == other.token;
        public override bool Equals(object obj) =>
            obj is BsShelfSynchronizationLease other && Equals(other);
        public override int GetHashCode() => token.GetHashCode();
        public static bool operator ==(
            BsShelfSynchronizationLease left,
            BsShelfSynchronizationLease right) => left.Equals(right);
        public static bool operator !=(
            BsShelfSynchronizationLease left,
            BsShelfSynchronizationLease right) => !left.Equals(right);
    }

    internal readonly struct BsShelfFlowSettlement
    {
        public BsShelfPresentationTrack Track { get; }
        public BsShelfSettleReason Reason { get; }
        public object Context { get; }
        public object Change { get; }
        public object Receipt { get; }
        public int Revision { get; }
        public bool ShouldRefresh { get; }

        internal BsShelfFlowSettlement(
            BsShelfPresentationTrack track,
            BsShelfSettleReason reason,
            object context,
            object change,
            object receipt,
            int revision,
            bool shouldRefresh)
        {
            Track = track;
            Reason = reason;
            Context = context;
            Change = change;
            Receipt = receipt;
            Revision = revision;
            ShouldRefresh = shouldRefresh;
        }
    }

    /// <summary>
    /// Owns covered loading, shelf refresh holds and garnish updates. Tracks stay separate but share
    /// identity, timeout and cleanup rules.
    /// </summary>
    internal sealed class BsShelfPresentationFlow
    {
        private sealed class Run
        {
            public BsShelfPresentationTrack Track;
            public BsShelfFlowToken Token;
            public object Owner;
            public object Context;
            public object Receipt;
            public object LatestChange;
            public int State;
            public int BaselineRevision = -1;
            public int Revision = -1;
            public BsRoundCommandStamp Stamp;
            public double Deadline;
        }

        private long nextTokenValue;
        private Run covered;
        private Run synchronization;
        private Run garnish;

        public BsShelfCoveredLoadState CoveredState => covered != null
            ? (BsShelfCoveredLoadState)covered.State
            : BsShelfCoveredLoadState.Idle;
        public bool CoveredRefreshPending =>
            CoveredState == BsShelfCoveredLoadState.Refreshing;
        public BsShelfSynchronizationState SynchronizationState =>
            synchronization != null
                ? (BsShelfSynchronizationState)synchronization.State
                : BsShelfSynchronizationState.Live;
        public bool SynchronizationDeferred => synchronization != null;
        public BsShelfGarnishRevealState GarnishState => garnish != null
            ? BsShelfGarnishRevealState.Synchronizing
            : BsShelfGarnishRevealState.Idle;

        public bool TryArmCovered(
            object owner,
            object context,
            double deadline,
            out BsShelfFlowToken token)
        {
            token = default;
            if (owner == null || context == null || !ValidDeadline(deadline)
                || covered != null)
                return false;

            if (!TryNextToken(out token)) return false;
            covered = new Run
            {
                Track = BsShelfPresentationTrack.CoveredLoad,
                Token = token,
                Owner = owner,
                Context = context,
                State = (int)BsShelfCoveredLoadState.Armed,
                Deadline = deadline,
            };
            return true;
        }

        public bool TryGetCovered(
            out object owner,
            out BsShelfFlowToken token,
            out object context)
        {
            owner = covered?.Owner;
            token = covered != null ? covered.Token : default;
            context = covered?.Context;
            return covered != null;
        }

        public bool TryBeginCovered(
            object owner,
            BsShelfFlowToken token,
            int revision,
            BsRoundCommandStamp stamp,
            double deadline)
        {
            if (!Matches(covered, owner, token)
                || CoveredState != BsShelfCoveredLoadState.Armed
                || revision < 0 || !stamp.IsValid || stamp.BoardRevision != revision
                || !ValidDeadline(deadline))
                return false;

            covered.Revision = revision;
            covered.Stamp = stamp;
            covered.Deadline = deadline;
            covered.State = (int)BsShelfCoveredLoadState.Refreshing;
            return true;
        }

        public bool IsCoveredCurrent(
            object owner,
            BsShelfFlowToken token,
            BsRoundCommandStamp currentStamp) =>
            Matches(covered, owner, token)
            && CoveredState == BsShelfCoveredLoadState.Refreshing
            && currentStamp.IsValid && currentStamp == covered.Stamp;

        public bool TrySettleCovered(
            object owner,
            BsShelfFlowToken token,
            BsShelfSettleReason reason,
            out BsShelfFlowSettlement settlement) =>
            TrySettle(
                BsShelfPresentationTrack.CoveredLoad,
                owner,
                token,
                null,
                -1,
                reason,
                false,
                false,
                out settlement);

        public bool TryBeginSynchronization(
            object owner,
            int revision,
            double deadline,
            out BsShelfSynchronizationLease lease)
        {
            lease = default;
            if (owner == null || revision < 0 || !ValidDeadline(deadline)
                || synchronization != null)
                return false;

            if (!TryNextToken(out BsShelfFlowToken token)) return false;
            synchronization = new Run
            {
                Track = BsShelfPresentationTrack.Synchronization,
                Token = token,
                Owner = owner,
                State = (int)BsShelfSynchronizationState.DeferredClean,
                BaselineRevision = revision,
                Revision = revision,
                Deadline = deadline,
            };
            lease = new BsShelfSynchronizationLease(token);
            return true;
        }

        public bool IsSynchronizationOwnedBy(
            object owner,
            BsShelfSynchronizationLease lease) =>
            Matches(synchronization, owner, lease.Token);

        public bool TryObserveBoardChange(int revision, object change)
        {
            if (synchronization == null) return false;
            if (revision >= synchronization.BaselineRevision)
            {
                if (revision > synchronization.Revision)
                {
                    synchronization.Revision = revision;
                    synchronization.LatestChange = change;
                }
                else if (revision == synchronization.Revision && change != null)
                {
                    synchronization.LatestChange = change;
                }
                synchronization.State =
                    (int)BsShelfSynchronizationState.DeferredDirty;
            }
            return true;
        }

        public bool TryDropSynchronization(
            object owner,
            BsShelfSynchronizationLease lease,
            BsShelfSettleReason reason,
            out BsShelfFlowSettlement settlement) =>
            TrySettle(
                BsShelfPresentationTrack.Synchronization,
                owner,
                lease.Token,
                null,
                -1,
                reason,
                false,
                false,
                out settlement);

        public bool TryEndSynchronization(
            object owner,
            BsShelfSynchronizationLease lease,
            bool forceRefresh,
            out BsShelfFlowSettlement settlement) =>
            TrySettle(
                BsShelfPresentationTrack.Synchronization,
                owner,
                lease.Token,
                null,
                -1,
                BsShelfSettleReason.Completed,
                forceRefresh,
                false,
                out settlement);

        /// <summary>
        /// Starts the synchronous garnish update for BoardCommitted. Its later fade stays a separate glass
        /// effect.
        /// </summary>
        public BsShelfGarnishSynchronizationStart BeginGarnishSynchronization(
            object owner,
            object receipt,
            int revision,
            double deadline,
            out BsShelfFlowToken token)
        {
            token = default;
            if (receipt == null)
                return BsShelfGarnishSynchronizationStart.NoTransaction;
            if (owner == null || revision < 0 || !ValidDeadline(deadline))
                return BsShelfGarnishSynchronizationStart.Rejected;
            if (garnish != null)
            {
                if (revision <= garnish.Revision)
                    return BsShelfGarnishSynchronizationStart.Rejected;
            }
            if (!TryNextToken(out token))
                return BsShelfGarnishSynchronizationStart.Rejected;
            if (garnish != null)
            {
                SettleCurrent(
                    BsShelfPresentationTrack.GarnishReveal,
                    BsShelfSettleReason.Cancelled,
                    false,
                    out _);
            }

            garnish = new Run
            {
                Track = BsShelfPresentationTrack.GarnishReveal,
                Token = token,
                Owner = owner,
                Receipt = receipt,
                Revision = revision,
                State = (int)BsShelfGarnishRevealState.Synchronizing,
                Deadline = deadline,
            };
            return BsShelfGarnishSynchronizationStart.Started;
        }

        public bool TrySettleGarnishSynchronization(
            object owner,
            BsShelfFlowToken token,
            object receipt,
            int revision,
            BsShelfSettleReason reason,
            out BsShelfFlowSettlement settlement) =>
            TrySettle(
                BsShelfPresentationTrack.GarnishReveal,
                owner,
                token,
                receipt,
                revision,
                reason,
                false,
                false,
                out settlement);

        public int SettleExpired(
            double now,
            Action<BsShelfFlowSettlement> onSettled)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return 0;
            BsShelfFlowSettlement coveredSettlement = default;
            BsShelfFlowSettlement synchronizationSettlement = default;
            BsShelfFlowSettlement garnishSettlement = default;
            bool hasCovered = IsExpired(covered, now)
                && SettleCurrent(
                    BsShelfPresentationTrack.CoveredLoad,
                    BsShelfSettleReason.TimedOut,
                    false,
                    out coveredSettlement);
            bool hasSynchronization = IsExpired(synchronization, now)
                && SettleCurrent(
                    BsShelfPresentationTrack.Synchronization,
                    BsShelfSettleReason.TimedOut,
                    true,
                    out synchronizationSettlement);
            bool hasGarnish = IsExpired(garnish, now)
                && SettleCurrent(
                    BsShelfPresentationTrack.GarnishReveal,
                    BsShelfSettleReason.TimedOut,
                    false,
                    out garnishSettlement);
            NotifySettlements(
                onSettled,
                hasCovered,
                coveredSettlement,
                hasSynchronization,
                synchronizationSettlement,
                hasGarnish,
                garnishSettlement);
            return Count(hasCovered, hasSynchronization, hasGarnish);
        }

        public int SettleAll(
            BsShelfSettleReason reason,
            Action<BsShelfFlowSettlement> onSettled)
        {
            bool hasCovered = SettleCurrent(
                BsShelfPresentationTrack.CoveredLoad,
                reason,
                false,
                out BsShelfFlowSettlement coveredSettlement);
            bool hasSynchronization = SettleCurrent(
                BsShelfPresentationTrack.Synchronization,
                reason,
                false,
                out BsShelfFlowSettlement synchronizationSettlement);
            bool hasGarnish = SettleCurrent(
                BsShelfPresentationTrack.GarnishReveal,
                reason,
                false,
                out BsShelfFlowSettlement garnishSettlement);
            NotifySettlements(
                onSettled,
                hasCovered,
                coveredSettlement,
                hasSynchronization,
                synchronizationSettlement,
                hasGarnish,
                garnishSettlement);
            return Count(hasCovered, hasSynchronization, hasGarnish);
        }

        /// <summary>Preserves the armed load intended for this LevelLoaded callback.</summary>
        public int SettleLevelBoundary(
            Action<BsShelfFlowSettlement> onSettled)
        {
            BsShelfFlowSettlement coveredSettlement = default;
            bool hasCovered =
                CoveredState == BsShelfCoveredLoadState.Refreshing
                && SettleCurrent(
                    BsShelfPresentationTrack.CoveredLoad,
                    BsShelfSettleReason.LevelChanged,
                    false,
                    out coveredSettlement);
            bool hasSynchronization = SettleCurrent(
                BsShelfPresentationTrack.Synchronization,
                BsShelfSettleReason.LevelChanged,
                false,
                out BsShelfFlowSettlement synchronizationSettlement);
            bool hasGarnish = SettleCurrent(
                BsShelfPresentationTrack.GarnishReveal,
                BsShelfSettleReason.LevelChanged,
                false,
                out BsShelfFlowSettlement garnishSettlement);
            NotifySettlements(
                onSettled,
                hasCovered,
                coveredSettlement,
                hasSynchronization,
                synchronizationSettlement,
                hasGarnish,
                garnishSettlement);
            return Count(hasCovered, hasSynchronization, hasGarnish);
        }

        private static void NotifySettlements(
            Action<BsShelfFlowSettlement> callback,
            bool hasFirst,
            BsShelfFlowSettlement first,
            bool hasSecond,
            BsShelfFlowSettlement second,
            bool hasThird,
            BsShelfFlowSettlement third)
        {
            if (callback == null) return;
            Exception failure = null;
            try { if (hasFirst) callback(first); }
            catch (Exception exception) { failure = exception; }
            try { if (hasSecond) callback(second); }
            catch (Exception exception) { if (failure == null) failure = exception; }
            try { if (hasThird) callback(third); }
            catch (Exception exception) { if (failure == null) failure = exception; }
            if (failure != null) throw failure;
        }

        private static bool IsExpired(Run run, double now) =>
            run != null && now >= run.Deadline;

        private static int Count(bool first, bool second, bool third) =>
            (first ? 1 : 0) + (second ? 1 : 0) + (third ? 1 : 0);

        private bool SettleCurrent(
            BsShelfPresentationTrack track,
            BsShelfSettleReason reason,
            bool forceRefresh,
            out BsShelfFlowSettlement settlement)
        {
            Run run = RunFor(track);
            if (run == null)
            {
                settlement = default;
                return false;
            }
            return TrySettle(
                track,
                run.Owner,
                run.Token,
                run.Receipt,
                run.Revision,
                reason,
                forceRefresh,
                true,
                out settlement);
        }

        private bool TrySettle(
            BsShelfPresentationTrack track,
            object owner,
            BsShelfFlowToken token,
            object receipt,
            int revision,
            BsShelfSettleReason reason,
            bool forceRefresh,
            bool internalSettle,
            out BsShelfFlowSettlement settlement)
        {
            settlement = default;
            Run run = RunFor(track);
            if (run == null || !KnownReason(reason)
                || !Matches(run, owner, token))
                return false;
            if (track == BsShelfPresentationTrack.GarnishReveal
                && (!internalSettle
                    && (receipt == null
                        || !ReferenceEquals(receipt, run.Receipt)
                        || revision != run.Revision)))
                return false;

            bool shouldRefresh = track == BsShelfPresentationTrack.Synchronization
                && (forceRefresh
                    || (BsShelfSynchronizationState)run.State
                        == BsShelfSynchronizationState.DeferredDirty);
            settlement = new BsShelfFlowSettlement(
                track,
                reason,
                run.Context,
                run.LatestChange,
                run.Receipt,
                run.Revision,
                shouldRefresh);
            SetRun(track, null);
            return true;
        }

        private Run RunFor(BsShelfPresentationTrack track)
        {
            switch (track)
            {
                case BsShelfPresentationTrack.CoveredLoad: return covered;
                case BsShelfPresentationTrack.Synchronization: return synchronization;
                case BsShelfPresentationTrack.GarnishReveal: return garnish;
                default: return null;
            }
        }

        private void SetRun(BsShelfPresentationTrack track, Run value)
        {
            switch (track)
            {
                case BsShelfPresentationTrack.CoveredLoad:
                    covered = value;
                    break;
                case BsShelfPresentationTrack.Synchronization:
                    synchronization = value;
                    break;
                case BsShelfPresentationTrack.GarnishReveal:
                    garnish = value;
                    break;
            }
        }

        private static bool Matches(Run run, object owner, BsShelfFlowToken token) =>
            run != null && owner != null && token.IsValid
            && run.Token == token && ReferenceEquals(run.Owner, owner);

        private bool TryNextToken(out BsShelfFlowToken token)
        {
            if (nextTokenValue == long.MaxValue)
            {
                token = default;
                return false;
            }
            nextTokenValue++;
            token = new BsShelfFlowToken(nextTokenValue);
            return true;
        }

        private static bool ValidDeadline(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

        private static bool KnownReason(BsShelfSettleReason value) =>
            value >= BsShelfSettleReason.Completed
            && value <= BsShelfSettleReason.TimedOut;
    }
}
