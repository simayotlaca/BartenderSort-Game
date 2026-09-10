namespace BartenderSort.Core
{
    /// <summary>
    /// Chooses the visible pause-menu card without Unity dependencies. Gameplay stays Paused in either open
    /// state.
    /// </summary>
    public enum BsPauseOverlayState
    {
        Closed = 0,
        Settings = 1,
        ExitConfirmation = 2,
    }

    public enum BsPauseOverlayTrigger
    {
        PauseAccepted,
        ExitRequested,
        ExitCancelled,
        PauseEnded,
    }

    public sealed class BsPauseOverlayStateMachine
    {
        public BsPauseOverlayState State { get; private set; } =
            BsPauseOverlayState.Closed;

        public bool Dispatch(BsPauseOverlayTrigger trigger)
        {
            if (!TryResolve(State, trigger, out BsPauseOverlayState next))
                return false;

            State = next;
            return true;
        }

        public static bool TryResolve(BsPauseOverlayState from,
                                      BsPauseOverlayTrigger trigger,
                                      out BsPauseOverlayState next)
        {
            next = from;
            int stateValue = (int)from;
            int triggerValue = (int)trigger;
            if (stateValue < (int)BsPauseOverlayState.Closed
                || stateValue > (int)BsPauseOverlayState.ExitConfirmation
                || triggerValue < (int)BsPauseOverlayTrigger.PauseAccepted
                || triggerValue > (int)BsPauseOverlayTrigger.PauseEnded)
                return false;

            switch (trigger)
            {
                case BsPauseOverlayTrigger.PauseAccepted:
                    if (from != BsPauseOverlayState.Closed) return false;
                    next = BsPauseOverlayState.Settings;
                    return true;

                case BsPauseOverlayTrigger.ExitRequested:
                    if (from != BsPauseOverlayState.Settings) return false;
                    next = BsPauseOverlayState.ExitConfirmation;
                    return true;

                case BsPauseOverlayTrigger.ExitCancelled:
                    if (from != BsPauseOverlayState.ExitConfirmation) return false;
                    next = BsPauseOverlayState.Settings;
                    return true;

                case BsPauseOverlayTrigger.PauseEnded:
                    if (from == BsPauseOverlayState.Closed) return false;
                    next = BsPauseOverlayState.Closed;
                    return true;

                default:
                    return false;
            }
        }
    }
}
