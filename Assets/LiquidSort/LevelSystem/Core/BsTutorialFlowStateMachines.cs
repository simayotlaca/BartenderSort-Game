namespace BartenderSort.Core
{
    public enum BsFirstShiftTutorialState
    {
        Dormant,
        Preparing,
        SelectSource,
        PourToShot,
        ServePresentation,
        ServeShot,
        SecondOrderIntro,
        FreePlay,
        ThirdOrderIntro,
        Complete,
        Transition,
        Aborting,
        Finished,
        Disposed,
    }

    /// <summary>Tracks First Shift flow; the director owns coroutines and visual effects.</summary>
    public sealed class BsFirstShiftTutorialStateMachine
    {
        public BsFirstShiftTutorialState State { get; private set; }
        public bool HasBegun => State != BsFirstShiftTutorialState.Dormant
            && State != BsFirstShiftTutorialState.Disposed;
        public bool IsAborting => State == BsFirstShiftTutorialState.Aborting;

        public void Reset()
        {
            State = BsFirstShiftTutorialState.Dormant;
        }

        public bool Begin() => Move(BsFirstShiftTutorialState.Dormant,
            BsFirstShiftTutorialState.Preparing);
        public bool StartGuidance() => Move(BsFirstShiftTutorialState.Preparing,
            BsFirstShiftTutorialState.SelectSource);
        public bool SourceSelected() => Move(BsFirstShiftTutorialState.SelectSource,
            BsFirstShiftTutorialState.PourToShot);
        public bool SourceDeselected() => Move(BsFirstShiftTutorialState.PourToShot,
            BsFirstShiftTutorialState.SelectSource);
        public bool PourCommitted() => Move(BsFirstShiftTutorialState.PourToShot,
            BsFirstShiftTutorialState.ServePresentation);
        public bool ServePresentationReady() => Move(BsFirstShiftTutorialState.ServePresentation,
            BsFirstShiftTutorialState.ServeShot);
        public bool FirstOrderDelivered() => Move(BsFirstShiftTutorialState.ServeShot,
            BsFirstShiftTutorialState.SecondOrderIntro);
        public bool SecondOrderDelivered() => Move(BsFirstShiftTutorialState.FreePlay,
            BsFirstShiftTutorialState.ThirdOrderIntro);
        public bool ThirdOrderDelivered() => Move(BsFirstShiftTutorialState.FreePlay,
            BsFirstShiftTutorialState.Complete);
        public bool TransitionCompleted() => Move(BsFirstShiftTutorialState.Transition,
            BsFirstShiftTutorialState.Finished);
        public bool AbortCompleted() => Move(BsFirstShiftTutorialState.Aborting,
            BsFirstShiftTutorialState.Finished);

        public bool Advance()
        {
            switch (State)
            {
                case BsFirstShiftTutorialState.SecondOrderIntro:
                case BsFirstShiftTutorialState.ThirdOrderIntro:
                    State = BsFirstShiftTutorialState.FreePlay;
                    return true;
                case BsFirstShiftTutorialState.Complete:
                    State = BsFirstShiftTutorialState.Transition;
                    return true;
                default:
                    return false;
            }
        }

        public bool Abort()
        {
            if ((int)State > (int)BsFirstShiftTutorialState.Transition) return false;
            State = BsFirstShiftTutorialState.Aborting;
            return true;
        }

        public bool Dispose()
        {
            if (State == BsFirstShiftTutorialState.Disposed) return false;
            State = BsFirstShiftTutorialState.Disposed;
            return true;
        }

        private bool Move(
            BsFirstShiftTutorialState expected,
            BsFirstShiftTutorialState next)
        {
            if (State != expected) return false;
            State = next;
            return true;
        }
    }

    public enum BsTimedOrdersTutorialState
    {
        Dormant,
        Preparing,
        Intro,
        Completing,
        Aborting,
        Finished,
        Disposed,
    }

    /// <summary>Tracks the Timed Orders introduction and its acknowledgement.</summary>
    public sealed class BsTimedOrdersTutorialStateMachine
    {
        public BsTimedOrdersTutorialState State { get; private set; }
        public bool IsAborting => State == BsTimedOrdersTutorialState.Aborting;

        public void Reset()
        {
            State = BsTimedOrdersTutorialState.Dormant;
        }

        public bool Begin() => Move(BsTimedOrdersTutorialState.Dormant,
            BsTimedOrdersTutorialState.Preparing);
        public bool PresentationReady() => Move(BsTimedOrdersTutorialState.Preparing,
            BsTimedOrdersTutorialState.Intro);
        public bool CompletionApplied() => Move(BsTimedOrdersTutorialState.Completing,
            BsTimedOrdersTutorialState.Finished);
        public bool AbortCompleted() => Move(BsTimedOrdersTutorialState.Aborting,
            BsTimedOrdersTutorialState.Finished);

        public bool Advance() => Move(BsTimedOrdersTutorialState.Intro,
            BsTimedOrdersTutorialState.Completing);

        public bool Abort()
        {
            if ((int)State > (int)BsTimedOrdersTutorialState.Completing) return false;
            State = BsTimedOrdersTutorialState.Aborting;
            return true;
        }

        public bool Dispose()
        {
            if (State == BsTimedOrdersTutorialState.Disposed) return false;
            State = BsTimedOrdersTutorialState.Disposed;
            return true;
        }

        private bool Move(
            BsTimedOrdersTutorialState expected,
            BsTimedOrdersTutorialState next)
        {
            if (State != expected) return false;
            State = next;
            return true;
        }
    }
}
