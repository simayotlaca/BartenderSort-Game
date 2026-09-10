using System;

namespace BartenderSort.Core
{
    /// <summary>
    /// Owns the flow from showing a result to carrying out its exit intent. The host supplies validated facts;
    /// Unity and storage stay outside.
    /// </summary>
    internal enum BsTerminalFlowState
    {
        Idle = 0,
        WaitingForGate = 1,
        Presented = 2,
        IntentQueued = 3,
        Executing = 4,
    }

    internal enum BsTerminalFlowRoute
    {
        None = 0,
        Ordinary = 1,
        DirectReturn = 2,
    }

    internal enum BsTerminalIntentKind
    {
        None = 0,
        ContinueAfterWin = 1,
        RetryAfterFailure = 2,
        PaidRetryAfterFailure = 3,
        ReturnToMainMenu = 4,
    }

    /// <summary>One-time action when the gate opens. None means it is not ready or the call is invalid.</summary>
    internal enum BsTerminalGateAction
    {
        None = 0,
        PublishTerminalReady = 1,
        DirectReturnQueued = 2,
    }

    /// <summary>Identifies one running terminal intent. OperationId always increases, even across Reset.</summary>
    internal readonly struct BsTerminalExecutionReceipt :
        IEquatable<BsTerminalExecutionReceipt>
    {
        public long OperationId { get; }
        public BsRoundToken Token { get; }
        public BsRoundOutcome Outcome { get; }
        public BsTerminalIntentKind Intent { get; }
        public bool IsValid => OperationId > 0;

        internal BsTerminalExecutionReceipt(
            long operationId,
            BsRoundToken token,
            BsRoundOutcome outcome,
            BsTerminalIntentKind intent)
        {
            OperationId = operationId;
            Token = token;
            Outcome = outcome;
            Intent = intent;
        }

        public bool Equals(BsTerminalExecutionReceipt other) =>
            OperationId == other.OperationId
            && Token == other.Token
            && Outcome == other.Outcome
            && Intent == other.Intent;

        public override bool Equals(object obj) =>
            obj is BsTerminalExecutionReceipt other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)(OperationId ^ (OperationId >> 32));
                hash = (hash * 397) ^ Token.GetHashCode();
                hash = (hash * 397) ^ (int)Outcome;
                return (hash * 397) ^ (int)Intent;
            }
        }

        public static bool operator ==(
            BsTerminalExecutionReceipt left,
            BsTerminalExecutionReceipt right) => left.Equals(right);

        public static bool operator !=(
            BsTerminalExecutionReceipt left,
            BsTerminalExecutionReceipt right) => !left.Equals(right);

        public override string ToString() =>
            $"Operation {OperationId}: {Outcome} / {Intent} / {Token}";
    }

    internal sealed class BsTerminalFlowStateMachine
    {
        BsTerminalFlowState _state;
        BsTerminalFlowRoute _route;
        BsRoundOutcome _outcome;
        BsRoundToken _token;
        BsTerminalIntentKind _queuedIntent;
        BsTerminalExecutionReceipt _activeReceipt;
        int _enteredFrame = -1;
        int _queuedFrame = -1;
        int _executionFrame = -1;
        long _queuedOperationId;
        long _lastOperationId;

        public BsTerminalFlowState State => _state;
        public BsTerminalFlowRoute Route => _route;
        public BsRoundOutcome Outcome => _outcome;
        public BsRoundToken Token => _token;
        public int EnteredFrame => _enteredFrame;
        public int QueuedFrame => _queuedFrame;
        public long QueuedOperationId => _queuedOperationId;
        public BsTerminalIntentKind QueuedIntent => _queuedIntent;

        /// <summary>Starts the normal result flow with its outcome, token and entry frame together.</summary>
        public bool TryArmTerminal(
            BsRoundOutcome outcome,
            BsRoundToken token,
            int enteredFrame)
        {
            if (!IsKnownOutcome(outcome) || enteredFrame < 0)
                return false;

            if (_state != BsTerminalFlowState.Idle)
                return false;

            return ArmTerminal(
                BsTerminalFlowRoute.Ordinary, outcome, token, enteredFrame);
        }

        /// <summary>
        /// Starts a direct return from the owner's validated failure. Wins must use the normal route.
        /// </summary>
        public bool TryArmDirectTerminal(
            BsRoundOutcome outcome,
            BsRoundToken terminalToken,
            int enteredFrame)
        {
            if (_state != BsTerminalFlowState.Idle
                || outcome != BsRoundOutcome.Failed
                || enteredFrame < 0)
            {
                return false;
            }

            return ArmTerminal(
                BsTerminalFlowRoute.DirectReturn,
                outcome,
                terminalToken,
                enteredFrame);
        }

        /// <summary>
        /// Opens after one frame when both owner barriers are ready. Temporary blocking keeps the state intact.
        /// </summary>
        public bool TryOpenGate(
            int currentFrame,
            bool terminalStillCurrent,
            bool presentationReady,
            out BsTerminalGateAction action)
        {
            action = BsTerminalGateAction.None;
            if (_state != BsTerminalFlowState.WaitingForGate
                || currentFrame < 0)
            {
                return false;
            }

            if (!terminalStillCurrent)
            {
                ClearFlow();
                return false;
            }

            if (currentFrame <= _enteredFrame || !presentationReady)
                return false;

            if (_route == BsTerminalFlowRoute.Ordinary)
            {
                _state = BsTerminalFlowState.Presented;
                action = BsTerminalGateAction.PublishTerminalReady;
                return true;
            }

            if (_route == BsTerminalFlowRoute.DirectReturn)
            {
                if (!TryReserveOperation()) return false;
                _queuedIntent = BsTerminalIntentKind.ReturnToMainMenu;
                _queuedFrame = currentFrame;
                _state = BsTerminalFlowState.IntentQueued;
                action = BsTerminalGateAction.DirectReturnQueued;
                return true;
            }

            return false;
        }

        public bool TryQueueContinueAfterWin(
            BsRoundToken expectedToken,
            int queuedFrame) =>
            TryQueuePresentedIntent(
                BsTerminalIntentKind.ContinueAfterWin,
                BsRoundOutcome.Won,
                expectedToken,
                queuedFrame);

        public bool TryQueueRetryAfterFailure(
            BsRoundToken expectedToken,
            int queuedFrame) =>
            TryQueuePresentedIntent(
                BsTerminalIntentKind.RetryAfterFailure,
                BsRoundOutcome.Failed,
                expectedToken,
                queuedFrame);

        public bool TryQueuePaidRetryAfterFailure(
            BsRoundToken expectedToken,
            int queuedFrame) =>
            TryQueuePresentedIntent(
                BsTerminalIntentKind.PaidRetryAfterFailure,
                BsRoundOutcome.Failed,
                expectedToken,
                queuedFrame);

        public bool TryQueueReturnToMainMenu(
            BsRoundToken expectedToken,
            int queuedFrame)
        {
            if (!IsKnownOutcome(_outcome))
                return false;

            return TryQueuePresentedIntent(
                BsTerminalIntentKind.ReturnToMainMenu,
                _outcome,
                expectedToken,
                queuedFrame);
        }

        /// <summary>
        /// Turns the queued intent into one receipt on the next frame. Closed barriers leave it queued.
        /// </summary>
        public bool TryBeginExecution(
            int currentFrame,
            bool terminalStillCurrent,
            bool presentationReady,
            out BsTerminalExecutionReceipt receipt)
        {
            receipt = default;
            if (_state != BsTerminalFlowState.IntentQueued
                || currentFrame < 0
                || _queuedOperationId <= 0L)
            {
                return false;
            }

            if (!terminalStillCurrent)
            {
                ClearFlow();
                return false;
            }

            if (currentFrame <= _queuedFrame
                || !presentationReady)
            {
                return false;
            }

            receipt = new BsTerminalExecutionReceipt(
                _queuedOperationId,
                _token,
                _outcome,
                _queuedIntent);

            _activeReceipt = receipt;
            _executionFrame = currentFrame;
            _state = BsTerminalFlowState.Executing;
            return true;
        }

        /// <summary>Only the current running receipt can finish the flow. Ignore old or completed receipts.</summary>
        public bool TryCompleteExecution(BsTerminalExecutionReceipt receipt)
        {
            if (!MatchesActiveReceipt(receipt))
                return false;

            ClearFlow();
            return true;
        }

        /// <summary>
        /// Checks ownership before async callbacks act. Reset, rejection or another receipt invalidates it.
        /// </summary>
        public bool IsExecutionActive(BsTerminalExecutionReceipt receipt) =>
            MatchesActiveReceipt(receipt);

        /// <summary>
        /// Normal execution failure keeps the panel open. Direct-return failure falls back once, showing
        /// failure next frame when barriers are ready.
        /// </summary>
        public bool TryRejectExecution(
            BsTerminalExecutionReceipt receipt,
            int currentFrame)
        {
            if (!MatchesActiveReceipt(receipt)
                || currentFrame < _executionFrame)
            {
                return false;
            }

            _queuedIntent = BsTerminalIntentKind.None;
            _queuedFrame = -1;
            _queuedOperationId = 0L;
            _executionFrame = -1;
            _activeReceipt = default;

            if (_route == BsTerminalFlowRoute.DirectReturn)
            {
                _route = BsTerminalFlowRoute.Ordinary;
                _enteredFrame = currentFrame;
                _state = BsTerminalFlowState.WaitingForGate;
                return true;
            }

            if (_route == BsTerminalFlowRoute.Ordinary)
            {
                _state = BsTerminalFlowState.Presented;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Clears the terminal context on disable, new round or cancellation. Keep the operation counter so old
        /// receipts stay invalid.
        /// </summary>
        public void Reset()
        {
            ClearFlow();
        }

        bool TryQueuePresentedIntent(
            BsTerminalIntentKind intent,
            BsRoundOutcome requiredOutcome,
            BsRoundToken expectedToken,
            int queuedFrame)
        {
            if (_state != BsTerminalFlowState.Presented
                || !IsKnownIntent(intent)
                || _outcome != requiredOutcome
                || _token != expectedToken
                || queuedFrame < 0
                || queuedFrame <= _enteredFrame
                || !TryReserveOperation())
            {
                return false;
            }

            _queuedIntent = intent;
            _queuedFrame = queuedFrame;
            _activeReceipt = default;
            _state = BsTerminalFlowState.IntentQueued;
            return true;
        }

        bool MatchesActiveReceipt(BsTerminalExecutionReceipt receipt) =>
            _state == BsTerminalFlowState.Executing
            && receipt.IsValid
            && receipt == _activeReceipt;

        bool ArmTerminal(
            BsTerminalFlowRoute route,
            BsRoundOutcome outcome,
            BsRoundToken token,
            int enteredFrame)
        {
            _outcome = outcome;
            _token = token;
            _enteredFrame = enteredFrame;
            _queuedFrame = -1;
            _queuedOperationId = 0L;
            _executionFrame = -1;
            _queuedIntent = BsTerminalIntentKind.None;
            _activeReceipt = default;
            _route = route;
            _state = BsTerminalFlowState.WaitingForGate;
            return true;
        }

        void ClearFlow()
        {
            _state = BsTerminalFlowState.Idle;
            _route = BsTerminalFlowRoute.None;
            _outcome = default;
            _token = default;
            _enteredFrame = -1;
            _queuedFrame = -1;
            _queuedOperationId = 0L;
            _executionFrame = -1;
            _queuedIntent = BsTerminalIntentKind.None;
            _activeReceipt = default;
        }

        bool TryReserveOperation()
        {
            if (_lastOperationId == long.MaxValue) return false;
            _queuedOperationId = ++_lastOperationId;
            return true;
        }

        static bool IsKnownOutcome(BsRoundOutcome outcome) =>
            outcome == BsRoundOutcome.Won || outcome == BsRoundOutcome.Failed;

        static bool IsKnownIntent(BsTerminalIntentKind intent) =>
            intent == BsTerminalIntentKind.ContinueAfterWin
            || intent == BsTerminalIntentKind.RetryAfterFailure
            || intent == BsTerminalIntentKind.PaidRetryAfterFailure
            || intent == BsTerminalIntentKind.ReturnToMainMenu;
    }
}
