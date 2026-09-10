using System;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Routes user navigation after the matching terminal animation finishes. The controller owns rounds and
    /// saves; callbacks must match the full attempt, operation, revision, cause and token receipt.
    /// </summary>
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
        private AsyncOperation terminalSceneLoad;
        private BartenderHomeSceneTransition homeSceneTransition;
        private BartenderTerminalCommandReceipt homeSceneTransitionReceipt;
        private long stagedHomeRewardRevision;
        private bool terminalControllerCommandInProgress;
        private bool terminalNavigationFailureLogged;

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

        /// <summary>
        /// Restores a temporarily disabled result view while its original command is still pending.
        /// This is a read-only snapshot: it neither queues a new command nor republishes readiness.
        /// Direct menu returns deliberately have no result card to restore.
        /// </summary>
        public bool TryGetPendingTerminalCommand(
            out BartenderTerminalPresentationReceipt presentation,
            out BartenderTerminalCommandReceipt command)
        {
            if (isActiveAndEnabled
                && terminalFlow.Route == BsTerminalFlowRoute.Ordinary
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

        /// <summary>
        /// Keep this exact receipt after a request succeeds and until it finishes. A round token alone
        /// cannot identify a navigation attempt.
        /// </summary>
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

        /// <summary>Fires inside each accepted transition so state changes and notifications stay together.</summary>
        public event Action<BsRoundTransition> FlowChanged;

        /// <summary>Fires once after the animation that caused the result finishes.</summary>
        public event Action<BartenderTerminalPresentationReceipt> TerminalReady;

        /// <summary>
        /// Publishes the full navigation receipt on completion so old callbacks cannot update a new result
        /// card.
        /// </summary>
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
            // Catch up to the controller even if it loaded the level before this component was added.
            SyncFromController();
        }

        private void OnDisable()
        {
            Unsubscribe();
            ResetTerminalNavigation();
            // Pending navigation survives a temporary disable. Started loads activate themselves; rejected
            // commands leave no waiting load.
            ReleaseTerminalLoadingReference();
        }

        private void LateUpdate()
        {
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
                "Authored BartenderAudioBridge Session Inspector'ında bağlı değil; "
                + "runtime bileşen oluşturulmayacak.", this);
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
                Debug.Log($"Akış: {transition.From} -> {transition.To} "
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

        /// <summary>Requests win continuation and checks the card's full terminal receipt.</summary>
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

        /// <summary>Explicit failure retry intent; stale terminal facts are rejected.</summary>
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

        /// <summary>
        /// Requests a paid life and retry. The controller spends coins atomically only while the token and
        /// terminal state still match.
        /// </summary>
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

        /// <summary>
        /// Queues menu return with stale-card and double-tap checks. Execution waits for the matching saved
        /// settlement and never repeats rewards or life loss.
        /// </summary>
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

        /// <summary>
        /// Declines TIME'S UP and returns to the menu after terminal barriers clear. Only an accepted
        /// TimeOfferDeclinedReturnToMenu commit creates this route.
        /// </summary>
        public bool RequestDeclineTimeOfferAndReturnToMainMenu(
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
                BsTimeOfferDeclineResult result = controller.DeclineTimeOffer(
                    expectedOfferId,
                    BsTimeOfferDeclineDisposition.ReturnToMainMenu);
                return result.DecisionAccepted;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return false;
            }
        }

        /// <summary>
        /// Retries the consumed decline's pending save through the controller. Success opens only the return
        /// route tied to that exact settlement.
        /// </summary>
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

        /// <summary>
        /// Confirmed Quit moves a saved abandon from Paused to Failed and shows its result card. Save
        /// failure keeps the round paused and confirmation open.
        /// </summary>
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
                    $"Terminal flow '{outcome}' / {transition.Token} için kurulamadı; "
                    + $"mevcut terminal durumu {terminalFlow.State}.", this);
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
                // An existing handoff owns the screen. Reject this command rather than clear a second
                // board under another presentation's snapshot.
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
                ExecuteTerminalLoadIntent(receipt, payload.CoinCost);
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

            // Unloaded reveals the embedded menu without reloading its scene.
            bool succeeded = false;
            try
            {
                succeeded = RunTerminalControllerCommand(
                    controller.TryReturnToMainMenuFromTerminal);
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            FinishTerminalExecution(receipt, succeeded);
        }

        /// <summary>Closes the result by clearing the already-settled board, then activates the menu scene.</summary>
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

        /// <summary>
        /// Win Continue loads the menu. The reward is already saved; this receipt only animates the balance
        /// change across scenes.
        /// </summary>
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
                    "Ana menü ödül sunumu bir önceki kazanmadan beri bekliyordu; bayat fiş "
                    + "temizlendi, yeni kazanma normal devam ediyor.",
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
                    // The accepted command publishes Empty immediately. Keep this execution's receipt and
                    // snapshot until the command returns.
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
                // Navigation is already accepted and rewards saved. Open the menu and skip only the failed
                // visual effect.
                Debug.LogWarning(
                    "Win kabul edildi fakat ana menü ödül sunumu stage edilemedi; "
                    + "menü kalıcı bakiye ile animasyonsuz açılacak.", this);
            }

            CompleteAcceptedExternalNavigation(receipt, commandReceipt);
        }

        private void ExecuteTerminalLoadIntent(
            BsTerminalExecutionReceipt receipt,
            int requestedCoinCost)
        {
            bool succeeded = false;
            try
            {
                if (TerminalExecutionCanCommit(receipt))
                {
                    switch (receipt.Intent)
                    {
                        case BsTerminalIntentKind.RetryAfterFailure:
                            succeeded = RunTerminalControllerCommand(
                                controller.TryRetryAfterFailure);
                            break;
                        case BsTerminalIntentKind.PaidRetryAfterFailure:
                            succeeded = RunTerminalControllerCommand(
                                () => controller.TryPaidRetryAfterFailure(
                                    requestedCoinCost));
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                succeeded = false;
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
                    // Start the self-activating scene load only after the controller accepts the command.
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
                            "Ana menü sahnesi yükleme işlemi başlatılamadı; "
                            + "exact navigation effect yeniden denenecek: "
                            + MainMenuSceneName, this);
                }
                return true;
            }

            terminalSceneLoad = sceneLoad;
            // Pause-menu and time-offer exits may have no result-frame cover. The persistent owner
            // keeps the incoming menu silent until its first ready frame as well.
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
            // Started loads activate themselves. Keep pending effects so re-enabling this Session can
            // resume limited retries.
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
            return shelfView == null
                || (!shelfView.SeatAnimationPlaying
                    && !shelfView.SynchronizationDeferred);
        }

        /// <summary>
        /// Result UI waits for visual barriers. Irreversible navigation also waits for the matching saved
        /// settlement; standalone rounds commit directly.
        /// </summary>
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

        /// <summary>Reads the controller snapshot when subscribing. Later updates arrive through RoundCommitted.</summary>
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
