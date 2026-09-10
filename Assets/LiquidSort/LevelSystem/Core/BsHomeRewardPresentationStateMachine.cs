namespace BartenderSort.Core
{
    /// <summary>Keeps home reward visuals in order without knowing rounds, balances, scenes or tweens.</summary>
    public enum BsHomeRewardPresentationState
    {
        Idle = 0,
        Prepared = 1,
        LevelAdvancing = 2,
        Scattering = 3,
        Collecting = 4,
        Settling = 5,
        LevelRevealing = 6,
        Interrupted = 7,
    }

    public enum BsHomeRewardPresentationTrigger
    {
        Prepare,
        BeginLevelAdvance,
        LevelAdvanceCompleted,
        BeginCollect,
        BeginSettle,
        PresentationCompleted,
        Reset,
        LevelSkinSwapped,
        PresentationInterrupted,
        BeginCoinReward,
    }

    /// <summary>Home reward state with no Unity dependency. Reset is always accepted and safe to repeat.</summary>
    public sealed class BsHomeRewardPresentationStateMachine
    {
        public BsHomeRewardPresentationState State { get; private set; } =
            BsHomeRewardPresentationState.Idle;

        public bool Dispatch(BsHomeRewardPresentationTrigger trigger)
        {
            if (!TryResolve(State, trigger,
                    out BsHomeRewardPresentationState next))
                return false;

            State = next;
            return true;
        }

        public static bool TryResolve(
            BsHomeRewardPresentationState from,
            BsHomeRewardPresentationTrigger trigger,
            out BsHomeRewardPresentationState next)
        {
            next = from;
            int stateValue = (int)from;
            int triggerValue = (int)trigger;
            if (stateValue < (int)BsHomeRewardPresentationState.Idle
                || stateValue > (int)BsHomeRewardPresentationState.Interrupted
                || triggerValue < (int)BsHomeRewardPresentationTrigger.Prepare
                || triggerValue > (int)BsHomeRewardPresentationTrigger.BeginCoinReward)
                return false;

            if (trigger == BsHomeRewardPresentationTrigger.Reset)
            {
                next = BsHomeRewardPresentationState.Idle;
                return true;
            }

            if (trigger == BsHomeRewardPresentationTrigger.PresentationInterrupted)
            {
                if (from == BsHomeRewardPresentationState.Idle
                    || from == BsHomeRewardPresentationState.Interrupted)
                    return false;
                next = BsHomeRewardPresentationState.Interrupted;
                return true;
            }

            switch (from)
            {
                case BsHomeRewardPresentationState.Idle:
                    if (trigger != BsHomeRewardPresentationTrigger.Prepare)
                        return false;
                    next = BsHomeRewardPresentationState.Prepared;
                    return true;

                case BsHomeRewardPresentationState.Prepared:
                    if (trigger == BsHomeRewardPresentationTrigger.BeginCoinReward)
                    {
                        next = BsHomeRewardPresentationState.Scattering;
                        return true;
                    }
                    if (trigger != BsHomeRewardPresentationTrigger.BeginLevelAdvance)
                        return false;
                    next = BsHomeRewardPresentationState.LevelAdvancing;
                    return true;

                case BsHomeRewardPresentationState.LevelAdvancing:
                    if (trigger != BsHomeRewardPresentationTrigger.LevelSkinSwapped)
                        return false;
                    next = BsHomeRewardPresentationState.LevelRevealing;
                    return true;

                case BsHomeRewardPresentationState.LevelRevealing:
                    if (trigger
                        != BsHomeRewardPresentationTrigger.LevelAdvanceCompleted)
                        return false;
                    next = BsHomeRewardPresentationState.Scattering;
                    return true;

                case BsHomeRewardPresentationState.Scattering:
                    if (trigger != BsHomeRewardPresentationTrigger.BeginCollect)
                        return false;
                    next = BsHomeRewardPresentationState.Collecting;
                    return true;

                case BsHomeRewardPresentationState.Collecting:
                    if (trigger != BsHomeRewardPresentationTrigger.BeginSettle)
                        return false;
                    next = BsHomeRewardPresentationState.Settling;
                    return true;

                case BsHomeRewardPresentationState.Settling:
                    if (trigger
                        != BsHomeRewardPresentationTrigger.PresentationCompleted)
                        return false;
                    next = BsHomeRewardPresentationState.Idle;
                    return true;

                default:
                    return false;
            }
        }
    }
}
