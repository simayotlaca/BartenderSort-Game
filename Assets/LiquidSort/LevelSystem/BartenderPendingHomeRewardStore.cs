using UnityEngine;

namespace LiquidSort.Levels
{
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

        public static bool TryDiscard(long revision)
        {
            lock (Gate)
            {
                if (!hasPending || consumed || pending.Revision != revision) return false;
                ClearPending();
                return true;
            }
        }

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
