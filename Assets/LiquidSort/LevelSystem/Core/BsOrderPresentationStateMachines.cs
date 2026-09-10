namespace BartenderSort.Core
{
    /// <summary>
    /// Controls order-card snapshot, stamp and queue timing separately from gameplay. It uses no Unity or tween
    /// types.
    /// </summary>
    internal enum BsOrderStripState
    {
        Detached = 0,
        Hidden = 1,
        Dealing = 2,
        Ready = 3,
        StampHold = 4,
        QueueAnimating = 5,
        Faulted = 6,
    }

    internal enum BsOrderStripTrigger
    {
        Attach,
        Detach,
        LevelLoaded,
        LevelDeactivated,
        ActivateLiveLevel,
        BeginDeal,
        DealCompleted,
        DeliveryCommitted,
        StampHoldElapsed,
        QueueCompleted,
        BindingRejected,
    }

    internal sealed class BsOrderStripStateMachine
    {
        public BsOrderStripState State { get; private set; } =
            BsOrderStripState.Detached;

        public bool TransitionPlaying =>
            State == BsOrderStripState.Dealing
            || State == BsOrderStripState.StampHold
            || State == BsOrderStripState.QueueAnimating
            || State == BsOrderStripState.Faulted;

        public bool Dispatch(BsOrderStripTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsOrderStripState next)) return false;
            State = next;
            return true;
        }

        internal static bool TryResolve(BsOrderStripState from,
                                        BsOrderStripTrigger trigger,
                                        out BsOrderStripState next)
        {
            next = from;
            int stateValue = (int)from;
            int triggerValue = (int)trigger;
            if (stateValue < (int)BsOrderStripState.Detached
                || stateValue > (int)BsOrderStripState.Faulted
                || triggerValue < (int)BsOrderStripTrigger.Attach
                || triggerValue > (int)BsOrderStripTrigger.BindingRejected)
                return false;

            switch (trigger)
            {
                case BsOrderStripTrigger.Attach:
                    if (from != BsOrderStripState.Detached) return false;
                    next = BsOrderStripState.Hidden;
                    return true;

                case BsOrderStripTrigger.Detach:
                    next = BsOrderStripState.Detached;
                    return true;

                case BsOrderStripTrigger.LevelLoaded:
                case BsOrderStripTrigger.LevelDeactivated:
                    if (from == BsOrderStripState.Detached) return false;
                    next = BsOrderStripState.Hidden;
                    return true;

                case BsOrderStripTrigger.ActivateLiveLevel:
                    if (from != BsOrderStripState.Hidden) return false;
                    next = BsOrderStripState.Ready;
                    return true;

                case BsOrderStripTrigger.BeginDeal:
                    if (from != BsOrderStripState.Hidden
                        && from != BsOrderStripState.Ready)
                        return false;
                    next = BsOrderStripState.Dealing;
                    return true;

                case BsOrderStripTrigger.DealCompleted:
                    if (from != BsOrderStripState.Dealing) return false;
                    next = BsOrderStripState.Ready;
                    return true;

                case BsOrderStripTrigger.DeliveryCommitted:
                    // Accept delivery while Ready or Dealing so a late presentation barrier cannot drop a
                    // valid delivery.
                    if (from != BsOrderStripState.Ready
                        && from != BsOrderStripState.Dealing)
                        return false;
                    next = BsOrderStripState.StampHold;
                    return true;

                case BsOrderStripTrigger.StampHoldElapsed:
                    if (from != BsOrderStripState.StampHold) return false;
                    next = BsOrderStripState.QueueAnimating;
                    return true;

                case BsOrderStripTrigger.QueueCompleted:
                    if (from != BsOrderStripState.QueueAnimating) return false;
                    next = BsOrderStripState.Ready;
                    return true;

                case BsOrderStripTrigger.BindingRejected:
                    if (from == BsOrderStripState.Detached) return false;
                    next = BsOrderStripState.Faulted;
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Owns one card's visibility and pose state. Canvas alpha and transforms follow this state instead of
    /// separate flags.
    /// </summary>
    internal enum BsOrderCardState
    {
        Uninitialized = 0,
        Hidden = 1,
        Dealing = 2,
        Visible = 3,
        Shifting = 4,
        Exiting = 5,
        Disabled = 6,
    }

    internal enum BsOrderCardTrigger
    {
        InitializeHidden,
        ShowImmediate,
        HideImmediate,
        BeginDeal,
        BeginShift,
        BeginExit,
        AnimationCompleted,
        ResetVisible,
        ResetHidden,
        Disable,
    }

    internal sealed class BsOrderCardStateMachine
    {
        public BsOrderCardState State { get; private set; } =
            BsOrderCardState.Uninitialized;

#if UNITY_EDITOR
        internal bool IsAnimating => State == BsOrderCardState.Dealing
                                   || State == BsOrderCardState.Shifting
                                   || State == BsOrderCardState.Exiting;
#endif

        public bool Dispatch(BsOrderCardTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsOrderCardState next)) return false;
            State = next;
            return true;
        }

        internal static bool TryResolve(BsOrderCardState from,
                                        BsOrderCardTrigger trigger,
                                        out BsOrderCardState next)
        {
            next = from;
            int stateValue = (int)from;
            int triggerValue = (int)trigger;
            if (stateValue < (int)BsOrderCardState.Uninitialized
                || stateValue > (int)BsOrderCardState.Disabled
                || triggerValue < (int)BsOrderCardTrigger.InitializeHidden
                || triggerValue > (int)BsOrderCardTrigger.Disable)
                return false;

            switch (trigger)
            {
                case BsOrderCardTrigger.InitializeHidden:
                case BsOrderCardTrigger.HideImmediate:
                case BsOrderCardTrigger.ResetHidden:
                    next = BsOrderCardState.Hidden;
                    return true;

                case BsOrderCardTrigger.ShowImmediate:
                case BsOrderCardTrigger.ResetVisible:
                    next = BsOrderCardState.Visible;
                    return true;

                case BsOrderCardTrigger.BeginDeal:
                    next = BsOrderCardState.Dealing;
                    return true;

                case BsOrderCardTrigger.BeginShift:
                    if (from != BsOrderCardState.Visible) return false;
                    next = BsOrderCardState.Shifting;
                    return true;

                case BsOrderCardTrigger.BeginExit:
                    if (from != BsOrderCardState.Visible
                        && from != BsOrderCardState.Shifting
                        && from != BsOrderCardState.Dealing)
                        return false;
                    next = BsOrderCardState.Exiting;
                    return true;

                case BsOrderCardTrigger.AnimationCompleted:
                    if (from == BsOrderCardState.Dealing
                        || from == BsOrderCardState.Shifting)
                    {
                        next = BsOrderCardState.Visible;
                        return true;
                    }
                    if (from == BsOrderCardState.Exiting)
                    {
                        next = BsOrderCardState.Hidden;
                        return true;
                    }
                    return false;

                case BsOrderCardTrigger.Disable:
                    next = BsOrderCardState.Disabled;
                    return true;

                default:
                    return false;
            }
        }
    }
}
