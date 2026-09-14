using System;
using System.Threading.Tasks;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderSession : MonoBehaviour
    {
        private const string MainMenuSceneName = "SortingShelfShowcase";

        private readonly struct TerminalCommandPayload
        {
            public BsTerminalIntentKind Intent { get; }
            public int CoinCost { get; }
            public Vector2 RewardSourceViewportPoint { get; }

            public TerminalCommandPayload(
                BsTerminalIntentKind intent,
                int coinCost,
                Vector2 rewardSourceViewportPoint)
            {
                Intent = intent;
                CoinCost = coinCost;
                RewardSourceViewportPoint = rewardSourceViewportPoint;
            }
        }

        [Header("Level source")]
        [Tooltip("Uses a component on this object if empty.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Waits for the result presentation. Uses this object if empty.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;
        [Tooltip("Waits for glass seating. Uses this object if empty.")]
        [SerializeField] private BartenderShelfLevelView shelfView;

        [Header("Authored presentation")]
        [Tooltip("Authored event-to-audio bridge on this gameplay rig.")]
        [SerializeField] private BartenderAudioBridge audioBridge;

        [Tooltip("Log accepted flow changes for debugging.")]
        [SerializeField] private bool logTransitions = false;

        private readonly BsTerminalFlowStateMachine terminalFlow =
            new BsTerminalFlowStateMachine();
        private readonly BartenderTerminalNavigationStateMachine
            mainMenuNavigation = new BartenderTerminalNavigationStateMachine();

        private BartenderLevelController subscribedController;
        private BartenderTerminalPresentationReceipt activeTerminalPresentation;
        private TerminalCommandPayload? queuedTerminalPayload;
        private Task<bool> queuedTerminalSettlement;
        private BartenderTerminalCommandReceipt queuedTerminalSettlementReceipt;
        private AsyncOperation terminalSceneLoad;
        private BartenderHomeSceneTransition homeSceneTransition;
        private BartenderTerminalCommandReceipt homeSceneTransitionReceipt;
        private long stagedHomeRewardRevision;
        private bool terminalControllerCommandInProgress;
        private Task terminalLoadExecution;
        private bool terminalNavigationFailureLogged;

        private bool TerminalLoadPending => terminalLoadExecution != null
                                            && !terminalLoadExecution.IsCompleted;

        private bool HasCurrentQueuedTerminalCommand =>
            terminalFlow.State == BsTerminalFlowState.IntentQueued
            && terminalFlow.Route == BsTerminalFlowRoute.Ordinary
            && TerminalFlowStillCurrent();

        private bool MainMenuNavigationActive =>
            mainMenuNavigation.Active || terminalSceneLoad != null;

        public BartenderLevelController Controller => controller;

        public BsFlowState State => controller == null
            ? BsFlowState.Menu
            : ProjectState(
                controller.CurrentRoundState,
                controller.CurrentRoundCompletion);

        public bool AcceptsInput => State == BsFlowState.Playing;
        public bool CanContinueAfterWin => CanRequestTerminal(BsRoundOutcome.Won);
        public bool CanRetryAfterFailure => CanRequestTerminal(BsRoundOutcome.Failed);

        public bool IsTerminalPresentationCurrent(
            BartenderTerminalPresentationReceipt receipt) =>
            TerminalPresentationsMatch(activeTerminalPresentation, receipt)
            && TerminalFlowStillCurrent();

        public bool TryGetCurrentTerminalPresentation(
            out BartenderTerminalPresentationReceipt receipt)
        {
            if (terminalFlow.State == BsTerminalFlowState.Presented
                && TerminalFlowStillCurrent())
            {
                receipt = activeTerminalPresentation;
                return true;
            }

            receipt = default;
            return false;
        }

        public bool TryGetPendingTerminalCommand(
            out BartenderTerminalPresentationReceipt presentation,
            out BartenderTerminalCommandReceipt command)
        {
            if (terminalFlow.Route == BsTerminalFlowRoute.Ordinary
                && (terminalFlow.State == BsTerminalFlowState.IntentQueued
                    || terminalFlow.State == BsTerminalFlowState.Executing)
                && terminalFlow.QueuedOperationId > 0L
                && TerminalFlowStillCurrent())
            {
                presentation = activeTerminalPresentation;
                command = new BartenderTerminalCommandReceipt(
                    terminalFlow.QueuedOperationId, presentation);
                return true;
            }

            presentation = default;
            command = default;
            return false;
        }

        public bool TryGetQueuedTerminalCommandReceipt(
            BartenderTerminalPresentationReceipt expectedPresentation,
            out BartenderTerminalCommandReceipt receipt)
        {
            if (terminalFlow.State == BsTerminalFlowState.IntentQueued
                && TerminalPresentationsMatch(
                    activeTerminalPresentation, expectedPresentation)
                && terminalFlow.QueuedOperationId > 0L
                && TerminalFlowStillCurrent())
            {
                receipt = new BartenderTerminalCommandReceipt(
                    terminalFlow.QueuedOperationId,
                    activeTerminalPresentation);
                return true;
            }

            receipt = default;
            return false;
        }

        public event Action<BsRoundTransition> FlowChanged;

        public event Action<BartenderTerminalPresentationReceipt> TerminalReady;

        public event Action<BartenderTerminalCommandCompletion> TerminalCommandCompleted;

        private void Awake()
        {
            ResolveDependencies();
            ReportMissingAudioBridge();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            if (!TerminalLoadPending && !HasCurrentQueuedTerminalCommand) SyncFromController();
        }

        private void OnDisable()
        {
            Unsubscribe();
            if (!TerminalLoadPending)
            {
                if (HasCurrentQueuedTerminalCommand) ReleaseUnacceptedHomeSceneTransition();
                else ResetTerminalNavigation();
            }
            ReleaseTerminalLoadingReference();
        }

        private void LateUpdate()
        {
            if (TerminalLoadPending) return;
            if (AdvanceMainMenuNavigation()) return;
            // Opening the gate and executing are separate steps. Even an immediate callback must wait until
            // the next frame.
            if (AdvanceTerminalGate()) return;
            TryBeginTerminalExecution();
        }

        private void ResolveDependencies()
        {
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (pourInteraction == null)
                pourInteraction = GetComponent<BartenderPourInteraction>();
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (audioBridge == null) audioBridge = GetComponent<BartenderAudioBridge>();
        }

        private void ReportMissingAudioBridge()
        {
            if (audioBridge != null) return;
            Debug.LogError(
                "Audio bridge missing.", this);
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.RoundCommitted += HandleRoundCommitted;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.RoundCommitted -= HandleRoundCommitted;
            }
            subscribedController = null;
        }

        private void HandleRoundCommitted(BsRoundCommit commit)
        {
            BsRoundTransition transition = commit?.Transition;
            if (transition == null) return;

            if (logTransitions)
                Debug.Log($"Flow: {transition.From} -> {transition.To} "
                        + $"({transition.Cause}), {transition.Token}.", this);

            if (transition.To == BsRoundState.Completed)
            {
                ArmTerminalNavigation(transition);
            }
            else if (!terminalControllerCommandInProgress)
            {
                ResetTerminalNavigation();
            }

            InvokeSafely(FlowChanged, transition);
        }

        public bool RequestContinueAfterWin()
        {
            return TryGetCurrentTerminalPresentation(out var presentation)
                   && RequestContinueAfterWin(
                       presentation, new Vector2(0.5f, 0.5f));
        }

        public bool RequestContinueAfterWin(
            BartenderTerminalPresentationReceipt expectedPresentation,
            Vector2 rewardSourceViewportPoint)
        {
            if (!CanRequestTerminal(
                    BsRoundOutcome.Won, expectedPresentation)
                || !terminalFlow.TryQueueContinueAfterWin(
                    expectedPresentation.Token, Time.frameCount))
                return false;

            queuedTerminalPayload = new TerminalCommandPayload(
                BsTerminalIntentKind.ContinueAfterWin,
                0,
                SanitizeViewportPoint(rewardSourceViewportPoint));
            return true;
        }

        public bool RequestRetryAfterFailure()
        {
            return TryGetCurrentTerminalPresentation(out var presentation)
                   && RequestRetryAfterFailure(presentation);
        }

        public bool RequestRetryAfterFailure(
            BartenderTerminalPresentationReceipt expectedPresentation)
        {
            if (!CanRequestTerminal(
                    BsRoundOutcome.Failed, expectedPresentation)
                || !terminalFlow.TryQueueRetryAfterFailure(
                    expectedPresentation.Token, Time.frameCount))
                return false;

            queuedTerminalPayload = new TerminalCommandPayload(
                BsTerminalIntentKind.RetryAfterFailure,
                0,
                new Vector2(0.5f, 0.5f));
            return true;
        }

        public bool RequestPaidRetryAfterFailure(
            BartenderTerminalPresentationReceipt expectedPresentation,
            int coinCost)
        {
            if (coinCost <= 0
                || BartenderProgressService.Lives >= BartenderProgressService.MaxLives
                || !BartenderProgressService.CanAfford(coinCost)
                || !CanRequestTerminal(
                    BsRoundOutcome.Failed, expectedPresentation)
                || !terminalFlow.TryQueuePaidRetryAfterFailure(
                    expectedPresentation.Token, Time.frameCount))
                return false;

            queuedTerminalPayload = new TerminalCommandPayload(
                BsTerminalIntentKind.PaidRetryAfterFailure,
                coinCost,
                new Vector2(0.5f, 0.5f));
            return true;
        }

        public bool RequestReturnToMainMenuFromTerminal()
        {
            return TryGetCurrentTerminalPresentation(out var presentation)
                   && RequestReturnToMainMenuFromTerminal(presentation);
        }

        public bool RequestReturnToMainMenuFromTerminal(
            BartenderTerminalPresentationReceipt expectedPresentation)
        {
            if (terminalFlow.State != BsTerminalFlowState.Presented
                || !CanRequestTerminal(
                    terminalFlow.Outcome, expectedPresentation)
                || !terminalFlow.TryQueueReturnToMainMenu(
                    expectedPresentation.Token, Time.frameCount))
                return false;

            queuedTerminalPayload = new TerminalCommandPayload(
                BsTerminalIntentKind.ReturnToMainMenu,
                0,
                new Vector2(0.5f, 0.5f));
            return true;
        }

        public async Task<bool> RequestDeclineTimeOfferAndPresentFailureAsync(
            BsTimeOfferId expectedOfferId)
        {
            if (controller == null
                || !expectedOfferId.IsValid
                || State != BsFlowState.Playing
                || MainMenuNavigationActive
                || terminalFlow.State != BsTerminalFlowState.Idle)
                return false;

            try
            {
                BsTimeOfferDeclineResult result = await controller.DeclineTimeOfferAsync(
                    expectedOfferId, BsTimeOfferDeclineDisposition.PresentFailure);
                return result.DecisionAccepted;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return false;
            }
        }

        public bool RequestRetryDeclinedTimeOfferAndReturnToMainMenu(
            BsTimeOfferId expectedOfferId)
        {
            if (controller == null || !expectedOfferId.IsValid)
                return false;

            try
            {
                return controller.RetryTimeOfferSettlement(expectedOfferId).Accepted;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return false;
            }
        }

        public bool RequestQuitToFailureFromPause()
        {
            if (controller == null || State != BsFlowState.Paused
                || MainMenuNavigationActive
                || terminalFlow.State != BsTerminalFlowState.Idle)
                return false;

            return controller.TryQuitToFailure(out _);
        }

        internal static bool RequiresExternalMainMenuSceneLoad(
            string activeSceneName) =>
            !string.IsNullOrWhiteSpace(activeSceneName)
            && !string.Equals(activeSceneName, MainMenuSceneName,
                StringComparison.Ordinal);

        private static bool RequiresExternalMainMenuSceneLoad()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            return activeScene.IsValid()
                && RequiresExternalMainMenuSceneLoad(activeScene.name);
        }

        private void ArmTerminalNavigation(BsRoundTransition transition)
        {
            if (controller == null || controller.IsStandaloneRound
                || !TryProjectTerminalOutcome(transition, out BsRoundOutcome outcome)
                || !TerminalTransitionStillCurrent(transition))
                return;

            var presentation = new BartenderTerminalPresentationReceipt(
                transition, outcome);
            if (TerminalPresentationMatches(activeTerminalPresentation, transition)
                && terminalFlow.State != BsTerminalFlowState.Idle)
                return;

            if (terminalFlow.State != BsTerminalFlowState.Idle)
                ResetTerminalNavigation();

            queuedTerminalPayload = null;
            bool directReturn = outcome == BsRoundOutcome.Failed
                && transition.Cause ==
                    BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu;
            bool armed = directReturn
                ? terminalFlow.TryArmDirectTerminal(
                    outcome, transition.Token, Time.frameCount)
                : terminalFlow.TryArmTerminal(
                    outcome, transition.Token, Time.frameCount);

            if (armed)
            {
                activeTerminalPresentation = presentation;
                return;
            }

            if (logTransitions)
                Debug.LogWarning(
                    $"Terminal flow failed: {terminalFlow.State}.", this);
        }

        private bool AdvanceTerminalGate()
        {
            if (terminalFlow.State != BsTerminalFlowState.WaitingForGate)
                return false;

            bool advanced = terminalFlow.TryOpenGate(
                Time.frameCount,
                TerminalFlowStillCurrent(),
                PresentationBarrierClear(),
                out BsTerminalGateAction action);
            if (!advanced) return false;

            if (action == BsTerminalGateAction.PublishTerminalReady)
            {
                InvokeTerminalReadySafely(activeTerminalPresentation);
            }

            // Return now so DirectReturnQueued cannot execute until the next frame.
            return true;
        }

        private bool CanRequestTerminal(BsRoundOutcome outcome) =>
            CanRequestTerminal(outcome, activeTerminalPresentation);

        private bool CanRequestTerminal(
            BsRoundOutcome outcome,
            BartenderTerminalPresentationReceipt expectedPresentation)
        {
            return terminalFlow.State == BsTerminalFlowState.Presented
                && terminalFlow.Outcome == outcome
                && !MainMenuNavigationActive
                && TerminalPresentationsMatch(
                    activeTerminalPresentation, expectedPresentation)
                && TerminalFlowStillCurrent();
        }

        private bool TerminalFlowStillCurrent()
        {
            if (controller == null || !activeTerminalPresentation.IsValid
                || terminalFlow.Token != activeTerminalPresentation.Token
                || terminalFlow.Outcome != activeTerminalPresentation.Outcome
                || !controller.IsRoundCurrent(
                    activeTerminalPresentation.AttemptId,
                    activeTerminalPresentation.Token,
                    activeTerminalPresentation.Revision)
                || !controller.TryGetCurrentTerminalTransition(
                    out BsRoundTransition transition))
                return false;

            return TerminalPresentationMatches(
                activeTerminalPresentation, transition);
        }

        private bool TerminalTransitionStillCurrent(BsRoundTransition transition)
        {
            if (controller == null
                || !TryProjectTerminalOutcome(transition, out _)
                || !controller.IsRoundCurrent(
                    transition.AttemptId,
                    transition.Token,
                    transition.Revision)
                || !controller.TryGetCurrentTerminalTransition(
                    out BsRoundTransition current))
                return false;

            return TerminalTransitionsMatch(transition, current);
        }

        private static bool TerminalPresentationMatches(
            BartenderTerminalPresentationReceipt presentation,
            BsRoundTransition transition)
        {
            return presentation.IsValid
                && TryProjectTerminalOutcome(transition, out BsRoundOutcome outcome)
                && presentation.AttemptId == transition.AttemptId
                && presentation.RoundOperationId == transition.OperationId
                && presentation.Revision == transition.Revision
                && presentation.BoardRevision == transition.BoardRevision
                && presentation.Cause == transition.Cause
                && presentation.Outcome == outcome
                && presentation.Token == transition.Token;
        }

        internal static bool TerminalPresentationsMatch(
            BartenderTerminalPresentationReceipt left,
            BartenderTerminalPresentationReceipt right)
        {
            return left.IsValid && right.IsValid
                && left.AttemptId == right.AttemptId
                && left.RoundOperationId == right.RoundOperationId
                && left.Revision == right.Revision
                && left.BoardRevision == right.BoardRevision
                && left.Cause == right.Cause
                && left.Outcome == right.Outcome
                && left.Token == right.Token;
        }

        private static bool TerminalTransitionsMatch(
            BsRoundTransition left,
            BsRoundTransition right)
        {
            return left != null && right != null
                && left.AttemptId == right.AttemptId
                && left.OperationId == right.OperationId
                && left.Revision == right.Revision
                && left.BoardRevision == right.BoardRevision
                && left.From == right.From
                && left.To == right.To
                && left.Cause == right.Cause
                && left.Completion == right.Completion
                && left.Token == right.Token
                && Nullable.Equals(
                    left.SettlementReceipt, right.SettlementReceipt);
        }

        private static bool TryProjectTerminalOutcome(
            BsRoundTransition transition,
            out BsRoundOutcome outcome)
        {
            outcome = default;
            if (transition == null
                || transition.To != BsRoundState.Completed
                || !transition.AttemptId.IsValid
                || !transition.OperationId.IsValid
                || transition.Revision <= 0L
                || transition.BoardRevision < 0)
                return false;

            if (transition.Completion == BsRoundCompletion.Won)
            {
                outcome = BsRoundOutcome.Won;
                return true;
            }

            if (transition.Completion == BsRoundCompletion.Failed
                || transition.Completion == BsRoundCompletion.Quit)
            {
                outcome = BsRoundOutcome.Failed;
                return true;
            }

            return false;
        }

        private void TryBeginTerminalExecution()
        {
            if (!AdvanceQueuedTerminalSettlement()) return;

            bool needsSceneCover = terminalFlow.State == BsTerminalFlowState.IntentQueued
                && (terminalFlow.QueuedIntent == BsTerminalIntentKind.ContinueAfterWin
                    || (terminalFlow.QueuedIntent == BsTerminalIntentKind.ReturnToMainMenu
                        && RequiresExternalMainMenuSceneLoad()));
            if (needsSceneCover && TerminalFlowStillCurrent() && TerminalExecutionBarrierClear()
                && Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
            {
                if (!homeSceneTransitionReceipt.IsValid)
                {
                    homeSceneTransitionReceipt = new BartenderTerminalCommandReceipt(
                        terminalFlow.QueuedOperationId, activeTerminalPresentation);
                    homeSceneTransition = BartenderHomeSceneTransition.Begin(
                        homeSceneTransitionReceipt, MainMenuSceneName);
                }
                if (homeSceneTransition != null
                    && !homeSceneTransition.IsCovered(homeSceneTransitionReceipt)) return;
            }

            if (!terminalFlow.TryBeginExecution(
                    Time.frameCount,
                    TerminalFlowStillCurrent(),
                    TerminalExecutionBarrierClear(),
                    out BsTerminalExecutionReceipt receipt))
            {
                if (terminalFlow.State == BsTerminalFlowState.Idle)
                    ResetTerminalNavigation();
                return;
            }

            if (needsSceneCover && homeSceneTransition == null)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            TerminalCommandPayload payload = queuedTerminalPayload
                ?? new TerminalCommandPayload(
                    receipt.Intent,
                    0,
                    new Vector2(0.5f, 0.5f));
            queuedTerminalPayload = null;

            if (payload.Intent != receipt.Intent)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            if (receipt.Intent == BsTerminalIntentKind.ContinueAfterWin)
            {
                ExecuteWinContinueToMainMenu(
                    receipt, payload.RewardSourceViewportPoint);
                return;
            }

            if (receipt.Intent == BsTerminalIntentKind.RetryAfterFailure
                || receipt.Intent == BsTerminalIntentKind.PaidRetryAfterFailure)
            {
                terminalLoadExecution = ExecuteTerminalLoadIntentAsync(receipt, payload.CoinCost);
                return;
            }

            if (receipt.Intent != BsTerminalIntentKind.ReturnToMainMenu)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            if (RequiresExternalMainMenuSceneLoad())
            {
                ExecuteTerminalReturnToMainMenu(receipt);
                return;
            }

            bool succeeded = false;
            try
            {
                succeeded = RunTerminalControllerCommand(
                    controller.TryReturnToMainMenuFromTerminal);
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            FinishTerminalExecution(receipt, succeeded);
        }

        private bool AdvanceQueuedTerminalSettlement()
        {
            if (terminalFlow.State != BsTerminalFlowState.IntentQueued
                || terminalFlow.Route != BsTerminalFlowRoute.Ordinary
                || !TerminalFlowStillCurrent())
                return true;
            // The presenter must capture the reserved receipt before any completion can be published.
            if (Time.frameCount <= terminalFlow.QueuedFrame) return false;
            if (controller.IsTerminalSettlementCommitted)
            {
                queuedTerminalSettlement = null;
                queuedTerminalSettlementReceipt = default;
                return true;
            }

            var expected = new BartenderTerminalCommandReceipt(
                terminalFlow.QueuedOperationId, activeTerminalPresentation);
            bool succeeded = false;
            try
            {
                if (queuedTerminalSettlement == null || queuedTerminalSettlementReceipt != expected)
                {
                    queuedTerminalSettlementReceipt = expected;
                    queuedTerminalSettlement = controller.RetryTerminalSettlementAsync();
                }
                if (!queuedTerminalSettlement.IsCompleted) return false;
                succeeded = queuedTerminalSettlement.GetAwaiter().GetResult();
            }
            catch (Exception exception) { Debug.LogException(exception, this); }

            queuedTerminalSettlement = null;
            queuedTerminalSettlementReceipt = default;
            if (!TerminalFlowStillCurrent()
                || terminalFlow.State != BsTerminalFlowState.IntentQueued
                || new BartenderTerminalCommandReceipt(terminalFlow.QueuedOperationId,
                    activeTerminalPresentation) != expected) return false;
            if (succeeded && controller.IsTerminalSettlementCommitted) return true;
            if (!terminalFlow.TryRejectQueuedIntent(expected.OperationId, Time.frameCount))
                return false;

            queuedTerminalPayload = null;
            ReleaseUnacceptedHomeSceneTransition();
            InvokeTerminalCommandCompletedSafely(new BartenderTerminalCommandCompletion(
                expected, false, "SAVE FAILED - TAP AGAIN TO RETRY"));
            return false;
        }

        private void ExecuteTerminalReturnToMainMenu(
            BsTerminalExecutionReceipt receipt)
        {
            if (!TerminalExecutionCanCommit(receipt)
                || !Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            BartenderTerminalCommandReceipt commandReceipt =
                CaptureTerminalCommandReceipt(receipt);
            if (!commandReceipt.IsValid)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }
            bool succeeded = false;
            try
            {
                if (TerminalExecutionCanCommit(receipt))
                    succeeded = RunTerminalControllerCommand(
                        controller.TryReturnToMainMenuFromTerminal);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }

            if (!succeeded)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            CompleteAcceptedExternalNavigation(receipt, commandReceipt);
        }

        private void ExecuteWinContinueToMainMenu(
            BsTerminalExecutionReceipt receipt,
            Vector2 rewardSourceViewportPoint)
        {
            if (!TerminalExecutionCanCommit(receipt)
                || !Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            // Drop a stale visual receipt and continue so a missed menu animation cannot trap the player
            // here.
            if (BartenderPendingHomeRewardStore.DiscardStalePending())
                Debug.LogWarning(
                    "Stale home reward cleared.",
                    this);

            int rewardAmount = BartenderProgressService.WinCoinReward;
            int finalCoins = Mathf.Max(0, BartenderProgressService.Coins);
            int previousCoins = Mathf.Max(0, finalCoins - rewardAmount);
            int completedLevelNumber = controller.CurrentLevel != null
                ? controller.CurrentLevel.Index
                : controller.CurrentCampaignSlot + 1;

            BartenderTerminalCommandReceipt commandReceipt =
                CaptureTerminalCommandReceipt(receipt);
            if (!commandReceipt.IsValid)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }
            bool succeeded = false;
            if (TerminalExecutionCanCommit(receipt))
            {
                try
                {
                    succeeded = RunTerminalControllerCommand(
                        controller.TryContinueAfterWin);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }

            if (!succeeded)
            {
                FinishTerminalExecution(receipt, false);
                return;
            }

            // Keep the level that was actually won for the celebration. The menu destination can differ
            // from it even on a replay or after the final level; that must not erase the success beat.
            int nextLevelNumber = BartenderLevelController.ResolveCampaignLevelNumber(
                BartenderLevelController.ResolveNextPlayableSlot());

            // Stage the reward after Unloaded/CampaignComplete finishes publishing so old menu presenters
            // cannot claim it.
            bool rewardStaged = false;
            BartenderPendingHomeReward stagedReward = default;
            try
            {
                rewardStaged = BartenderPendingHomeRewardStore.TryStage(
                    previousCoins,
                    finalCoins,
                    rewardAmount,
                    completedLevelNumber,
                    nextLevelNumber,
                    rewardSourceViewportPoint,
                    out stagedReward);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }

            if (rewardStaged)
            {
                stagedHomeRewardRevision = stagedReward.Revision;
                // The accepted visual receipt now belongs to the destination menu. Disabling this Session
                // must not discard it.
                stagedHomeRewardRevision = 0L;
            }
            else
            {
                Debug.LogWarning(
                    "Home reward presentation failed.", this);
            }

            CompleteAcceptedExternalNavigation(receipt, commandReceipt);
        }

        private async Task ExecuteTerminalLoadIntentAsync(
            BsTerminalExecutionReceipt receipt,
            int requestedCoinCost)
        {
            bool succeeded = false;
            BartenderLevelController reservedController = controller;
            bool wasInProgress = terminalControllerCommandInProgress;
            try
            {
                if (TerminalExecutionCanCommit(receipt))
                {
                    // Keep the exact terminal execution while its durable replacement is pending.
                    // Round publication must not reset the result receipt before completion is reported.
                    terminalControllerCommandInProgress = true;
                    BartenderCommandResult<bool> result;
                    switch (receipt.Intent)
                    {
                        case BsTerminalIntentKind.RetryAfterFailure:
                            result = await reservedController.RetryAfterFailureAsync();
                            succeeded = result.Succeeded;
                            break;
                        case BsTerminalIntentKind.PaidRetryAfterFailure:
                            result = await reservedController.PaidRetryAfterFailureAsync(
                                requestedCoinCost);
                            succeeded = result.Succeeded;
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (this != null) Debug.LogException(exception, this);
                succeeded = false;
            }
            finally
            {
                terminalControllerCommandInProgress = wasInProgress;
            }

            if (this == null || !terminalFlow.IsExecutionActive(receipt)
                || !TerminalExecutionOwnsPresentation(receipt)) return;
            if (controller == null || !ReferenceEquals(controller, reservedController))
            {
                // The old task cannot finish a replacement controller's presentation. Retire only its
                // still-owned execution, otherwise Executing would block the new authority forever.
                if (isActiveAndEnabled)
                {
                    ResolveDependencies();
                    Subscribe();
                    SyncFromController();
                }
                else ResetTerminalNavigation();
                return;
            }
            if (!isActiveAndEnabled)
            {
                ResetTerminalNavigation();
                return;
            }
            FinishTerminalExecution(receipt, succeeded);
        }

        private void CompleteAcceptedExternalNavigation(
            BsTerminalExecutionReceipt executionReceipt,
            BartenderTerminalCommandReceipt commandReceipt)
        {
            bool scheduled = mainMenuNavigation.TrySchedule(
                commandReceipt, true, Time.frameCount);
            if (!scheduled)
            {
                Debug.LogError(
                    "Accepted terminal command could not reserve its exact "
                    + "main-menu navigation effect.", this);
                // Do not report navigation success unless its exact effect receipt was retained, even after
                // the controller accepts.
                FinishTerminalExecution(executionReceipt, false);
                return;
            }

            // If scene loading cannot start, keep the accepted command receipt and retry its effect without
            // reopening terminal gameplay.
            homeSceneTransition?.AcceptHandoff(commandReceipt);
            FinishTerminalExecution(executionReceipt, true);
            AdvanceMainMenuNavigation();
        }

        private bool AdvanceMainMenuNavigation()
        {
            if (!mainMenuNavigation.Active) return false;
            if (mainMenuNavigation.State ==
                BartenderTerminalNavigationState.Loading) return true;
            if (!mainMenuNavigation.CanAttempt(Time.frameCount)) return true;

            BartenderTerminalCommandReceipt commandReceipt =
                mainMenuNavigation.Receipt;
            AsyncOperation sceneLoad = null;
            Exception loadException = null;
            IDisposable musicSuspension = null;
            if (Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
            {
                try
                {
                    musicSuspension = BsAudio.SuspendBackgroundMusic();
                    sceneLoad = SceneManager.LoadSceneAsync(
                        MainMenuSceneName, LoadSceneMode.Single);
                }
                catch (Exception exception)
                {
                    loadException = exception;
                }
            }

            if (sceneLoad == null)
            {
                musicSuspension?.Dispose();
                mainMenuNavigation.TryRecordAttemptFailed(
                    commandReceipt, Time.frameCount);
                if (!terminalNavigationFailureLogged)
                {
                    terminalNavigationFailureLogged = true;
                    if (loadException != null)
                        Debug.LogException(loadException, this);
                    else
                        Debug.LogError(
                            "Main menu load failed: " + MainMenuSceneName, this);
                }
                return true;
            }

            terminalSceneLoad = sceneLoad;
            BartenderSceneMusicHandoff.TakeOver(musicSuspension, sceneLoad);
            // Accepted navigation can resume after this Session was disabled. The persistent cover binds
            // the retry by its exact command receipt even after our local presentation reference is gone.
            BartenderHomeSceneTransition.RecordSceneLoad(commandReceipt, sceneLoad);
            terminalNavigationFailureLogged = false;
            if (!mainMenuNavigation.TryRecordLoadStarted(commandReceipt))
                Debug.LogError(
                    "Main-menu scene load started without its exact navigation receipt.",
                    this);
            return true;
        }

        private BartenderTerminalCommandReceipt CaptureTerminalCommandReceipt(
            BsTerminalExecutionReceipt executionReceipt) =>
            new BartenderTerminalCommandReceipt(
                executionReceipt.OperationId,
                activeTerminalPresentation);

        private void ReleaseTerminalLoadingReference()
        {
            DiscardStagedHomeReward();
            terminalSceneLoad = null;
        }

        private void DiscardStagedHomeReward()
        {
            if (stagedHomeRewardRevision == 0L) return;
            BartenderPendingHomeRewardStore.TryDiscard(stagedHomeRewardRevision);
            stagedHomeRewardRevision = 0L;
        }

        private bool TerminalExecutionStillCurrent(
            BsTerminalExecutionReceipt receipt)
        {
            return terminalFlow.IsExecutionActive(receipt)
                && TerminalExecutionOwnsPresentation(receipt)
                && TerminalFlowStillCurrent();
        }

        private bool TerminalExecutionCanCommit(
            BsTerminalExecutionReceipt receipt) =>
            TerminalExecutionStillCurrent(receipt)
            && TerminalExecutionBarrierClear();

        private bool TerminalExecutionOwnsPresentation(
            BsTerminalExecutionReceipt receipt)
        {
            return receipt.IsValid
                && activeTerminalPresentation.IsValid
                && receipt.Token == activeTerminalPresentation.Token
                && receipt.Outcome == activeTerminalPresentation.Outcome
                && terminalFlow.Token == activeTerminalPresentation.Token
                && terminalFlow.Outcome == activeTerminalPresentation.Outcome;
        }

        private bool RunTerminalControllerCommand(Func<bool> command)
        {
            if (command == null) return false;
            bool wasInProgress = terminalControllerCommandInProgress;
            terminalControllerCommandInProgress = true;
            try { return command(); }
            finally { terminalControllerCommandInProgress = wasInProgress; }
        }

        private void FinishTerminalExecution(
            BsTerminalExecutionReceipt receipt,
            bool succeeded)
        {
            BartenderTerminalPresentationReceipt presentation =
                activeTerminalPresentation;
            if (!TerminalExecutionOwnsPresentation(receipt)) return;

            bool finalized = succeeded
                ? terminalFlow.TryCompleteExecution(receipt)
                : terminalFlow.TryRejectExecution(receipt, Time.frameCount);
            if (!finalized) return;

            if (!succeeded) ReleaseUnacceptedHomeSceneTransition();

            var commandReceipt = new BartenderTerminalCommandReceipt(
                receipt.OperationId, presentation);
            if (succeeded) activeTerminalPresentation = default;
            InvokeTerminalCommandCompletedSafely(
                new BartenderTerminalCommandCompletion(
                    commandReceipt,
                    succeeded));
        }

        private bool PresentationBarrierClear()
        {
            if (controller == null || controller.PresentationLocked) return false;
            if (pourInteraction != null && pourInteraction.Busy) return false;
            if (pourInteraction != null && pourInteraction.BoardPresentationSettling) return false;
            return shelfView == null
                || (!shelfView.SeatAnimationPlaying
                    && !shelfView.SynchronizationDeferred);
        }

        private bool TerminalExecutionBarrierClear() =>
            controller != null
            && controller.IsTerminalSettlementCommitted
            && PresentationBarrierClear();

        private void ResetTerminalNavigation()
        {
            ReleaseUnacceptedHomeSceneTransition();
            terminalFlow.Reset();
            activeTerminalPresentation = default;
            queuedTerminalPayload = null;
            queuedTerminalSettlement = null;
            queuedTerminalSettlementReceipt = default;
        }

        private void ReleaseUnacceptedHomeSceneTransition()
        {
            BartenderHomeSceneTransition transition = homeSceneTransition;
            BartenderTerminalCommandReceipt expected = homeSceneTransitionReceipt;
            homeSceneTransition = null;
            homeSceneTransitionReceipt = default;
            transition?.CancelBeforeAcceptance(expected);
        }

        private static Vector2 SanitizeViewportPoint(Vector2 point)
        {
            if (float.IsNaN(point.x) || float.IsInfinity(point.x)
                || float.IsNaN(point.y) || float.IsInfinity(point.y))
                return new Vector2(0.5f, 0.5f);
            return new Vector2(Mathf.Clamp01(point.x), Mathf.Clamp01(point.y));
        }

        private static BsFlowState ProjectState(
            BsRoundState state,
            BsRoundCompletion completion)
        {
            switch (state)
            {
                case BsRoundState.Preparing:
                    return BsFlowState.Loading;
                case BsRoundState.Playing:
                    return BsFlowState.Playing;
                case BsRoundState.Paused:
                    return BsFlowState.Paused;
                case BsRoundState.Completed:
                    return completion == BsRoundCompletion.Won
                        ? BsFlowState.Won
                        : BsFlowState.Failed;
                default:
                    return BsFlowState.Menu;
            }
        }

        private void InvokeTerminalReadySafely(
            BartenderTerminalPresentationReceipt receipt)
        {
            InvokeSafely(TerminalReady, receipt);
        }

        private void InvokeTerminalCommandCompletedSafely(
            BartenderTerminalCommandCompletion completion)
        {
            InvokeSafely(TerminalCommandCompleted, completion);
        }

        private void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            Delegate[] invocationList = handlers.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try { ((Action<T>)invocationList[i])(value); }
                catch (Exception exception) { Debug.LogException(exception, this); }
            }
        }

        private void SyncFromController()
        {
            ResetTerminalNavigation();
            if (controller != null
                && controller.TryGetCurrentTerminalTransition(
                    out BsRoundTransition transition))
                ArmTerminalNavigation(transition);
        }
    }
}
