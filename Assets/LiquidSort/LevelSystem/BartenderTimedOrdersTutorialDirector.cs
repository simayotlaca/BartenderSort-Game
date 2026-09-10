using System;
using System.Collections;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>Persistent one-time gate for the first campaign level with order clocks.</summary>
    public static class BartenderTimedOrdersTutorialProgress
    {
        public const int FirstTimedLevelNumber = 15;
        private const string TutorialId = "timed_orders";
        private const int Version = 1;

#if UNITY_EDITOR
        private const string EditorReplayRequestedKey =
            "GlassPourMathDemo.TimedOrdersTutorial.EditorReplayRequested";
        private const string EditorReplaySceneKey =
            "GlassPourMathDemo.TimedOrdersTutorial.EditorReplayScene";
        private const string EditorReplayRunningKey =
            "GlassPourMathDemo.TimedOrdersTutorial.EditorReplayRunning";
        private const string EditorSuppressNaturalStartKey =
            "GlassPourMathDemo.TimedOrdersTutorial.SuppressNaturalStart";
#endif

        public static bool IsCompleted =>
            BartenderTutorialProgress.IsCompleted(TutorialId, Version);

        public static void Complete() =>
            BartenderTutorialProgress.Complete(TutorialId, Version);

        public static void Reset() =>
            BartenderTutorialProgress.Reset(TutorialId, Version);

        /// <summary>
        /// Show natural onboarding only at the player's real next campaign level, not for old saves or Level
        /// Jumper rehearsals.
        /// </summary>
        public static bool ShouldStartNaturally(
            BartenderLevelController controller, BsLevel level)
        {
#if UNITY_EDITOR
            // Only the dedicated timed-tutorial button can replay this tutorial in the Editor.
            if (EditorNaturalStartSuppressed) return false;
#endif
            return controller != null
                && IsTimedIntroductionLevel(level)
                && ReferenceEquals(controller.CurrentLevel, level)
                && !controller.IsStandaloneRound
                && controller.CurrentCampaignSlot >= 0
                && controller.CurrentCampaignSlot == controller.NextUnlockedCampaignSlot
                && !IsCompleted;
        }

        internal static bool IsTimedIntroductionLevel(BsLevel level)
        {
            if (level == null || level.Index != FirstTimedLevelNumber
                || !level.AllowTimedOrders || level.Orders == null)
                return false;

            for (int i = 0; i < level.Orders.Count; i++)
            {
                OrderDef order = level.Orders[i];
                if (order != null && order.TimeLimit > 0f) return true;
            }
            return false;
        }

#if UNITY_EDITOR
        public static bool EditorReplayActive =>
            UnityEditor.SessionState.GetBool(EditorReplayRequestedKey, false)
            || EditorReplayRunning;

        public static bool EditorReplayRunning =>
            UnityEditor.SessionState.GetBool(EditorReplayRunningKey, false);

        private static bool EditorNaturalStartSuppressed =>
            UnityEditor.SessionState.GetBool(EditorSuppressNaturalStartKey, false);

        public static void EditorSuppressNaturalStartForLevelJumper() =>
            UnityEditor.SessionState.SetBool(EditorSuppressNaturalStartKey, true);

        public static void EditorClearNaturalStartSuppression() =>
            UnityEditor.SessionState.EraseBool(EditorSuppressNaturalStartKey);

        public static bool EditorTryArmReplay(string gameplaySceneName,
                                              out string rejectionReason)
        {
            string normalized = string.IsNullOrWhiteSpace(gameplaySceneName)
                ? null
                : gameplaySceneName.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                rejectionReason = "Timed tutorial gameplay scene is missing.";
                return false;
            }

            EditorCancelReplay();
            UnityEditor.SessionState.SetBool(EditorReplayRequestedKey, true);
            UnityEditor.SessionState.SetString(EditorReplaySceneKey, normalized);
            rejectionReason = null;
            return true;
        }

        internal static bool EditorTryConsumeReplayScene(out string gameplaySceneName)
        {
            gameplaySceneName = UnityEditor.SessionState.GetString(
                EditorReplaySceneKey, string.Empty);
            bool requested = UnityEditor.SessionState.GetBool(
                    EditorReplayRequestedKey, false)
                && !string.IsNullOrWhiteSpace(gameplaySceneName);
            UnityEditor.SessionState.EraseBool(EditorReplayRequestedKey);
            UnityEditor.SessionState.EraseString(EditorReplaySceneKey);
            if (requested)
                UnityEditor.SessionState.SetBool(EditorReplayRunningKey, true);
            return requested;
        }

        public static void EditorCancelReplay()
        {
            UnityEditor.SessionState.EraseBool(EditorReplayRequestedKey);
            UnityEditor.SessionState.EraseString(EditorReplaySceneKey);
            UnityEditor.SessionState.EraseBool(EditorReplayRunningKey);
        }
#endif
    }

    /// <summary>
    /// Shows one timed-order explanation. Order clocks stay frozen until the player dismisses it.
    /// </summary>
    [DefaultExecutionOrder(-825)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Tutorial/Timed Orders Director")]
    public sealed class BartenderTimedOrdersTutorialDirector :
        MonoBehaviour, IBartenderInputPolicy
    {
        /// <summary>Editable tutorial copy; see <see cref="BartenderTutorialCopy"/>.</summary>
        private static BartenderTutorialCopy Copy => BartenderTutorialCopy.Resolve();

        private const string TimedLevelResourcePath = "Levels/Level_015";
        private const float PresentationTimeoutSeconds = 10f;
        private const float PanelYOffset = 60f;

        /// <summary>
        /// Owns one tutorial run's leases, targets and async work so old callbacks cannot advance or release a
        /// newer run.
        /// </summary>
        private sealed class TutorialRunContext
        {
            public readonly long Id;
            public bool EditorReplay;
            public BsLevel ExpectedLevel;
            public BsAttemptId AttemptId;
            public BsRoundToken RoundToken;
            public BartenderLevelController BarrierController;
            public Action<BsLevel> LevelLoadedHandler;
            public Action<BartenderLevelState> StateChangedHandler;
            public Action AdvanceRequestedHandler;
            public Coroutine TutorialRoutine;
            public Coroutine SettlementRoutine;
            public OrderCardView TimedCard;
            public RectTransform TimerTarget;
            public int AdvanceRequestVersion;
            public double PauseStartedAt = -1d;
            public double PausedDuration;
            public bool OwnsStandaloneRound;

            public TutorialRunContext(
                long id,
                BsLevel expectedLevel,
                bool editorReplay)
            {
                Id = id;
                ExpectedLevel = expectedLevel;
                EditorReplay = editorReplay;
            }
        }

        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderPourInteraction interaction;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private OrderStripPresenter orderStrip;
        [SerializeField] private BartenderLevelIntroDirector levelIntro;
        [SerializeField] private BartenderFirstShiftOverlayView overlay;

        private readonly BsTimedOrdersTutorialStateMachine tutorialFlow =
            new BsTimedOrdersTutorialStateMachine();
        private TutorialRunContext activeRun;
        private long nextRunId;

        internal bool HasAuthoredBindings => controller != null
            && interaction != null && shelfView != null && orderStrip != null
            && levelIntro != null && overlay != null
            && overlay.ValidateAuthoredBindings(out _);
        internal BartenderLevelController Controller => controller;

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            tutorialFlow.Reset();
            activeRun = new TutorialRunContext(
                NextRunId(), null, editorReplay: false);
            ResolveDependencies();
            Subscribe(activeRun);
        }

        private IEnumerator Start()
        {
            TutorialRunContext activation = activeRun;
            // Wait one frame for presenters to finish startup before loading a replay board.
            yield return null;
            if (!IsRunActive(activation)) yield break;
            ResolveDependencies();

#if UNITY_EDITOR
            bool editorReplay = BartenderTimedOrdersTutorialInstaller
                .IsEditorReplayAuthorized(gameObject);
            if (editorReplay)
            {
                TutorialRunContext run = BeginRun(
                    activation, null, editorReplay: true);
                if (run != null) StartEditorReplayBoard(run);
                yield break;
            }
#endif

            if (IsRunActive(activation)
                && controller != null && controller.CurrentLevel != null)
                HandleLevelLoaded(activation, controller.CurrentLevel);
        }

        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();

        private void HandleLevelLoaded(
            TutorialRunContext callbackRun,
            BsLevel level)
        {
            if (level == null || controller == null) return;

            TutorialRunContext run = callbackRun;
            if (!IsRunActive(run)) return;
            if (tutorialFlow.State == BsTimedOrdersTutorialState.Dormant)
            {
                bool eligible = BartenderTimedOrdersTutorialProgress
                    .ShouldStartNaturally(controller, level);
                if (!eligible) return;
                run = BeginRun(run, level, editorReplay: false);
                if (run != null) ArmRunForLevel(run, level);
                return;
            }

            if (run != null)
            {
                if (run.EditorReplay && run.ExpectedLevel == null
                    && BartenderTimedOrdersTutorialProgress
                        .IsTimedIntroductionLevel(level))
                {
                    ArmRunForLevel(run, level);
                    return;
                }
                if (IsRunActive(run)
                    && ReferenceEquals(run.ExpectedLevel, level)
                    && RoundMatchesContext(run))
                    return;

                if (IsRunActive(run))
                    AbortTutorial(run,
                        "The level changed while timed-order onboarding was open.");
                return;
            }
        }

        private TutorialRunContext BeginRun(
            TutorialRunContext run,
            BsLevel level,
            bool editorReplay)
        {
            if (!IsRunActive(run)
                || tutorialFlow.State != BsTimedOrdersTutorialState.Dormant)
                return null;
            if (!tutorialFlow.Begin()) return null;
            run.ExpectedLevel = level;
            run.EditorReplay = editorReplay;
            return run;
        }

        private void ArmRunForLevel(TutorialRunContext run, BsLevel level)
        {
            if (!IsRunActive(run) || level == null) return;
            run.ExpectedLevel = level;

            if (overlay == null || !overlay.ValidateAuthoredBindings(out _))
            {
                AbortTutorial(run,
                    "The authored tutorial overlay cannot be presented.");
                return;
            }

            // Take the barrier during load so unscaled order clocks cannot consume even the first tutorial
            // frame.
            if (!controller.AcquirePresentationBarrier(run))
            {
                AbortTutorial(run,
                    "The order clocks could not be paused for onboarding.");
                return;
            }
            run.BarrierController = controller;
            UpdatePauseClock(run, controller.State);
            BsRoundCommandStamp roundStamp = controller.CurrentRoundStamp;
            run.AttemptId = roundStamp.AttemptId;
            run.RoundToken = roundStamp.Token;
            if (!roundStamp.IsValid
                || run.RoundToken != controller.CurrentRoundToken)
            {
                AbortTutorial(run,
                    "The timed-order board did not expose an exact round identity.");
                return;
            }
            run.TutorialRoutine = StartCoroutine(RunTutorial(run));
        }

        private IEnumerator RunTutorial(TutorialRunContext run)
        {
            double deadline = PresentationTime(run) + PresentationTimeoutSeconds;
            while (IsRunActive(run) && ContextIsCurrent(run)
                   && !TryResolveTargets(run))
            {
                if (PresentationTime(run) >= deadline)
                {
                    AbortTutorial(run,
                        "The timed-order presentation did not become ready: "
                        + PresentationBlockerSummary());
                    yield break;
                }
                yield return null;
            }

            if (!IsRunActive(run)) yield break;
            if (!ContextIsCurrent(run))
            {
                AbortTutorial(run,
                    "The level changed before timed-order onboarding began.");
                yield break;
            }
            if (!EnsureOverlay(run))
            {
                AbortTutorial(run,
                    "The tutorial overlay could not be created.");
                yield break;
            }
            if (interaction == null || !interaction.TrySetInputPolicy(this))
            {
                AbortTutorial(run,
                    "Another modal input policy owns the board.");
                yield break;
            }

            if (!tutorialFlow.PresentationReady())
            {
                AbortTutorial(run,
                    "The tutorial flow could not enter its first step.");
                yield break;
            }
            overlay.ShowUiTargetWithCoachMark(
                Copy.TimedOrdersCountdown,
                BartenderFirstShiftPose.TimedOrdersIntro,
                run.TimerTarget, compact: true,
                panelYOffset: PanelYOffset);
            yield return WaitForAdvanceRequest(
                run, BsTimedOrdersTutorialState.Intro);
            if (!IsRunActive(run)) yield break;
            PlayStepComplete(run);

            if (!tutorialFlow.Advance())
            {
                AbortTutorial(run, "The timed-order introduction could not complete.");
                yield break;
            }

#if UNITY_EDITOR
            if (!run.EditorReplay)
#endif
                BartenderTimedOrdersTutorialProgress.Complete();

            tutorialFlow.CompletionApplied();
            run.TutorialRoutine = null;

#if UNITY_EDITOR
            if (run.EditorReplay)
            {
                TrySettle(run, abortOwnedRound: true, immediate: false);
                yield break;
            }
#endif
            TrySettle(run, abortOwnedRound: false, immediate: true);
            Destroy(this);
        }

        private IEnumerator WaitForAdvanceRequest(
            TutorialRunContext run,
            BsTimedOrdersTutorialState expectedStage)
        {
            int initialVersion = run.AdvanceRequestVersion;
            while (IsRunActive(run)
                   && tutorialFlow.State == expectedStage
                   && ContextIsCurrent(run)
                   && (controller.State == BartenderLevelState.Paused
                       || run.AdvanceRequestVersion == initialVersion))
                yield return null;

            if (!IsRunActive(run)) yield break;
            if (tutorialFlow.State == expectedStage
                && ContextIsCurrent(run)
                && run.AdvanceRequestVersion != initialVersion)
                yield break;
            AbortTutorial(run,
                "The level changed while waiting for tutorial input.");
        }

        private bool TryResolveTargets(TutorialRunContext run)
        {
            if (!IsRunActive(run)) return false;
            ResolveDependencies();
            if (!PresentationHasSettled() || orderStrip == null
                || orderStrip.Cards == null)
                return false;

            run.TimedCard = null;
            for (int i = 0; i < orderStrip.Cards.Count; i++)
            {
                OrderCardView candidate = orderStrip.Cards[i];
                if (candidate == null || candidate.Model == null
                    || candidate.Model.TimeLimit <= 0f
                    || candidate.Rt == null
                    || !candidate.Rt.gameObject.activeInHierarchy
                    || candidate.TimerTarget == null)
                    continue;
                run.TimedCard = candidate;
                break;
            }
            if (run.TimedCard == null) return false;

            run.TimerTarget = run.TimedCard.TimerTarget;
            return run.TimerTarget != null;
        }

        private bool PresentationHasSettled()
        {
            return controller != null
                && controller.State == BartenderLevelState.Playing
                && interaction != null
                && !interaction.Busy
                && shelfView != null
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.SynchronizationDeferred
                && (levelIntro == null || !levelIntro.Playing)
                && (orderStrip == null || !orderStrip.TransitionPlaying)
                && !BartenderLoadingOverlayPresenter.AnyVisible;
        }

        private bool ContextIsCurrent(TutorialRunContext run)
        {
            if (!IsRunActive(run) || controller == null
                || run.ExpectedLevel == null
                || !ReferenceEquals(controller.CurrentLevel, run.ExpectedLevel)
                || (controller.State != BartenderLevelState.Playing
                    && controller.State != BartenderLevelState.Paused)
                || !RoundMatchesContext(run))
                return false;
#if UNITY_EDITOR
            if (run.EditorReplay) return controller.IsStandaloneRound
                && BartenderTimedOrdersTutorialInstaller
                    .IsEditorReplayAuthorized(gameObject);
#endif
            return !controller.IsStandaloneRound;
        }

        private bool EnsureOverlay(TutorialRunContext run)
        {
            if (!IsRunActive(run) || overlay == null
                || !overlay.ValidateAuthoredBindings(out _)) return false;
            if (run.AdvanceRequestedHandler == null)
            {
                run.AdvanceRequestedHandler = () =>
                    HandleAdvanceRequested(run);
                overlay.AdvanceRequested += run.AdvanceRequestedHandler;
            }
            return true;
        }

        private const float StepCompleteVolume = 0.85f;
        private void PlayStepComplete(TutorialRunContext run)
        {
            if (!IsRunActive(run)) return;
            BsAudio audio = BsAudio.Instance;
            if (audio == null) return;
            audio.Play(BsSfx.StepComplete, StepCompleteVolume);
        }

        private void HandleAdvanceRequested(TutorialRunContext run)
        {
            if (ContextIsCurrent(run)
                && controller.State == BartenderLevelState.Playing
                && tutorialFlow.State == BsTimedOrdersTutorialState.Intro)
                run.AdvanceRequestVersion++;
        }

        public bool Allows(BartenderInputRequest request, out string rejectionReason)
        {
            BsTimedOrdersTutorialState state = tutorialFlow.State;
            bool modal = state == BsTimedOrdersTutorialState.Intro;
            rejectionReason = modal
                ? "Tap the tutorial message to continue."
                : null;
            return !modal;
        }

        public void HandleRejected(BartenderInputRequest request,
                                   string rejectionReason) => overlay?.Nudge();

        private void HandleStateChanged(
            TutorialRunContext run,
            BartenderLevelState state)
        {
            if (!IsRunActive(run) || run.ExpectedLevel == null) return;
            UpdatePauseClock(run, state);
            if (state == BartenderLevelState.Playing
                || state == BartenderLevelState.Paused) return;
            AbortTutorial(run,
                "The level stopped while timed-order onboarding was open.");
        }

        private static void UpdatePauseClock(
            TutorialRunContext run, BartenderLevelState state)
        {
            double now = Time.realtimeSinceStartupAsDouble;
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

        private static double PresentationTime(TutorialRunContext run)
        {
            double now = run.PauseStartedAt >= 0d
                ? run.PauseStartedAt
                : Time.realtimeSinceStartupAsDouble;
            return now - run.PausedDuration;
        }

        private void AbortTutorial(TutorialRunContext run, string reason)
        {
            if (!IsRunActive(run) || !tutorialFlow.Abort()) return;
            Debug.LogWarning("[Timed Orders Tutorial] " + reason, this);

#if UNITY_EDITOR
            if (run.EditorReplay)
            {
                TrySettle(run, abortOwnedRound: true, immediate: false);
                return;
            }
#endif
            TrySettle(run, abortOwnedRound: false, immediate: true);
            Destroy(this);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Stop the rehearsal before script reload because its coroutines and replay authorization cannot
        /// survive.
        /// </summary>
        public static void EditorAbortActiveReplaysBeforeAssemblyReload()
        {
            BartenderTimedOrdersTutorialDirector[] directors =
                FindObjectsByType<BartenderTimedOrdersTutorialDirector>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < directors.Length; i++)
            {
                BartenderTimedOrdersTutorialDirector director = directors[i];
                if (director == null || (!director.HasEditorReplayRun
                    && !BartenderTimedOrdersTutorialInstaller
                        .IsEditorReplayAuthorized(director.gameObject)))
                    continue;
                director.Cleanup();
            }
            BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
        }

        private bool HasEditorReplayRun => activeRun != null
            && activeRun.EditorReplay;

        private void StartEditorReplayBoard(TutorialRunContext run)
        {
            if (!IsRunActive(run)) return;
            if (controller == null || interaction == null || shelfView == null
                || orderStrip == null)
            {
                AbortTutorial(run, "The editor replay rig is incomplete.");
                return;
            }

            BsLevel timedLevel = Resources.Load<BsLevel>(TimedLevelResourcePath);
            if (!BartenderTimedOrdersTutorialProgress
                    .IsTimedIntroductionLevel(timedLevel))
            {
                AbortTutorial(run, "Resources/" + TimedLevelResourcePath
                    + " is missing or is not the first timed level.");
                return;
            }
            if (!controller.TryStartStandalone(timedLevel,
                    out string rejectionReason))
            {
                AbortTutorial(run,
                    "The editor replay board could not start: "
                    + rejectionReason);
                return;
            }
            if (!IsRunActive(run)) return;
            run.OwnsStandaloneRound = controller.IsStandaloneRound;
            if (!run.OwnsStandaloneRound || !RoundMatchesContext(run))
                AbortTutorial(run,
                    "The editor replay board did not retain its tutorial context.");
        }

        private IEnumerator SettleEditorReplay(TutorialRunContext run)
        {
            // Start work only after TrySettle stores the handle on this context.
            yield return null;
            if (!OwnsRunContext(run)) yield break;
            if (run.OwnsStandaloneRound && controller != null
                && controller.IsStandaloneRound
                && RoundMatchesContext(run))
            {
                controller.RequestAbortStandalone(out _);
                float deadline = Time.realtimeSinceStartup + 2f;
                while (OwnsRunContext(run) && controller != null
                       && controller.IsStandaloneRound
                       && RoundMatchesContext(run)
                       && Time.realtimeSinceStartup < deadline)
                    yield return null;
            }
            if (!OwnsRunContext(run)) yield break;
            run.OwnsStandaloneRound = false;
            FinishAbortTransitionIfNeeded();
            run.SettlementRoutine = null;
            CompleteSettlement(run);
            UnityEditor.EditorApplication.ExitPlaymode();
        }
#endif

        private void ReleaseOverlay(TutorialRunContext run)
        {
            if (run == null) return;
            Action advanceRequested = run.AdvanceRequestedHandler;
            run.AdvanceRequestedHandler = null;
            if (overlay != null && advanceRequested != null)
                overlay.AdvanceRequested -= advanceRequested;
        }

        private static void ReleaseBarrier(TutorialRunContext run)
        {
            if (run == null || run.BarrierController == null) return;
            BartenderLevelController barrierController = run.BarrierController;
            run.BarrierController = null;
            barrierController.ReleasePresentationBarrier(run);
        }

        private void StopTutorialRoutine(TutorialRunContext run)
        {
            if (run == null || run.TutorialRoutine == null) return;
            Coroutine routine = run.TutorialRoutine;
            run.TutorialRoutine = null;
            StopCoroutine(routine);
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
            && tutorialFlow.State != BsTimedOrdersTutorialState.Finished
            && tutorialFlow.State != BsTimedOrdersTutorialState.Disposed;

        private bool RoundMatchesContext(TutorialRunContext run)
        {
            if (controller == null || run == null || !run.AttemptId.IsValid)
                return false;
            BsRoundCommandStamp stamp = controller.CurrentRoundStamp;
            return stamp.AttemptId == run.AttemptId
                && stamp.Token == run.RoundToken;
        }

        /// <summary>
        /// Releases this run's input, overlay, barrier, coroutines and replay ownership. Repeated calls cannot
        /// touch a later run.
        /// </summary>
        private bool TrySettle(
            TutorialRunContext run,
            bool abortOwnedRound,
            bool immediate)
        {
            if (!OwnsRunContext(run)) return false;
            if (run.SettlementRoutine != null)
            {
                if (immediate)
                {
                    Coroutine settlement = run.SettlementRoutine;
                    run.SettlementRoutine = null;
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
                        FinishAbortTransitionIfNeeded,
                        () => CompleteDetachedSettlement(run));
                    if (reentrantFailure != null)
                        Debug.LogException(reentrantFailure, this);
                }
                return false;
            }

            bool settleAsynchronously = false;
            Exception failure = null;
#if UNITY_EDITOR
            if (run.EditorReplay && !immediate)
            {
                try
                {
                    // Attach the settling handle before cleanup callbacks; SettleEditorReplay yields before
                    // domain work.
                    Coroutine settlement = StartCoroutine(SettleEditorReplay(run));
                    if (settlement != null && OwnsRunContext(run))
                    {
                        run.SettlementRoutine = settlement;
                        settleAsynchronously = true;
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }
#endif

            if (!settleAsynchronously)
                activeRun = null;

            Exception releaseFailure = ExecuteRunCleanup(
                () => StopTutorialRoutine(run),
                () => interaction?.ClearInputPolicy(this),
                () => overlay?.HideImmediate(),
                () => ReleaseOverlay(run),
                () => ReleaseBarrier(run),
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
                    if (abortOwnedRound && run.OwnsStandaloneRound
                        && controller != null && controller.IsStandaloneRound
                        && RoundMatchesContext(run))
                        controller.RequestAbortStandalone(out _);
                },
                () => run.OwnsStandaloneRound = false,
                FinishAbortTransitionIfNeeded,
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

        private void FinishAbortTransitionIfNeeded()
        {
            if (tutorialFlow.IsAborting) tutorialFlow.AbortCompleted();
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
            run.ExpectedLevel = null;
            run.SettlementRoutine = null;
#if UNITY_EDITOR
            if (run.EditorReplay)
                BartenderTimedOrdersTutorialInstaller
                    .ReleaseEditorReplayAuthorization(gameObject);
#endif
        }

        private string PresentationBlockerSummary()
        {
            return $"intro={(levelIntro != null && levelIntro.Playing)}, "
                 + $"interactionBusy={(interaction != null && interaction.Busy)}, "
                 + $"shelfReady={(shelfView != null && shelfView.Ready)}, "
                 + $"seat={(shelfView != null && shelfView.SeatAnimationPlaying)}, "
                 + $"syncDeferred={(shelfView != null && shelfView.SynchronizationDeferred)}, "
                 + $"orders={(orderStrip != null && orderStrip.TransitionPlaying)}, "
                 + $"loading={BartenderLoadingOverlayPresenter.AnyVisible}";
        }

        private void ResolveDependencies()
        {
            if (interaction == null) interaction = GetComponent<BartenderPourInteraction>();
            if (controller == null && interaction != null)
                controller = interaction.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (shelfView == null && interaction != null)
                shelfView = interaction.ShelfView;
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (orderStrip == null) orderStrip = GetComponent<OrderStripPresenter>();
            if (levelIntro == null) levelIntro = GetComponent<BartenderLevelIntroDirector>();
        }

        private void Subscribe(TutorialRunContext run)
        {
            if (!OwnsRunContext(run) || controller == null) return;
            run.LevelLoadedHandler = level => HandleLevelLoaded(run, level);
            run.StateChangedHandler = state => HandleStateChanged(run, state);
            controller.LevelLoaded += run.LevelLoadedHandler;
            controller.StateChanged += run.StateChangedHandler;
        }

        private void Unsubscribe(TutorialRunContext run)
        {
            if (run == null) return;
            Action<BsLevel> levelLoaded = run.LevelLoadedHandler;
            Action<BartenderLevelState> stateChanged = run.StateChangedHandler;
            run.LevelLoadedHandler = null;
            run.StateChangedHandler = null;

            Exception failure = ExecuteRunCleanup(
                controller != null && levelLoaded != null
                    ? (Action)(() => controller.LevelLoaded -= levelLoaded)
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
#if UNITY_EDITOR
            if (run == null)
                BartenderTimedOrdersTutorialInstaller
                    .ReleaseEditorReplayAuthorization(gameObject);
#endif
        }
    }

    /// <summary>
    /// Adds the director to the live rig. An authorized Editor replay replaces normal startup with a standalone
    /// level 15 board.
    /// </summary>
    internal static class BartenderTimedOrdersTutorialInstaller
    {
        private static bool sceneHooked;
#if UNITY_EDITOR
        private static bool editorReplayPending;
        private static string pendingSceneName;
        private static int authorizedSceneHandle = -1;
        private static int authorizedHostInstanceId;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            sceneHooked = false;
#if UNITY_EDITOR
            editorReplayPending = false;
            pendingSceneName = null;
            authorizedSceneHandle = -1;
            authorizedHostInstanceId = 0;
            if (BartenderTimedOrdersTutorialProgress
                    .EditorTryConsumeReplayScene(out string sceneName))
            {
                pendingSceneName = NormalizeSceneName(sceneName);
                editorReplayPending = !string.IsNullOrWhiteSpace(pendingSceneName);
            }
            else if (BartenderTimedOrdersTutorialProgress.EditorReplayActive)
            {
                // The consumed request means reload interrupted a replay. Its coroutines cannot safely resume.
                BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
                UnityEditor.EditorApplication.ExitPlaymode();
            }
#endif
            EnsureSceneHook();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallForLoadedScenes()
        {
            EnsureSceneHook();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                InstallInScene(SceneManager.GetSceneAt(i));
        }

        private static void EnsureSceneHook()
        {
            if (sceneHooked) return;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            sceneHooked = true;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode _)
        {
            InstallInScene(scene);
#if UNITY_EDITOR
            if (editorReplayPending && SceneMatchesPendingReplay(scene))
            {
                Debug.LogWarning("[Timed Orders Tutorial] The requested gameplay scene "
                    + "loaded without a complete Bartender rig; replay was cancelled.");
                editorReplayPending = false;
                pendingSceneName = null;
                BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
                UnityEditor.EditorApplication.ExitPlaymode();
            }
#endif
        }

        private static void InstallInScene(Scene scene)
        {
            if (!Application.isPlaying || !scene.IsValid() || !scene.isLoaded) return;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                BartenderTimedOrdersTutorialDirector director =
                    roots[i].GetComponentInChildren<
                        BartenderTimedOrdersTutorialDirector>(true);
                if (director == null || !director.HasAuthoredBindings)
                    continue;

#if UNITY_EDITOR
                bool authorizeReplay = editorReplayPending
                    && SceneMatchesPendingReplay(scene);
                if (authorizeReplay)
                    director.Controller.DisableAutomaticLoadAtRuntime();
#endif
#if UNITY_EDITOR
                if (authorizeReplay)
                {
                    editorReplayPending = false;
                    pendingSceneName = null;
                    authorizedSceneHandle = scene.handle;
                    authorizedHostInstanceId = director.gameObject.GetInstanceID();
                    return;
                }
#endif
            }
        }

#if UNITY_EDITOR
        internal static bool IsEditorReplayAuthorized(GameObject host)
        {
            if (host == null || authorizedHostInstanceId == 0) return false;
            Scene scene = host.scene;
            return scene.IsValid()
                && scene.handle == authorizedSceneHandle
                && host.GetInstanceID() == authorizedHostInstanceId;
        }

        internal static void ReleaseEditorReplayAuthorization(GameObject host)
        {
            if (!IsEditorReplayAuthorized(host)) return;
            authorizedSceneHandle = -1;
            authorizedHostInstanceId = 0;
            BartenderTimedOrdersTutorialProgress.EditorCancelReplay();
        }

        private static bool SceneMatchesPendingReplay(Scene scene) =>
            scene.IsValid() && scene.isLoaded
            && !string.IsNullOrWhiteSpace(pendingSceneName)
            && string.Equals(scene.name, pendingSceneName,
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
#endif
    }
}
