using System;
using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>Persistent first-time completion flag for the standalone onboarding.</summary>
    public static class BartenderFirstShiftProgress
    {
        private const string TutorialId = "first_shift";
        private const int Version = 1;
#if UNITY_EDITOR
        private const string EditorTestRequestedKey =
            "GlassPourMathDemo.FirstShift.EditorTestRequested";
        private const string EditorTestSceneKey =
            "GlassPourMathDemo.FirstShift.EditorTestScene";
#endif

        /// <summary>
        /// Only new campaigns need First Shift. Older saves past the first slot must not restart onboarding
        /// because the flag is missing.
        /// </summary>
        public static bool ShouldStart =>
            BartenderProgressService.IsAvailable
            && BartenderProgressService.NextUnlockedCampaignSlot == 0
            && !BartenderTutorialProgress.IsCompleted(TutorialId, Version);

        public static void Complete()
        {
            BartenderTutorialProgress.Complete(TutorialId, Version);
        }

        public static void Reset() =>
            BartenderTutorialProgress.Reset(TutorialId, Version);

#if UNITY_EDITOR
        public static bool EditorTestRequested =>
            UnityEditor.SessionState.GetBool(EditorTestRequestedKey, false);

        public static bool EditorTryArmTestRun(string gameplaySceneName,
                                               out string rejectionReason)
        {
            string normalized = string.IsNullOrWhiteSpace(gameplaySceneName)
                ? null
                : gameplaySceneName.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                rejectionReason = "Tutorial gameplay scene is missing.";
                return false;
            }

            UnityEditor.SessionState.SetBool(EditorTestRequestedKey, true);
            UnityEditor.SessionState.SetString(EditorTestSceneKey, normalized);
            rejectionReason = null;
            return true;
        }

        public static bool EditorTryConsumeTestScene(out string gameplaySceneName)
        {
            gameplaySceneName = UnityEditor.SessionState.GetString(
                EditorTestSceneKey, string.Empty);
            bool requested = EditorTestRequested
                && !string.IsNullOrWhiteSpace(gameplaySceneName);
            EditorCancelTestRun();
            return requested;
        }

        public static void EditorCancelTestRun()
        {
            UnityEditor.SessionState.EraseBool(EditorTestRequestedKey);
            UnityEditor.SessionState.EraseString(EditorTestSceneKey);
        }
#endif
    }

    /// <summary>
    /// Runs three standalone tutorial orders without lives or progress receipts. The first is guided; later
    /// orders release control as soon as their presentation is ready.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-850)]
    [AddComponentMenu("Liquid Sort/Tutorial/First Shift Director")]
    public sealed class BartenderFirstShiftDirector : MonoBehaviour, IBartenderInputPolicy
    {
        /// <summary>Editable tutorial copy; see <see cref="BartenderTutorialCopy"/>.</summary>
        private static BartenderTutorialCopy Copy => BartenderTutorialCopy.Resolve();

        private const string LevelResourcePath = "Tutorials/BartenderFirstShift";
        private const string MainMenuSceneName = "SortingShelfShowcase";

        private sealed class HiddenObject
        {
            public GameObject Target;
            public bool WasActive;
        }

        /// <summary>
        /// Owns the changing state for one tutorial run. Callbacks must match this context before the
        /// director applies their effects.
        /// </summary>
        private sealed class TutorialRunContext
        {
            public readonly long Id;
            public readonly List<HiddenObject> HiddenObjects =
                new List<HiddenObject>(4);
            public Coroutine GuidanceRoutine;
            public Coroutine SettlementRoutine;
            public BsAttemptId AttemptId;
            public BsRoundToken RoundToken;
            public Action<int> SelectionChangedHandler;
            public Action<BartenderPourReceipt> PouredHandler;
            public Action<BartenderBoardChange> BoardCommittedHandler;
            public Action<BartenderLevelState> StateChangedHandler;
            public int StepCompleteIndex;
            public bool OwnsStandaloneRound;
            public double PauseStartedAt = -1d;
            public double PausedDuration;
#if UNITY_EDITOR
            public bool EditorTestRun;
#endif

            public TutorialRunContext(long id)
            {
                Id = id;
            }
        }

        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderPourInteraction interaction;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private BartenderSession session;
        [SerializeField] private OrderStripPresenter orderStrip;
        [SerializeField] private BartenderFirstShiftOverlayView overlay;
        [Tooltip("Campaign-only HUD objects hidden while the authored tutorial rig runs.")]
        [SerializeField] private GameObject[] campaignChrome = new GameObject[0];

        private readonly BsFirstShiftTutorialStateMachine tutorialFlow =
            new BsFirstShiftTutorialStateMachine();
        private TutorialRunContext activeRun;
        private long nextRunId;

        internal bool HasAuthoredBindings => controller != null
            && interaction != null && shelfView != null && session != null
            && orderStrip != null && overlay != null
            && overlay.ValidateAuthoredBindings(out _)
            && ReferenceEquals(session.Controller, controller);
        internal BartenderLevelController Controller => controller;

        private void Awake()
        {
            ResolveDependencies();
            // The authored director stays idle unless the menu authorized this rig. The scene installer
            // grants that request before Start.
            if (BartenderFirstShiftInstaller.IsLaunchAuthorized(gameObject))
                controller?.DisableAutomaticLoadAtRuntime();
        }

        private void OnEnable()
        {
            tutorialFlow.Reset();
            activeRun = new TutorialRunContext(NextRunId());
            ResolveDependencies();
            Subscribe(activeRun);
        }

        private IEnumerator Start()
        {
            TutorialRunContext run = activeRun;
            // Wait one frame so scene installation and every presenter's Awake/OnEnable finish before
            // loading the tutorial board.
            yield return null;
            if (!IsRunActive(run)) yield break;
            bool editorReplay = false;
#if UNITY_EDITOR
            editorReplay = BartenderFirstShiftInstaller
                .IsEditorReplayAuthorized(gameObject);
#endif
            bool launchEligible = editorReplay
                || BartenderFirstShiftProgress.ShouldStart;
            if (!launchEligible
                || !BartenderFirstShiftInstaller.IsLaunchAuthorized(gameObject))
            {
                BartenderFirstShiftInstaller.ReleaseLaunchAuthorization(gameObject);
                TrySettle(run, abortOwnedRound: false, immediate: true);
                Destroy(this);
                yield break;
            }
            yield return BeginFirstShift(run, editorReplay);
        }

        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();

        private IEnumerator BeginFirstShift(
            TutorialRunContext run,
            bool editorReplay)
        {
            if (!IsRunActive(run)
                || tutorialFlow.State != BsFirstShiftTutorialState.Dormant
                || !tutorialFlow.Begin())
                yield break;
#if UNITY_EDITOR
            run.EditorTestRun = editorReplay;
#endif
            ResolveDependencies();
            if (controller == null || interaction == null || shelfView == null
                || session == null || orderStrip == null || overlay == null
                || !overlay.ValidateAuthoredBindings(out _)
                || !ReferenceEquals(session.Controller, controller))
            {
                AbortFirstShift(run, "Gameplay rig dependencies are missing.");
                yield break;
            }

            BsLevel firstShift = Resources.Load<BsLevel>(LevelResourcePath);
            if (firstShift == null)
            {
                AbortFirstShift(run, $"Resources/{LevelResourcePath} was not found.");
                yield break;
            }

            if (!controller.TryStartStandalone(firstShift, out string rejectionReason))
            {
                AbortFirstShift(run, rejectionReason);
                yield break;
            }
            // TryStartStandalone may notify listeners before returning. Claim the board only after success,
            // then recheck for an in-call failure.
            run.OwnsStandaloneRound = controller.IsStandaloneRound;
            if (!run.OwnsStandaloneRound
                || controller.State != BartenderLevelState.Playing)
            {
                AbortFirstShift(run,
                    "The standalone board did not enter its playable state.");
                yield break;
            }
            BsRoundCommandStamp roundStamp = controller.CurrentRoundStamp;
            run.AttemptId = roundStamp.AttemptId;
            run.RoundToken = roundStamp.Token;
            if (!roundStamp.IsValid
                || run.RoundToken != controller.CurrentRoundToken)
            {
                AbortFirstShift(run,
                    "The standalone board did not expose an exact round identity.");
                yield break;
            }
            // Hide campaign UI after board loading refreshes the level badge.
            HideCampaignChrome(run);
            if (!IsRunActive(run)) yield break;

            // Own input while the board settles, then teach the first action directly.
            if (!interaction.TrySetInputPolicy(this))
            {
                AbortFirstShift(run,
                    "Another modal input policy owns the board.");
                yield break;
            }

            yield return WaitForPresentationReady(run,
                "Presentation did not become ready");
            if (!IsRunActive(run)) yield break;
            if (!IsStandaloneActive(run))
            {
                AbortFirstShift(run,
                    "The standalone board stopped before guidance began.");
                yield break;
            }

            if (!tutorialFlow.StartGuidance())
            {
                AbortFirstShift(run, "The first guided action could not start.");
                yield break;
            }
            if (!ShowTarget(0,
                    Copy.DirectStart,
                    BartenderFirstShiftPose.Point))
                AbortFirstShift(run,
                    "The first guided glass could not be resolved.");
        }

        public bool Allows(BartenderInputRequest request, out string rejectionReason)
        {
            rejectionReason = null;
            switch (tutorialFlow.State)
            {
                case BsFirstShiftTutorialState.SelectSource:
                    if (request.Intent == BartenderInputIntent.BottleTap
                        && request.PrimaryGlassId == 0) return true;
                    rejectionReason = Copy.RejectTapGlassFirst;
                    return false;

                case BsFirstShiftTutorialState.PourToShot:
                    if (request.Intent == BartenderInputIntent.BottleTap
                        && request.PrimaryGlassId == 2
                        && request.SelectedGlassId == 0) return true;
                    if (request.Intent == BartenderInputIntent.Pour
                        && request.PrimaryGlassId == 0
                        && request.SecondaryGlassId == 2) return true;
                    rejectionReason = Copy.RejectPourIntoEmpty;
                    return false;

                case BsFirstShiftTutorialState.ServeShot:
                    if ((request.Intent == BartenderInputIntent.BottleTap
                         || request.Intent == BartenderInputIntent.Delivery)
                        && request.PrimaryGlassId == 2) return true;
                    rejectionReason = Copy.RejectServeReadyDrink;
                    return false;

                case BsFirstShiftTutorialState.FreePlay:
                    return true;

                default:
                    rejectionReason = "One moment — the next order is arriving.";
                    return false;
            }
        }

        // Play the teaching cue at +0, +3 and +5 semitones. Each completed step sounds higher while staying
        // in the music's scale.
        private static readonly float[] StepCompletePitches = { 1.0000f, 1.1892f, 1.3348f };
        private const float StepCompleteVolume = 0.85f;

        private void PlayStepComplete(TutorialRunContext run)
        {
            if (!IsRunActive(run)) return;
            BsAudio audio = BsAudio.Instance;
            if (audio == null) return;
            float pitch = StepCompletePitches[
                Mathf.Min(run.StepCompleteIndex, StepCompletePitches.Length - 1)];
            run.StepCompleteIndex++;
            audio.Play(BsSfx.StepComplete, StepCompleteVolume, pitch);
        }

        public void HandleRejected(BartenderInputRequest request, string rejectionReason)
        {
            BsFirstShiftTutorialState state = tutorialFlow.State;
            if (state == BsFirstShiftTutorialState.SelectSource
                || state == BsFirstShiftTutorialState.PourToShot
                || state == BsFirstShiftTutorialState.ServeShot)
                overlay?.Nudge();
        }

        private void HandleSelectionChanged(
            TutorialRunContext run,
            int glassId)
        {
            if (!IsRunActive(run) || !IsStandaloneRunning(run)) return;
            // Pause clears the selected glass. Teach selection again so the empty-glass step cannot
            // require a source that input no longer owns. A committed pour already left PourToShot.
            if (glassId == -1 && tutorialFlow.SourceDeselected())
            {
                if (!ShowTarget(0, Copy.TapRedGlass,
                        BartenderFirstShiftPose.Point, animate: false))
                    AbortFirstShift(run,
                        "The first guided glass could not be restored after selection cleared.");
                return;
            }
            if (!IsStandaloneActive(run)) return;
            if (glassId != 0 || !tutorialFlow.SourceSelected()) return;
            PlayStepComplete(run);
            if (!ShowTarget(2, Copy.TapEmptyGlass,
                    BartenderFirstShiftPose.Point))
                AbortFirstShift(run,
                    "The empty guided glass could not be resolved.");
        }

        private void HandlePoured(
            TutorialRunContext run,
            BartenderPourReceipt receipt)
        {
            if (!IsCurrentPourReceipt(run, receipt)
                || tutorialFlow.State != BsFirstShiftTutorialState.PourToShot
                || receipt.SourceBefore == null || receipt.TargetBefore == null
                || receipt.SourceBefore.Id != 0 || receipt.TargetBefore.Id != 2) return;
            if (!tutorialFlow.PourCommitted()) return;

            PlayStepComplete(run);
            overlay?.SuspendForGameplayPresentation();
            ReplaceGuidanceRoutine(run, ShowServeStepWhenReady(run));
        }

        private void HandleBoardCommitted(
            TutorialRunContext run,
            BartenderBoardChange change)
        {
            if (!TryAcceptDeliveryCommit(run, change,
                    out BartenderDeliveryReceipt receipt)
                || receipt.DeliveredOrder == null) return;
            int orderIndex = receipt.DeliveredOrder.RuntimeOrderIndex;
            if (orderIndex == 0 && tutorialFlow.FirstOrderDelivered())
            {
                PlayStepComplete(run);
                overlay?.SuspendForGameplayPresentation();
                ReplaceGuidanceRoutine(run, ShowSecondOrderWhenReady(run));
                return;
            }

            if (orderIndex == 1 && tutorialFlow.SecondOrderDelivered())
            {
                if (!interaction.TrySetInputPolicy(this))
                {
                    AbortFirstShift(run,
                        "Another modal input policy owns the board.");
                    return;
                }
                overlay?.SuspendForGameplayPresentation();
                ReplaceGuidanceRoutine(run, ShowThirdOrderWhenReady(run));
                return;
            }

            if (orderIndex == 2
                && tutorialFlow.State == BsFirstShiftTutorialState.FreePlay)
                BeginCompletion(run);
        }

        private bool IsCurrentPourReceipt(
            TutorialRunContext run,
            BartenderPourReceipt receipt)
        {
            return IsStandaloneActive(run)
                && receipt != null
                && receipt.AttemptId.IsValid
                && receipt.OperationId.IsValid
                && receipt.DomainRevision > 0L
                && receipt.BoardRevision >= 0
                && receipt.Cause == BsRoundTransitionCause.PlayerPour
                && receipt.AttemptId == run.AttemptId
                && receipt.Token == run.RoundToken
                && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                    receipt.AttemptId,
                    receipt.Token,
                    receipt.DomainRevision,
                    receipt.BoardRevision);
        }

        private bool TryAcceptDeliveryCommit(
            TutorialRunContext run,
            BartenderBoardChange change,
            out BartenderDeliveryReceipt receipt)
        {
            receipt = change != null ? change.DeliveryReceipt : null;
            bool currentCommit = IsRunActive(run)
                && run.OwnsStandaloneRound
                && controller != null && controller.IsStandaloneRound
                && change != null
                && change.AttemptId.IsValid
                && change.OperationId.IsValid
                && change.DomainRevision > 0L
                && change.BoardRevision >= 0
                && change.Cause == BsRoundTransitionCause.PlayerDelivery
                && change.AttemptId == run.AttemptId
                && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                    change.AttemptId,
                    change.Token,
                    change.DomainRevision,
                    change.BoardRevision)
                && DeliveryMatchesBoardChange(receipt, change);
            if (!currentCommit) return false;
            if (controller.State == BartenderLevelState.Playing)
                return change.Token == run.RoundToken;

            // Winning invalidates gameplay before BoardCommitted. Accept only this run's exact final-
            // delivery transition.
            if (controller.State != BartenderLevelState.Won
                || tutorialFlow.State != BsFirstShiftTutorialState.FreePlay
                || receipt.DeliveredOrder?.RuntimeOrderIndex != 2
                || change.Token.RoundId != run.RoundToken.RoundId
                || run.RoundToken.GameplayEpoch == int.MaxValue
                || change.Token.GameplayEpoch != run.RoundToken.GameplayEpoch + 1
                || !controller.TryGetCurrentTerminalTransition(
                    out BsRoundTransition terminal)
                || terminal.From != BsRoundState.Playing
                || terminal.To != BsRoundState.Completed
                || terminal.Completion != BsRoundCompletion.Won
                || terminal.Cause != BsRoundTransitionCause.PlayerDelivery
                || terminal.Stamp != controller.CurrentRoundStamp
                || terminal.OperationId != change.OperationId
                || !Nullable.Equals(terminal.SettlementReceipt,
                    change.SettlementReceipt))
                return false;

            run.RoundToken = change.Token;
            return true;
        }

        private static bool DeliveryMatchesBoardChange(
            BartenderDeliveryReceipt receipt,
            BartenderBoardChange change)
        {
            return receipt != null && change != null
                && receipt.AttemptId == change.AttemptId
                && receipt.Token == change.Token
                && receipt.OperationId == change.OperationId
                && receipt.DomainRevision == change.DomainRevision
                && receipt.BoardRevision == change.BoardRevision
                && receipt.Cause == change.Cause
                && Nullable.Equals(
                    receipt.SettlementReceipt,
                    change.SettlementReceipt);
        }

        private IEnumerator ShowServeStepWhenReady(TutorialRunContext run)
        {
            yield return WaitForPresentationReady(run,
                "Serve presentation did not become ready");
            if (!IsRunActive(run)) yield break;
            if (tutorialFlow.State
                    != BsFirstShiftTutorialState.ServePresentation
                || !IsStandaloneActive(run))
            {
                if (!tutorialFlow.IsAborting)
                    AbortFirstShift(run,
                        "The serve step changed state before it became interactive.");
                yield break;
            }
            if (!tutorialFlow.ServePresentationReady())
            {
                AbortFirstShift(run,
                    "The serve presentation could not advance.");
                yield break;
            }
            if (!ShowTarget(2, Copy.TapFinishedDrink,
                    BartenderFirstShiftPose.Celebrate))
                AbortFirstShift(run,
                    "The finished guided drink could not be resolved.");
        }

        private IEnumerator ShowSecondOrderWhenReady(TutorialRunContext run)
        {
            yield return WaitForPresentationReady(run,
                "Second order did not become ready");
            if (!IsRunActive(run)) yield break;
            if (tutorialFlow.State
                    != BsFirstShiftTutorialState.SecondOrderIntro
                || !IsStandaloneActive(run))
            {
                if (!tutorialFlow.IsAborting)
                    AbortFirstShift(run,
                        "The second order changed state before it became interactive.");
                yield break;
            }
            if (!tutorialFlow.Advance())
                yield break;
            overlay.HideImmediate();
            interaction.ClearInputPolicy(this);
        }

        private IEnumerator ShowThirdOrderWhenReady(TutorialRunContext run)
        {
            yield return WaitForPresentationReady(run,
                "Third order did not become ready");
            if (!IsRunActive(run)) yield break;
            if (tutorialFlow.State
                    != BsFirstShiftTutorialState.ThirdOrderIntro
                || !IsStandaloneActive(run))
            {
                if (!tutorialFlow.IsAborting)
                    AbortFirstShift(run,
                        "The third order changed state before it became interactive.");
                yield break;
            }
            if (!tutorialFlow.Advance())
                yield break;
            overlay.HideImmediate();
            interaction.ClearInputPolicy(this);
        }

        private void BeginCompletion(TutorialRunContext run)
        {
            if (!IsRunActive(run) || !tutorialFlow.ThirdOrderDelivered()) return;
            CompleteProgressIfProduction(run);
            interaction.ClearInputPolicy(this);
            overlay?.SuspendForGameplayPresentation();
            ReplaceGuidanceRoutine(run, CompleteAndEnterCampaign(run));
        }

        private void CompleteProgressIfProduction(TutorialRunContext run)
        {
#if UNITY_EDITOR
            if (run.EditorTestRun) return;
#endif
            BartenderFirstShiftProgress.Complete();
        }

        private IEnumerator CompleteAndEnterCampaign(TutorialRunContext run)
        {
            yield return WaitForPresentationSettled(run,
                "Completion presentation did not become ready");
            if (!IsRunActive(run)) yield break;

            float stateDeadline = Time.realtimeSinceStartup + 2f;
            while (IsRunActive(run) && controller != null
                   && controller.IsStandaloneRound
                   && RoundMatchesContext(run)
                   && controller.State != BartenderLevelState.Won
                   && Time.realtimeSinceStartup < stateDeadline)
                yield return null;
            if (!IsRunActive(run)) yield break;
            if (controller == null || !controller.IsStandaloneRound
                || !RoundMatchesContext(run))
            {
                AbortFirstShift(run,
                    "The standalone board changed before completion was shown.");
                yield break;
            }
            if (controller.State != BartenderLevelState.Won)
            {
                AbortFirstShift(run,
                    "The completed order did not settle the standalone board.");
                yield break;
            }

            overlay.Show(Copy.ShiftComplete,
                BartenderFirstShiftPose.Celebrate, null, null, false,
                advanceOnTap: false);
            double feedbackDeadline = TutorialTime(run) + 0.75d;
            while (IsRunActive(run) && TutorialTime(run) < feedbackDeadline)
                yield return null;
            if (!IsRunActive(run)) yield break;

            if (!tutorialFlow.Advance())
            {
                AbortFirstShift(run,
                    "The completion step could not enter transition.");
                yield break;
            }
            overlay.HideImmediate();
            RestoreCampaignChrome(run);
            controller.UnloadLevel();
            yield return null;
            if (!IsRunActive(run)) yield break;
            if (controller.IsStandaloneRound && RoundMatchesContext(run))
            {
                AbortFirstShift(run,
                    "The completed standalone board could not be unloaded.");
                yield break;
            }
            run.OwnsStandaloneRound = false;

#if UNITY_EDITOR
            if (run.EditorTestRun)
            {
                // Editor rehearsal ends by leaving Play Mode. Loading the menu could accidentally start a
                // real campaign attempt.
                tutorialFlow.TransitionCompleted();
                BartenderFirstShiftProgress.EditorCancelTestRun();
                run.GuidanceRoutine = null;
                TrySettle(run, abortOwnedRound: false, immediate: true);
                Destroy(this);
                UnityEditor.EditorApplication.ExitPlaymode();
                yield break;
            }
#endif

            if (!controller.TryStartSavedCampaign(out string rejectionReason))
            {
                Debug.LogWarning("[First Shift] Campaign could not start: "
                    + rejectionReason, this);
                if (Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
                    LoadMainMenuWithMusicSuspended();
            }

            tutorialFlow.TransitionCompleted();
            run.GuidanceRoutine = null;
            TrySettle(run, abortOwnedRound: false, immediate: true);
            Destroy(this);
        }

        private void HandleControllerStateChanged(
            TutorialRunContext run,
            BartenderLevelState state)
        {
            if (!IsRunActive(run) || !tutorialFlow.HasBegun
                || !run.OwnsStandaloneRound || controller == null) return;

            // Completion unloads this board during Transition. Any other level or round replacement cancels
            // the run.
            if (state == BartenderLevelState.Unloaded
                && tutorialFlow.State == BsFirstShiftTutorialState.Transition)
                return;
            if (!controller.IsStandaloneRound || !RoundMatchesContext(run))
            {
                AbortFirstShift(run,
                    "The onboarding board was replaced by another level.");
                return;
            }
            UpdatePauseClock(run, state, Time.realtimeSinceStartupAsDouble);
            if (state == BartenderLevelState.Playing
                || state == BartenderLevelState.Paused
                || state == BartenderLevelState.Won) return;
            AbortFirstShift(run, state == BartenderLevelState.Failed
                ? "The onboarding board reached a failed state."
                : "The onboarding board stopped before the tutorial completed.");
        }

        private bool ShowTarget(int glassId, string message, BartenderFirstShiftPose pose,
                                bool animate = true)
        {
            if (shelfView != null && shelfView.TryGetBottle(glassId, out LiquidBottle bottle)
                && bottle != null && interaction != null
                && interaction.InputCamera != null)
            {
                overlay.Show(message, pose, bottle, interaction.InputCamera, animate: animate);
                return true;
            }
            return false;
        }

        private IEnumerator WaitForPresentationReady(
            TutorialRunContext run,
            string timeoutReason)
        {
            const float timeoutSeconds = 8f;
            double deadline = TutorialTime(run) + timeoutSeconds;
            while (IsRunActive(run) && IsStandaloneRunning(run)
                   && (IsStandalonePaused(run) || !PresentationReady()))
            {
                if (TutorialTime(run) >= deadline)
                {
                    AbortFirstShift(run, timeoutReason + ": "
                        + PresentationBlockerSummary());
                    yield break;
                }
                yield return null;
            }

            if (IsRunActive(run) && !IsStandaloneRunning(run))
                AbortFirstShift(run,
                    "The standalone board changed while presentation was settling.");
        }

        private IEnumerator WaitForPresentationSettled(
            TutorialRunContext run,
            string timeoutReason)
        {
            const float timeoutSeconds = 8f;
            double deadline = TutorialTime(run) + timeoutSeconds;
            while (IsRunActive(run) && controller != null
                   && controller.IsStandaloneRound
                   && RoundMatchesContext(run)
                   && (IsStandalonePaused(run) || !PresentationSettled(run)))
            {
                if (TutorialTime(run) >= deadline)
                {
                    AbortFirstShift(run, timeoutReason + ": "
                        + PresentationBlockerSummary());
                    yield break;
                }
                yield return null;
            }

            if (IsRunActive(run)
                && (controller == null || !controller.IsStandaloneRound
                    || !RoundMatchesContext(run)))
                AbortFirstShift(run,
                    "The standalone board changed while completion was settling.");
        }

        private bool PresentationReady() => controller != null
            && interaction != null
            && shelfView != null
            && session != null
            && overlay != null
            && overlay.ValidateAuthoredBindings(out _)
            && ReferenceEquals(session.Controller, controller)
            && session.AcceptsInput
            && !controller.PresentationLocked
            && !interaction.Busy
            && shelfView.Ready
            && !shelfView.SeatAnimationPlaying
            && !shelfView.SynchronizationDeferred
            && (orderStrip == null || !orderStrip.TransitionPlaying);

        private bool PresentationSettled(TutorialRunContext run) => controller != null
            && interaction != null
            && shelfView != null
            && RoundMatchesContext(run)
            && !controller.PresentationLocked
            && !interaction.Busy
            && shelfView.Ready
            && !shelfView.SeatAnimationPlaying
            && !shelfView.SynchronizationDeferred
            && (orderStrip == null || !orderStrip.TransitionPlaying);

        private string PresentationBlockerSummary()
        {
            if (controller == null || interaction == null || shelfView == null
                || session == null)
                return "missing runtime dependency";
            return $"controllerLock={controller.PresentationLocked}, "
                 + $"interactionBusy={interaction.Busy}, shelfReady={shelfView.Ready}, "
                 + $"sessionMatches={ReferenceEquals(session.Controller, controller)}, "
                 + $"sessionAcceptsInput={session.AcceptsInput}, "
                 + $"seat={shelfView.SeatAnimationPlaying}, "
                 + $"syncDeferred={shelfView.SynchronizationDeferred}, "
                 + $"orders={(orderStrip != null && orderStrip.TransitionPlaying)}, "
                 + $"shelfError={shelfView.LastError}";
        }

        private void AbortFirstShift(TutorialRunContext run, string reason)
        {
            if (!IsRunActive(run) || !tutorialFlow.Abort()) return;
            Debug.LogWarning("[First Shift] " + reason, this);
            bool shouldReturnToMenu = ShouldReturnToMenu(run);
            TrySettle(run, abortOwnedRound: true, immediate: false,
                returnToMenu: shouldReturnToMenu);
        }

        private bool ShouldReturnToMenu(TutorialRunContext run)
        {
            if (!OwnsRunContext(run)) return false;
            if (controller == null
                || controller.State == BartenderLevelState.Unloaded
                || controller.State == BartenderLevelState.CampaignComplete)
                return true;
            return run.OwnsStandaloneRound
                && controller.IsStandaloneRound
                && RoundMatchesContext(run);
        }

        private IEnumerator AbortStandaloneWhenSafe(
            TutorialRunContext run,
            bool returnToMenu)
        {
            // Wait for the controller callback to finish so TrySettle can store this coroutine as the
            // settling owner.
            yield return null;
            if (!OwnsRunContext(run)) yield break;
            if (run.OwnsStandaloneRound && controller != null
                && controller.IsStandaloneRound && RoundMatchesContext(run))
            {
                controller.RequestAbortStandalone(out _);
                float deadline = Time.realtimeSinceStartup + 2f;
                while (OwnsRunContext(run) && controller.IsStandaloneRound
                       && RoundMatchesContext(run)
                       && Time.realtimeSinceStartup < deadline)
                    yield return null;
            }

            if (!OwnsRunContext(run)) yield break;

            bool unloaded = controller == null || !controller.IsStandaloneRound
                || !RoundMatchesContext(run);
            if (run.OwnsStandaloneRound && !unloaded)
                Debug.LogWarning("[First Shift] The queued standalone abort timed out; "
                    + "returning to the menu so the board cannot remain stranded.", this);
            run.OwnsStandaloneRound = false;
            tutorialFlow.AbortCompleted();
            run.SettlementRoutine = null;
            CompleteSettlement(run);
#if UNITY_EDITOR
            if (run.EditorTestRun)
            {
                BartenderFirstShiftProgress.EditorCancelTestRun();
                Destroy(this);
                UnityEditor.EditorApplication.ExitPlaymode();
                yield break;
            }
#endif
            if (returnToMenu
                && Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
                LoadMainMenuWithMusicSuspended();

            Destroy(this);
        }

        private static void LoadMainMenuWithMusicSuspended()
        {
            IDisposable suspension = BsAudio.SuspendBackgroundMusic();
            try
            {
                AsyncOperation operation = SceneManager.LoadSceneAsync(
                    MainMenuSceneName, LoadSceneMode.Single);
                if (operation == null) return;
                BartenderSceneMusicHandoff.TakeOver(suspension, operation);
                suspension = null;
            }
            finally
            {
                suspension?.Dispose();
            }
        }

        private bool IsStandalonePaused(TutorialRunContext run) => IsRunActive(run)
            && controller != null && controller.IsStandaloneRound
            && controller.State == BartenderLevelState.Paused
            && RoundMatchesContext(run);

        private bool IsStandaloneRunning(TutorialRunContext run) =>
            IsStandaloneActive(run) || IsStandalonePaused(run);

        private static void UpdatePauseClock(
            TutorialRunContext run, BartenderLevelState state, double now)
        {
            if (state == BartenderLevelState.Paused)
            {
                if (run.PauseStartedAt < 0d) run.PauseStartedAt = now;
            }
            else if (run.PauseStartedAt >= 0d)
            {
                run.PausedDuration += Math.Max(0d, now - run.PauseStartedAt);
                run.PauseStartedAt = -1d;
            }
        }

        private static double TutorialTime(TutorialRunContext run) =>
            TutorialTimeAt(run, Time.realtimeSinceStartupAsDouble);

        private static double TutorialTimeAt(TutorialRunContext run, double now) =>
            (run.PauseStartedAt >= 0d ? run.PauseStartedAt : now)
            - run.PausedDuration;

        private bool IsStandaloneActive(TutorialRunContext run) => IsRunActive(run)
            && controller != null
            && controller.IsStandaloneRound
            && controller.State == BartenderLevelState.Playing
            && RoundMatchesContext(run);

        private bool RoundMatchesContext(TutorialRunContext run)
        {
            if (controller == null || run == null || !run.AttemptId.IsValid)
                return false;
            BsRoundCommandStamp stamp = controller.CurrentRoundStamp;
            return stamp.AttemptId == run.AttemptId
                && stamp.Token == run.RoundToken;
        }

        private void ReplaceGuidanceRoutine(
            TutorialRunContext run,
            IEnumerator routine)
        {
            if (!IsRunActive(run)) return;
            if (run.GuidanceRoutine != null) StopCoroutine(run.GuidanceRoutine);
            run.GuidanceRoutine = StartCoroutine(RunGuidance(run, routine));
        }

        private IEnumerator RunGuidance(
            TutorialRunContext run,
            IEnumerator routine)
        {
            yield return routine;
            if (OwnsRunContext(run)) run.GuidanceRoutine = null;
        }

        private void HideCampaignChrome(TutorialRunContext run)
        {
            if (!OwnsRunContext(run)) return;
            run.HiddenObjects.Clear();
            if (campaignChrome == null) return;
            for (int i = 0; i < campaignChrome.Length; i++)
            {
                GameObject candidate = campaignChrome[i];
                if (candidate == null) continue;
                run.HiddenObjects.Add(new HiddenObject
                {
                    Target = candidate,
                    WasActive = candidate.activeSelf,
                });
                candidate.SetActive(false);
            }
        }

        private static void RestoreCampaignChrome(TutorialRunContext run)
        {
            if (run == null) return;
            HiddenObject[] hiddenObjects = run.HiddenObjects.ToArray();
            run.HiddenObjects.Clear();
            Exception failure = null;
            for (int i = 0; i < hiddenObjects.Length; i++)
            {
                HiddenObject hidden = hiddenObjects[i];
                try
                {
                    if (hidden.Target != null)
                        hidden.Target.SetActive(hidden.WasActive);
                }
                catch (Exception exception)
                {
                    if (failure == null) failure = exception;
                }
            }
            if (failure != null) throw failure;
        }

        private long NextRunId()
        {
            nextRunId = nextRunId == long.MaxValue ? 1L : nextRunId + 1L;
            return nextRunId;
        }

        private bool OwnsRunContext(TutorialRunContext run) => run != null
            && ReferenceEquals(activeRun, run)
            && activeRun.Id == run.Id;

        private bool IsRunActive(TutorialRunContext run) => OwnsRunContext(run)
            && run.SettlementRoutine == null
            && !tutorialFlow.IsAborting
            && tutorialFlow.State != BsFirstShiftTutorialState.Finished
            && tutorialFlow.State != BsFirstShiftTutorialState.Disposed;

        /// <summary>
        /// The only run exit checks exact ownership so stale callbacks cannot clean up a newer tutorial.
        /// Immediate disable may finish an existing async abort.
        /// </summary>
        private bool TrySettle(
            TutorialRunContext run,
            bool abortOwnedRound,
            bool immediate,
            bool returnToMenu = false)
        {
            if (!OwnsRunContext(run)) return false;
            if (run.SettlementRoutine != null)
            {
                if (immediate)
                {
                    Coroutine settlement = run.SettlementRoutine;
                    run.SettlementRoutine = null;
                    // Detach before stopping coroutines or calling the controller so old cleanup cannot see
                    // a replacement run.
                    activeRun = null;
                    Exception reentrantFailure = ExecuteRunCleanup(
                        settlement != null
                            ? (Action)(() => StopCoroutine(settlement))
                            : null,
                        () =>
                        {
                            if (abortOwnedRound && run.OwnsStandaloneRound
                                && controller != null && controller.IsStandaloneRound
                                && RoundMatchesContext(run))
                                controller.RequestAbortStandalone(out _);
                        },
                        () => run.OwnsStandaloneRound = false,
                        () => CompleteDetachedSettlement(run));
                    if (reentrantFailure != null)
                        Debug.LogException(reentrantFailure, this);
                }
                return false;
            }

            bool mustAwaitOwnedRound = abortOwnedRound
                && run.OwnsStandaloneRound
                && controller != null
                && controller.IsStandaloneRound
                && RoundMatchesContext(run);
            bool settleAsynchronously = !immediate
                && (mustAwaitOwnedRound || tutorialFlow.IsAborting);
            Exception failure = null;
            if (settleAsynchronously)
            {
                try
                {
                    // Attach the yielding abort coroutine before cleanup can re-enter this context.
                    Coroutine settlement = StartCoroutine(
                        AbortStandaloneWhenSafe(run, returnToMenu));
                    if (settlement != null && OwnsRunContext(run))
                        run.SettlementRoutine = settlement;
                    else
                        settleAsynchronously = false;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    settleAsynchronously = false;
                }
            }

            if (!settleAsynchronously)
                activeRun = null;

            Coroutine guidance = run.GuidanceRoutine;
            run.GuidanceRoutine = null;
            Exception releaseFailure = ExecuteRunCleanup(
                guidance != null ? (Action)(() => StopCoroutine(guidance)) : null,
                () => interaction?.ClearInputPolicy(this),
                () => overlay?.HideImmediate(),
                () => RestoreCampaignChrome(run),
                () => Unsubscribe(run));
            if (failure == null) failure = releaseFailure;

            if (settleAsynchronously)
            {
                if (failure != null) Debug.LogException(failure, this);
                return true;
            }

            Exception terminalFailure = ExecuteRunCleanup(
                () =>
                {
                    if (mustAwaitOwnedRound && controller != null
                        && controller.IsStandaloneRound && RoundMatchesContext(run))
                        controller.RequestAbortStandalone(out _);
                },
                () => run.OwnsStandaloneRound = false,
                () => CompleteDetachedSettlement(run));
            if (failure == null) failure = terminalFailure;
            if (failure != null) Debug.LogException(failure, this);
            return true;
        }

        internal static Exception ExecuteRunCleanup(params Action[] cleanupSteps)
        {
            Exception firstFailure = null;
            if (cleanupSteps == null) return null;
            for (int i = 0; i < cleanupSteps.Length; i++)
            {
                Action cleanup = cleanupSteps[i];
                if (cleanup == null) continue;
                try
                {
                    cleanup();
                }
                catch (Exception exception)
                {
                    if (firstFailure == null) firstFailure = exception;
                }
            }
            return firstFailure;
        }

        private void CompleteSettlement(TutorialRunContext run)
        {
            if (!OwnsRunContext(run)) return;
            activeRun = null;
            CompleteDetachedSettlement(run);
        }

        private void CompleteDetachedSettlement(TutorialRunContext run)
        {
            if (run == null) return;
            run.AttemptId = default;
            run.RoundToken = default;
            run.SettlementRoutine = null;
            BartenderFirstShiftInstaller.ReleaseLaunchAuthorization(gameObject);
#if UNITY_EDITOR
            BartenderFirstShiftProgress.EditorCancelTestRun();
#endif
        }

        private void ResolveDependencies()
        {
            if (interaction == null) interaction = GetComponent<BartenderPourInteraction>();
            if (controller == null && interaction != null) controller = interaction.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (shelfView == null && interaction != null) shelfView = interaction.ShelfView;
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (session == null && interaction != null) session = interaction.Session;
            if (session == null) session = GetComponent<BartenderSession>();
            if (orderStrip == null) orderStrip = GetComponent<OrderStripPresenter>();
        }

        private void Subscribe(TutorialRunContext run)
        {
            if (!OwnsRunContext(run)) return;
            run.SelectionChangedHandler = glassId =>
                HandleSelectionChanged(run, glassId);
            run.PouredHandler = receipt => HandlePoured(run, receipt);
            run.BoardCommittedHandler = change =>
                HandleBoardCommitted(run, change);
            run.StateChangedHandler = state =>
                HandleControllerStateChanged(run, state);

            if (interaction != null)
                interaction.SelectionChanged += run.SelectionChangedHandler;
            if (controller != null)
            {
                controller.Poured += run.PouredHandler;
                controller.BoardCommitted += run.BoardCommittedHandler;
                controller.StateChanged += run.StateChangedHandler;
            }
        }

        private void Unsubscribe(TutorialRunContext run)
        {
            if (run == null) return;
            Action<int> selectionChanged = run.SelectionChangedHandler;
            Action<BartenderPourReceipt> poured = run.PouredHandler;
            Action<BartenderBoardChange> boardCommitted = run.BoardCommittedHandler;
            Action<BartenderLevelState> stateChanged = run.StateChangedHandler;
            run.SelectionChangedHandler = null;
            run.PouredHandler = null;
            run.BoardCommittedHandler = null;
            run.StateChangedHandler = null;

            Exception failure = ExecuteRunCleanup(
                interaction != null && selectionChanged != null
                    ? (Action)(() => interaction.SelectionChanged -= selectionChanged)
                    : null,
                controller != null && poured != null
                    ? (Action)(() => controller.Poured -= poured)
                    : null,
                controller != null && boardCommitted != null
                    ? (Action)(() => controller.BoardCommitted -= boardCommitted)
                    : null,
                controller != null && stateChanged != null
                    ? (Action)(() => controller.StateChanged -= stateChanged)
                    : null);
            if (failure != null) throw failure;
        }

        private void Cleanup()
        {
            TutorialRunContext run = activeRun;
            tutorialFlow.Dispose();
            if (run != null)
                TrySettle(run, abortOwnedRound: true, immediate: true);
            else
            {
                BartenderFirstShiftInstaller
                    .ReleaseLaunchAuthorization(gameObject);
#if UNITY_EDITOR
                BartenderFirstShiftProgress.EditorCancelTestRun();
#endif
            }
        }
    }

    /// <summary>
    /// Carries an explicit menu request and installs First Shift before gameplay Start. Opening the gameplay
    /// scene alone never starts it.
    /// </summary>
    internal readonly struct BartenderFirstShiftLaunchRequest :
        IEquatable<BartenderFirstShiftLaunchRequest>
    {
        private readonly long value;

        internal BartenderFirstShiftLaunchRequest(long value)
        {
            this.value = value;
        }

        internal bool IsValid => value != 0L;

        public bool Equals(BartenderFirstShiftLaunchRequest other) =>
            value == other.value;

        public override bool Equals(object obj) =>
            obj is BartenderFirstShiftLaunchRequest other && Equals(other);

        public override int GetHashCode() => value.GetHashCode();

        public static bool operator ==(
            BartenderFirstShiftLaunchRequest left,
            BartenderFirstShiftLaunchRequest right) => left.Equals(right);

        public static bool operator !=(
            BartenderFirstShiftLaunchRequest left,
            BartenderFirstShiftLaunchRequest right) => !left.Equals(right);
    }

    internal static class BartenderFirstShiftInstaller
    {
        private enum LaunchRequestState
        {
            Pending,
            Authorized,
        }

        /// <summary>
        /// Scene intent and host approval share one request so old menu callbacks cannot cancel a newer
        /// launch.
        /// </summary>
        private sealed class LaunchRequestContext
        {
            internal LaunchRequestContext(
                BartenderFirstShiftLaunchRequest request,
                string sceneName,
                bool editorReplay)
            {
                Request = request;
                SceneName = sceneName;
                EditorReplay = editorReplay;
                State = LaunchRequestState.Pending;
                AuthorizedSceneHandle = -1;
                SceneLoadedHandler = HandleSceneLoaded;
            }

            internal BartenderFirstShiftLaunchRequest Request { get; }
            internal string SceneName { get; }
            internal bool EditorReplay { get; }
            internal UnityEngine.Events.UnityAction<Scene, LoadSceneMode>
                SceneLoadedHandler { get; }
            internal LaunchRequestState State { get; private set; }
            internal int AuthorizedSceneHandle { get; private set; }
            internal int AuthorizedHostInstanceId { get; private set; }

            internal bool IsPending => State == LaunchRequestState.Pending;

            internal bool TryAuthorize(Scene scene, GameObject host)
            {
                if (!IsPending || host == null || !scene.IsValid()) return false;
                State = LaunchRequestState.Authorized;
                AuthorizedSceneHandle = scene.handle;
                AuthorizedHostInstanceId = host.GetInstanceID();
                return true;
            }

            internal bool IsAuthorized(GameObject host)
            {
                if (State != LaunchRequestState.Authorized || host == null
                    || AuthorizedHostInstanceId == 0)
                    return false;
                Scene scene = host.scene;
                return scene.IsValid()
                    && scene.handle == AuthorizedSceneHandle
                    && host.GetInstanceID() == AuthorizedHostInstanceId;
            }

            private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                BartenderFirstShiftInstaller.HandleSceneLoaded(
                    this, scene, mode);
            }
        }

        private static long lastRequestId;
        private static LaunchRequestContext activeRequest;

        internal static bool HasPendingLaunch => activeRequest != null
            && activeRequest.IsPending
            && PendingLaunchEligible(activeRequest);

        private static bool PendingLaunchEligible(LaunchRequestContext request)
        {
#if UNITY_EDITOR
            if (request != null && request.EditorReplay) return true;
#endif
            return BartenderFirstShiftProgress.ShouldStart;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            if (activeRequest != null) Unsubscribe(activeRequest);
            lastRequestId = 0L;
            activeRequest = null;
#if UNITY_EDITOR
            // Level Jumper stores its request before Play. Registration disables campaign auto-load before
            // the gameplay scene's Start methods.
            if (BartenderFirstShiftProgress.EditorTryConsumeTestScene(
                    out string editorTestSceneName))
            {
                string normalizedSceneName = NormalizeSceneName(editorTestSceneName);
                if (!string.IsNullOrWhiteSpace(normalizedSceneName))
                {
                    activeRequest = CreateRequest(normalizedSceneName, true);
                    if (activeRequest != null) Subscribe(activeRequest);
                }
            }
#endif
        }

        internal static bool RequestLaunch(
            string gameplaySceneName,
            out BartenderFirstShiftLaunchRequest request)
        {
            request = default;
            string normalizedSceneName = NormalizeSceneName(gameplaySceneName);
            if (!Application.isPlaying
                || !BartenderFirstShiftProgress.ShouldStart
                || string.IsNullOrWhiteSpace(normalizedSceneName))
            {
                return false;
            }

            LaunchRequestContext context = CreateRequest(normalizedSceneName, false);
            if (context == null) return false;
            if (activeRequest != null) Unsubscribe(activeRequest);
            activeRequest = context;
            request = context.Request;
            Subscribe(context);

            // Also handles requests for an already-loaded target scene.
            try
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    InstallInScene(SceneManager.GetSceneAt(i), context);
            }
            catch (Exception exception)
            {
                CancelPendingLaunch(context.Request);
                request = default;
                Debug.LogException(exception);
                return false;
            }
            return Owns(context);
        }

        internal static bool CancelPendingLaunch(
            BartenderFirstShiftLaunchRequest request)
        {
            if (!request.IsValid || activeRequest == null
                || activeRequest.Request != request)
                return false;
            LaunchRequestContext cancelled = activeRequest;
            activeRequest = null;
            Unsubscribe(cancelled);
            return true;
        }

        internal static bool IsLaunchAuthorized(GameObject host)
        {
            return activeRequest != null && activeRequest.IsAuthorized(host);
        }

#if UNITY_EDITOR
        internal static bool IsEditorReplayAuthorized(GameObject host) =>
            activeRequest != null
            && activeRequest.EditorReplay
            && activeRequest.IsAuthorized(host);
#endif

        internal static void ReleaseLaunchAuthorization(GameObject host)
        {
            if (activeRequest == null || !activeRequest.IsAuthorized(host)) return;
            LaunchRequestContext released = activeRequest;
            activeRequest = null;
            Unsubscribe(released);
        }

        private static void Subscribe(LaunchRequestContext request)
        {
            if (request == null) return;
            SceneManager.sceneLoaded -= request.SceneLoadedHandler;
            SceneManager.sceneLoaded += request.SceneLoadedHandler;
        }

        private static void Unsubscribe(LaunchRequestContext request)
        {
            if (request == null) return;
            SceneManager.sceneLoaded -= request.SceneLoadedHandler;
        }

        private static void HandleSceneLoaded(
            LaunchRequestContext request, Scene scene, LoadSceneMode _)
        {
            if (!Owns(request) || !request.IsPending) return;
            if (!PendingLaunchEligible(request))
            {
                CancelActiveRequest(request);
                return;
            }
            InstallInScene(scene, request);

            // Consume a request if its scene has no complete rig so an unrelated later scene cannot inherit
            // it.
            if (Owns(request) && request.IsPending
                && SceneMatchesRequest(scene, request))
            {
                Debug.LogWarning("[First Shift] The requested gameplay scene loaded "
                    + "without a valid Bartender rig; onboarding was skipped.");
#if UNITY_EDITOR
                bool editorTestRun = request.EditorReplay;
#endif
                CancelActiveRequest(request);
#if UNITY_EDITOR
                if (editorTestRun) UnityEditor.EditorApplication.ExitPlaymode();
#endif
            }
        }

        private static void InstallInScene(
            Scene scene, LaunchRequestContext request)
        {
            if (!Application.isPlaying || !Owns(request) || !request.IsPending
                || !PendingLaunchEligible(request)
                || !SceneMatchesRequest(scene, request)) return;

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                BartenderFirstShiftDirector director =
                    roots[i].GetComponentInChildren<BartenderFirstShiftDirector>(true);
                if (director == null || !director.HasAuthoredBindings
                    || !TryConsumeLaunch(request, scene, director.gameObject))
                    continue;

                director.Controller.DisableAutomaticLoadAtRuntime();
                return;
            }
        }

        private static bool TryConsumeLaunch(
            LaunchRequestContext request, Scene scene, GameObject host)
        {
            if (!Owns(request) || !request.IsPending
                || !PendingLaunchEligible(request) || host == null
                || !SceneMatchesRequest(scene, request)) return false;

            if (!request.TryAuthorize(scene, host)) return false;
            Unsubscribe(request);
            return true;
        }

        private static LaunchRequestContext CreateRequest(
            string sceneName, bool editorReplay)
        {
            if (lastRequestId == long.MaxValue) return null;
            lastRequestId++;
            return new LaunchRequestContext(
                new BartenderFirstShiftLaunchRequest(lastRequestId),
                sceneName,
                editorReplay);
        }

        private static bool Owns(LaunchRequestContext request) =>
            request != null && ReferenceEquals(activeRequest, request);

        private static void CancelActiveRequest(LaunchRequestContext request)
        {
            if (!Owns(request)) return;
            activeRequest = null;
            Unsubscribe(request);
        }

        private static bool SceneMatchesRequest(
            Scene scene, LaunchRequestContext request) =>
            scene.IsValid() && scene.isLoaded
            && request != null
            && !string.IsNullOrWhiteSpace(request.SceneName)
            && string.Equals(scene.name, request.SceneName,
                StringComparison.Ordinal);

        private static string NormalizeSceneName(string sceneNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(sceneNameOrPath)) return null;
            string value = sceneNameOrPath.Trim().Replace('\\', '/');
            int slashIndex = value.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex + 1 < value.Length)
                value = value.Substring(slashIndex + 1);
            const string extension = ".unity";
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - extension.Length);
            return value;
        }
    }
}
