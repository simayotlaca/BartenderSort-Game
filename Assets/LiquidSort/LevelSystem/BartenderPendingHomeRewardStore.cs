using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Visual receipt for an earned menu reward. The balance is already saved; this cannot change the
    /// economy.
    /// </summary>
    public readonly struct BartenderPendingHomeReward
    {
        public long Revision { get; }
        public int PreviousCoins { get; }
        public int FinalCoins { get; }
        public int CompletedLevelNumber { get; }
        public int NextLevelNumber { get; }
        public Vector2 SourceViewportPoint { get; }
        public bool IncludesLevelCelebration => CompletedLevelNumber > 0;

        internal BartenderPendingHomeReward(
            long revision,
            int previousCoins,
            int finalCoins,
            int completedLevelNumber,
            int nextLevelNumber,
            Vector2 sourceViewportPoint)
        {
            Revision = revision;
            PreviousCoins = previousCoins;
            FinalCoins = finalCoins;
            CompletedLevelNumber = completedLevelNumber;
            NextLevelNumber = nextLevelNumber;
            SourceViewportPoint = sourceViewportPoint;
        }
    }

    /// <summary>
    /// One reward slot survives scene changes, but not app exit. Consume claims it; the same revision is
    /// cleared only after presentation acknowledges it.
    /// </summary>
    public static class BartenderPendingHomeRewardStore
    {
        private static readonly object Gate = new object();

        private static BartenderPendingHomeReward pending;
        private static long nextRevision;
        private static bool hasPending;
        private static bool consumed;

        public static bool HasPending
        {
            get
            {
                lock (Gate) return hasPending;
            }
        }

        /// <summary>
        /// Stages a receipt with a unique process revision. Rejects invalid balances or level IDs and clamps
        /// the viewport point.
        /// </summary>
        public static bool TryStage(
            int previousCoins,
            int finalCoins,
            int rewardAmount,
            int completedLevelNumber,
            int nextLevelNumber,
            Vector2 sourceViewportPoint,
            out BartenderPendingHomeReward payload)
        {
            payload = default;
            if (!ValidSnapshot(previousCoins, finalCoins, rewardAmount,
                    completedLevelNumber, nextLevelNumber, sourceViewportPoint))
                return false;

            return TryStageValidated(previousCoins, finalCoins, completedLevelNumber,
                nextLevelNumber, sourceViewportPoint, out payload);
        }

        /// <summary>Stages already-saved coins without replaying the campaign level celebration.</summary>
        public static bool TryStageCoins(int previousCoins, int finalCoins,
            Vector2 sourceViewportPoint, out BartenderPendingHomeReward payload)
        {
            payload = default;
            if (previousCoins < 0 || finalCoins <= previousCoins
                || !IsFinite(sourceViewportPoint.x) || !IsFinite(sourceViewportPoint.y))
                return false;
            return TryStageValidated(previousCoins, finalCoins, 0, 0,
                sourceViewportPoint, out payload);
        }

        private static bool TryStageValidated(int previousCoins, int finalCoins,
            int completedLevelNumber, int nextLevelNumber, Vector2 sourceViewportPoint,
            out BartenderPendingHomeReward payload)
        {
            payload = default;
            lock (Gate)
            {
                if (hasPending) return false;

                long revision = NextRevision();
                pending = new BartenderPendingHomeReward(
                    revision,
                    previousCoins,
                    finalCoins,
                    completedLevelNumber,
                    nextLevelNumber,
                    new Vector2(
                        Mathf.Clamp01(sourceViewportPoint.x),
                        Mathf.Clamp01(sourceViewportPoint.y)));
                hasPending = true;
                consumed = false;
                payload = pending;
                return true;
            }
        }

        /// <summary>Claims the receipt for one presenter until that revision is acknowledged or released.</summary>
        public static bool TryConsume(out BartenderPendingHomeReward payload)
        {
            lock (Gate)
            {
                if (!hasPending || consumed)
                {
                    payload = default;
                    return false;
                }

                consumed = true;
                payload = pending;
                return true;
            }
        }

        /// <summary>Clears the matching claimed receipt after completion.</summary>
        public static bool TryAcknowledge(long revision)
        {
            lock (Gate)
            {
                if (!hasPending || !consumed || pending.Revision != revision)
                    return false;
                ClearPending();
                return true;
            }
        }

        /// <summary>Releases an unfinished claim without deleting its data so another presenter can try.</summary>
        public static bool TryRelease(long revision)
        {
            lock (Gate)
            {
                if (!hasPending || !consumed || pending.Revision != revision)
                    return false;
                consumed = false;
                return true;
            }
        }

        /// <summary>
        /// Cancels only the matching unclaimed receipt before navigation. Old callbacks cannot remove a
        /// newer reward; claimed receipts need Ack or Release.
        /// </summary>
        public static bool TryDiscard(long revision)
        {
            lock (Gate)
            {
                if (!hasPending || consumed || pending.Revision != revision) return false;
                ClearPending();
                return true;
            }
        }

        /// <summary>
        /// Clears an old unseen animation receipt to make room for a new win. Saved coins stay intact; true
        /// means one receipt was dropped.
        /// </summary>
        public static bool DiscardStalePending()
        {
            lock (Gate)
            {
                if (!hasPending) return false;
                ClearPending();
                return true;
            }
        }

        private static bool ValidSnapshot(
            int previousCoins,
            int finalCoins,
            int rewardAmount,
            int completedLevelNumber,
            int nextLevelNumber,
            Vector2 sourceViewportPoint)
        {
            if (previousCoins < 0 || finalCoins < 0 || rewardAmount <= 0)
                return false;
            if ((long)finalCoins - previousCoins != rewardAmount) return false;
            // A completed campaign may return to an earlier replay level. These are presentation
            // endpoints, not a request to change unlocked progress.
            if (completedLevelNumber <= 0 || nextLevelNumber <= 0)
                return false;
            return IsFinite(sourceViewportPoint.x)
                   && IsFinite(sourceViewportPoint.y);
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static long NextRevision()
        {
            nextRevision = nextRevision == long.MaxValue ? 1L : nextRevision + 1L;
            if (nextRevision == 0L) nextRevision = 1L;
            return nextRevision;
        }

        private static void ClearPending()
        {
            pending = default;
            hasPending = false;
            consumed = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            lock (Gate)
                ClearPending();
        }
    }
}
