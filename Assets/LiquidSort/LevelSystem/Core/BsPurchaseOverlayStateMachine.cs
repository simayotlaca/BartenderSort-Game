namespace BartenderSort.Core
{
    /// <summary>
    /// Owns one purchase card's visual state. The presenter reports purchase results; round rules, balances and
    /// storage stay outside.
    /// </summary>
    public enum BsPurchaseOverlayState
    {
        Hidden = 0,
        Visible = 1,
        PurchasePending = 2,
    }

    public enum BsPurchaseOverlayTrigger
    {
        Show,
        BeginPurchase,
        PurchaseRejected,
        PurchaseSucceeded,
        Dismiss,
        Reset,
    }

    public sealed class BsPurchaseOverlayStateMachine
    {
        public BsPurchaseOverlayState State { get; private set; } =
            BsPurchaseOverlayState.Hidden;

        public bool Dispatch(BsPurchaseOverlayTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsPurchaseOverlayState next))
                return false;

            State = next;
            return true;
        }

        public static bool TryResolve(BsPurchaseOverlayState from,
                                      BsPurchaseOverlayTrigger trigger,
                                      out BsPurchaseOverlayState next)
        {
            next = from;
            int stateValue = (int)from;
            int triggerValue = (int)trigger;
            if (stateValue < (int)BsPurchaseOverlayState.Hidden
                || stateValue > (int)BsPurchaseOverlayState.PurchasePending
                || triggerValue < (int)BsPurchaseOverlayTrigger.Show
                || triggerValue > (int)BsPurchaseOverlayTrigger.Reset)
                return false;

            // Reset is repeat-safe lifecycle cleanup, separate from user Dismiss. The owner must resolve its
            // domain barrier first if needed.
            if (trigger == BsPurchaseOverlayTrigger.Reset)
            {
                next = BsPurchaseOverlayState.Hidden;
                return true;
            }

            switch (from)
            {
                case BsPurchaseOverlayState.Hidden:
                    if (trigger != BsPurchaseOverlayTrigger.Show) return false;
                    next = BsPurchaseOverlayState.Visible;
                    return true;

                case BsPurchaseOverlayState.Visible:
                    if (trigger == BsPurchaseOverlayTrigger.BeginPurchase)
                    {
                        next = BsPurchaseOverlayState.PurchasePending;
                        return true;
                    }
                    if (trigger == BsPurchaseOverlayTrigger.Dismiss)
                    {
                        next = BsPurchaseOverlayState.Hidden;
                        return true;
                    }
                    return false;

                case BsPurchaseOverlayState.PurchasePending:
                    if (trigger == BsPurchaseOverlayTrigger.PurchaseRejected)
                    {
                        next = BsPurchaseOverlayState.Visible;
                        return true;
                    }
                    if (trigger == BsPurchaseOverlayTrigger.PurchaseSucceeded)
                    {
                        next = BsPurchaseOverlayState.Hidden;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }
    }
}
