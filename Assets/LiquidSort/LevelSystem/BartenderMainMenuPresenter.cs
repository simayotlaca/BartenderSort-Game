using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Shows campaign, economy and settings state through the menu prefab's Inspector-linked views.</summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class BartenderMainMenuPresenter : MonoBehaviour
    {
        internal enum GameLaunchOrigin
        {
            LevelButton,
            AutomaticFirstShift,
        }

        internal enum LaunchFlowState
        {
            Idle,
            Preparing,
            LoadingScene,
            LoadingSceneSlow,
            StartingEmbedded,
            AwaitingActivation,
            Settling,
            Completed,
            Failed,
            Cancelled,
        }

        /// <summary>
        /// Owns one menu launch. Its fixed identity and one-time Unity handles are released together through
        /// the matching cleanup path.
        /// </summary>
        internal sealed class LaunchRunContext
        {
            internal LaunchRunContext(
                long operationId,
                GameLaunchOrigin origin,
                string sceneName,
                BartenderLoadingOverlayPresenter loadingCover,
                BartenderFirstShiftLaunchRequest firstShiftRequest)
            {
                OperationId = operationId;
                Origin = origin;
                SceneName = sceneName;
                LoadingCover = loadingCover;
                FirstShiftRequest = firstShiftRequest;
                State = LaunchFlowState.Idle;
            }

            internal long OperationId { get; }
            internal GameLaunchOrigin Origin { get; }
            internal string SceneName { get; }
            internal BartenderLoadingOverlayPresenter LoadingCover { get; }
            internal BartenderFirstShiftLaunchRequest FirstShiftRequest { get; }
            internal LaunchFlowState State { get; set; }
            internal Coroutine Routine { get; private set; }
            internal bool AdvancingRoutine { get; set; }
            internal AsyncOperation SceneOperation { get; private set; }
            internal BartenderLevelController BarrierController { get; private set; }
            internal BartenderShelfLevelView CoveredShelf { get; private set; }
            internal int LoadingPresentationVersion { get; set; }
            internal bool OwnsLoadingCover => LoadingCover != null
                && LoadingCover.IsCurrentPresentation(LoadingPresentationVersion);

            private CanvasGroup heldMenuInput;
            private bool menuBlockedRaycasts;
            private EventSystem heldNavigation;
            private bool navigationWasEnabled;
            private IDisposable musicSuspension;

            internal void HoldBackgroundMusic()
            {
                if (musicSuspension == null)
                    musicSuspension = BsAudio.SuspendBackgroundMusic();
            }

            internal IDisposable TakeMusicSuspension()
            {
                IDisposable suspension = musicSuspension;
                musicSuspension = null;
                return suspension;
            }

            internal void HoldMenuInput(GameObject root)
            {
                if (root == null || heldMenuInput != null) return;
                heldMenuInput = root.GetComponent<CanvasGroup>();
                if (heldMenuInput == null) heldMenuInput = root.AddComponent<CanvasGroup>();
                menuBlockedRaycasts = heldMenuInput.blocksRaycasts;
                // Keep the menu's visual state while suppressing pointer and keyboard/gamepad input.
                heldMenuInput.blocksRaycasts = false;
                heldNavigation = EventSystem.current;
                if (heldNavigation != null)
                {
                    navigationWasEnabled = heldNavigation.sendNavigationEvents;
                    heldNavigation.sendNavigationEvents = false;
                }
            }

            internal void ReleaseMenuInput()
            {
                CanvasGroup group = heldMenuInput;
                EventSystem navigation = heldNavigation;
                heldMenuInput = null;
                heldNavigation = null;
                if (group != null) group.blocksRaycasts = menuBlockedRaycasts;
                if (navigation != null) navigation.sendNavigationEvents = navigationWasEnabled;
            }

            internal bool UsesExternalScene =>
                !string.IsNullOrWhiteSpace(SceneName);

            internal bool TryAttachRoutine(Coroutine routine)
            {
                if (routine == null || Routine != null) return false;
                Routine = routine;
                return true;
            }

            internal Coroutine TakeRoutine()
            {
                Coroutine routine = Routine;
                Routine = null;
                return routine;
            }

            internal bool TryAttachSceneOperation(AsyncOperation operation)
            {
                if (operation == null || SceneOperation != null) return false;
                SceneOperation = operation;
                return true;
            }

            internal AsyncOperation TakeSceneOperation()
            {
                AsyncOperation operation = SceneOperation;
                SceneOperation = null;
                return operation;
            }

            internal bool TryAttachBarrier(BartenderLevelController owner)
            {
                if (owner == null || BarrierController != null) return false;
                BarrierController = owner;
                return true;
            }

            internal BartenderLevelController TakeBarrier()
            {
                BartenderLevelController owner = BarrierController;
                BarrierController = null;
                return owner;
            }

            internal void TrackCoveredShelf(
                BartenderShelfLevelView shelf, bool armed)
            {
                CoveredShelf = armed ? shelf : null;
            }

            internal BartenderShelfLevelView TakeCoveredShelf()
            {
                BartenderShelfLevelView shelf = CoveredShelf;
                CoveredShelf = null;
                return shelf;
            }
        }

        /// <summary>
        /// Allows one launch at a time with unique operation IDs. Only the current launch context can change
        /// state.
        /// </summary>
        internal sealed class LaunchFlowStateMachine
        {
            private long lastOperationId;

            internal LaunchRunContext Current { get; private set; }
            internal LaunchFlowState State => Current != null
                ? Current.State
                : LaunchFlowState.Idle;
            internal bool IsActive => IsOccupiedState(State);

            internal bool TryBegin(
                GameLaunchOrigin origin,
                string sceneName,
                BartenderLoadingOverlayPresenter loadingCover,
                BartenderFirstShiftLaunchRequest firstShiftRequest,
                out LaunchRunContext context)
            {
                context = null;
                if (IsActive) return false;
                if (lastOperationId == long.MaxValue) return false;

                lastOperationId++;
                context = new LaunchRunContext(
                    lastOperationId,
                    origin,
                    sceneName,
                    loadingCover,
                    firstShiftRequest);
                Current = context;
                return TryMove(context, LaunchFlowState.Idle,
                    LaunchFlowState.Preparing);
            }

            internal bool Owns(LaunchRunContext context) =>
                context != null
                && ReferenceEquals(Current, context)
                && context.OperationId != 0L;

            internal bool OwnsActive(LaunchRunContext context) =>
                Owns(context) && IsRunningState(context.State);

            internal bool TryEnterLaunchPath(LaunchRunContext context)
            {
                if (!OwnsActive(context)
                    || context.State != LaunchFlowState.Preparing)
                    return false;
                context.State = context.UsesExternalScene
                    ? LaunchFlowState.LoadingScene
                    : LaunchFlowState.StartingEmbedded;
                return true;
            }

            internal bool TryAwaitActivation(LaunchRunContext context)
            {
                if (!OwnsActive(context)
                    || (context.State != LaunchFlowState.LoadingScene
                        && context.State != LaunchFlowState.LoadingSceneSlow
                        && context.State != LaunchFlowState.StartingEmbedded))
                    return false;
                context.State = LaunchFlowState.AwaitingActivation;
                return true;
            }

            /// <summary>
            /// Unity cannot cancel a scene load. A timeout keeps ownership and blocks another launch until
            /// handoff is possible.
            /// </summary>
            internal bool TryMarkSceneLoadSlow(LaunchRunContext context)
            {
                if (!OwnsActive(context)) return false;
                if (context.State == LaunchFlowState.LoadingSceneSlow) return true;
                if (context.State != LaunchFlowState.LoadingScene) return false;
                context.State = LaunchFlowState.LoadingSceneSlow;
                return true;
            }

            internal bool TryBeginSettle(
                LaunchRunContext context, LaunchFlowState terminalState)
            {
                if (!OwnsActive(context)
                    || context.State == LaunchFlowState.Settling
                    || !IsTerminalState(terminalState))
                    return false;
                context.State = LaunchFlowState.Settling;
                return true;
            }

            internal bool TryFinishSettle(
                LaunchRunContext context, LaunchFlowState terminalState)
            {
                if (!Owns(context)
                    || context.State != LaunchFlowState.Settling
                    || !IsTerminalState(terminalState))
                    return false;
                context.State = terminalState;
                return true;
            }

            private bool TryMove(
                LaunchRunContext context,
                LaunchFlowState expected,
                LaunchFlowState next)
            {
                if (!Owns(context) || context.State != expected) return false;
                context.State = next;
                return true;
            }

            private static bool IsRunningState(LaunchFlowState state) =>
                state == LaunchFlowState.Preparing
                || state == LaunchFlowState.LoadingScene
                || state == LaunchFlowState.LoadingSceneSlow
                || state == LaunchFlowState.StartingEmbedded
                || state == LaunchFlowState.AwaitingActivation;

            private static bool IsOccupiedState(LaunchFlowState state) =>
                IsRunningState(state) || state == LaunchFlowState.Settling;

            private static bool IsTerminalState(LaunchFlowState state) =>
                state == LaunchFlowState.Completed
                || state == LaunchFlowState.Failed
                || state == LaunchFlowState.Cancelled;
        }

        private const float AuthoredPopupReferenceWidth = 720f;
        private const float LifeRegainedVolume = 0.80f;
        private const float LifeRefillVolume = 0.90f;
        private const float LevelButtonPauseAfterSoundSeconds = 0.03f;
        internal const double SceneLoadWaitTimeoutSeconds = 45d;
        internal const double CoveredShelfWaitTimeoutSeconds = 8d;

        private static readonly CultureInfo EnglishCulture =
            CultureInfo.GetCultureInfo("en-US");
        private static bool firstShiftAutoStartAttemptedThisSession;
        // Unity cannot cancel an async load. Keep it authoritative even if its presenter is disabled
        // or finishes releasing its visual resources before activation destroys the old menu.
        private static AsyncOperation outstandingSceneLoad;

        private readonly BsPurchaseOverlayStateMachine moreLivesFlow =
            new BsPurchaseOverlayStateMachine();
        private readonly LaunchFlowStateMachine launchFlow =
            new LaunchFlowStateMachine();

        [Header("Scene owner")]
        [Tooltip("Uses a component under the same rig if empty.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("LEVEL opens this scene when set.")]
        [SerializeField] private string gameplaySceneName;

        [Header("Serialized hierarchy")]
        [SerializeField] private GameObject menuRoot;
        [SerializeField] private RectTransform layoutAreaRect;
        [SerializeField] private RectTransform primaryActionRect;
        [SerializeField] private RectTransform noticeRect;
        [SerializeField] private GameObject homePage;
        [SerializeField] private GameObject shopPage;
        [SerializeField] private GameObject recipePage;
        [SerializeField] private GameObject settingsOverlay;
        [SerializeField] private RectTransform settingsCardRect;
        [SerializeField] private Canvas menuCanvas;
        [SerializeField] private BartenderLoadingOverlayPresenter loadingOverlay;

        [Header("Top HUD")]
        [SerializeField] private Text lifeCountLabel;
        [SerializeField] private Text lifeTimerLabel;
        [SerializeField] private Text coinLabel;
        [SerializeField] private Image coinHudIcon;
        [SerializeField] private Button addLifeButton;
        [SerializeField] private Button addLifeBadgeButton;
        [SerializeField] private Button addCoinButton;
        [SerializeField] private Button addCoinBadgeButton;
        [SerializeField] private Button settingsButton;

        [Header("Primary action")]
        [SerializeField] private Button playButton;
        [SerializeField] private Text playLabel;
        [SerializeField] private BartenderMainMenuLevelButtonView levelButtonView;
        [SerializeField] private Text noticeLabel;

        [Header("More Lives")]
        [SerializeField] private Button closeMoreLivesButton;
        [SerializeField] private Button refillLivesButton;
        [SerializeField] private Button rewardedLifeButton;
        [SerializeField] private Text moreLivesCountLabel;
        [SerializeField] private Text moreLivesTimerLabel;
        [SerializeField] private Text moreLivesCoinBalanceLabel;
        [SerializeField] private Text moreLivesRefillCostLabel;
        [SerializeField] private Text moreLivesFeedbackLabel;
        [Tooltip("Inactive authored BartenderMoreLivesPopup instance under this menu.")]
        [SerializeField] private BartenderMoreLivesPopupView authoredMoreLivesView;

        [Header("Home reward")]
        [SerializeField] private BartenderHomeRewardPresenter homeRewardPresenter;

        [Header("Bottom navigation")]
        [SerializeField] private RectTransform bottomNavigationRect;
        [SerializeField] private BartenderBottomNavPresenter bottomNavigationPresenter;
        [SerializeField] private Button shopButton;
        [SerializeField] private Button homeButton;
        [SerializeField] private Button recipesButton;

        [Header("Settings")]
        [SerializeField] private Button closeSettingsButton;

        [Header("Settings sliders and switches")]
        [Tooltip("Music/Audio row volume. Zero also disables the channel.")]
        [SerializeField] private Slider musicVolumeSlider;
        [Tooltip("SFX row volume. Zero also disables the channel.")]
        [SerializeField] private Slider soundVolumeSlider;
        [SerializeField] private Toggle vibrationToggle;
        [SerializeField] private Toggle notificationsToggle;
        [Tooltip("Optional full-screen backdrop that closes the popup.")]
        [SerializeField] private Button settingsBackdropButton;
        [SerializeField] private Button accountInfoButton;
        [SerializeField] private Button helpSupportButton;

        [Header("Settings integration hooks")]
        [Tooltip("Connect an account screen here when that screen is available.")]
        [SerializeField] private UnityEvent accountInfoRequested = new UnityEvent();
        [Tooltip("Connect the support flow here when its destination is available.")]
        [SerializeField] private UnityEvent helpSupportRequested = new UnityEvent();

        private BartenderLevelController subscribedController;
        private bool buttonsHooked;
        private int lastKnownLives = -1;
        private Sequence noticeTween;
        private bool noticeRestPoseCaptured;
        private Vector2 noticeRestAnchoredPosition;
        private Vector3 noticeRestScale;
        private float noticeRestAlpha = 1f;
        private float lastLayoutHeight = float.NaN;
        private bool authoredMoreLivesPrepared;
        private bool authoredMoreLivesErrorReported;
        private bool homeRewardErrorReported;
        private bool levelButtonViewErrorReported;
        private bool loadingOverlayErrorReported;
        private bool dailyRewardCoinProjectionHeld;
        private bool menuStartupFinished;
        private bool menuMusicPending;

        public bool Visible => menuRoot != null && menuRoot.activeSelf;
        private bool LaunchInProgress => launchFlow.IsActive
            || (outstandingSceneLoad != null && !outstandingSceneLoad.isDone);
        internal bool CanUseMenuFeatures => isActiveAndEnabled && Visible && !LaunchInProgress
            && !BartenderStartupIntro.IsPlaying
            && (homeRewardPresenter == null || !homeRewardPresenter.IsPresenting);
        internal bool CanUseHomeFeatures => CanUseMenuFeatures
            && (homePage == null || homePage.activeInHierarchy);
        internal bool CanShowHomeIntroduction => CanUseHomeFeatures && menuStartupFinished
            && !BartenderHomeSceneTransition.IsHoldingResultFrame
            && (settingsOverlay == null || !settingsOverlay.activeInHierarchy)
            && moreLivesFlow.State == BsPurchaseOverlayState.Hidden;
        internal Transform FeaturePageParent => homePage != null
            ? homePage.transform.parent : menuRoot != null ? menuRoot.transform : transform;
        internal RectTransform FeaturePageTemplate => homePage != null
            ? homePage.transform as RectTransform : null;
        internal Sprite PrimaryActionSprite => playButton != null && playButton.image != null
            ? playButton.image.sprite : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeSessionState()
        {
            firstShiftAutoStartAttemptedThisSession = false;
            outstandingSceneLoad = null;
        }

        private void Awake()
        {
            // Guard hand-authored variants too. The production scene already saves loadOnStart=false.
            if (controller != null) controller.DisableAutomaticLoadAtRuntime();
            PrepareAuthoredLoadingOverlay();
            BartenderDailyOrdersPresenter.Ensure(this);
            BartenderRecipeBookPresenter.Ensure(this);
        }

        /// <summary>Daily orders use the same guarded scene launch as the campaign button.</summary>
        internal void StartGameFromDailyOrders() =>
            StartGame(GameLaunchOrigin.LevelButton);

        internal bool TryStartRecipeReplay(int campaignSlot, out string rejectionReason)
        {
            rejectionReason = null;
            if (!CanUseMenuFeatures)
            {
                rejectionReason = "Wait for the menu to be ready";
                return false;
            }
            if (!BartenderLevelController.CanReplayCampaignSlot(campaignSlot))
            {
                rejectionReason = "Complete this level in the campaign first";
                return false;
            }
            if (!BartenderProgressService.TrySelectReplay(campaignSlot, out rejectionReason))
                return false;
            StartGame(GameLaunchOrigin.LevelButton);
            return true;
        }

        private void OnEnable()
        {
            if (controller != null) controller.DisableAutomaticLoadAtRuntime();
            EnsureAuthoredMoreLivesView();
            EnsureHomeRewardPresenter();
            HookButtons();

            // Read lives before subscribing so the first refresh cannot play a refill sound.
            lastKnownLives = BartenderProgressService.Lives;
            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
            BartenderProgressService.LivesChanged += HandleLivesChanged;
            BartenderProgressService.LifeTimerChanged += HandleLifeTimerChanged;
            BartenderProgressService.ProgressChanged += HandleProgressChanged;
            BartenderSettingsStore.SettingsChanged += HandleSettingsChanged;
            SubscribeController();

            ProjectControllerState();
            RefreshAll();
            RefreshResponsiveLayout(true);
            TryAutoStartFirstShift();
        }

        private IEnumerator Start()
        {
            // First Shift and pending rewards must not begin underneath the startup signature.
            while (BartenderStartupIntro.IsPlaying) yield return null;
            TryAutoStartFirstShift();
            menuStartupFinished = true;
            menuMusicPending = true;
            // Select the menu track even beneath a handoff cover. Its suspension releases playback
            // only when that destination is ready and visible.
            RestoreMenuMusicIfReady();

            // I present the pending reward in Start so every reward component has finished enabling.
            if (menuRoot != null && menuRoot.activeSelf)
                TryPresentHomeReward();
        }

        private void OnDisable()
        {
            // The saved visual receipt survives; a later menu entry can present it.
            dailyRewardCoinProjectionHeld = false;
            // Settle launch ownership first, then try every other cleanup even if one collaborator throws.
            Exception cleanupFailure = RunLaunchCleanupSteps(
                CancelStartGameTransition,
                HideNoticeImmediate,
                ExitShopPresentation,
                UnsubscribeHomeRewardPresenter,
                () => homeRewardPresenter?.CancelAndSnap(),
                ResetMoreLives,
                () => BartenderProgressService.CoinsChanged -= HandleCoinsChanged,
                () => BartenderProgressService.LivesChanged -= HandleLivesChanged,
                () => BartenderProgressService.LifeTimerChanged -= HandleLifeTimerChanged,
                () => BartenderProgressService.ProgressChanged -= HandleProgressChanged,
                () => BartenderSettingsStore.SettingsChanged -= HandleSettingsChanged,
                UnsubscribeController,
                UnhookButtons);
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
        }

        private void OnDestroy()
        {
            Exception cleanupFailure = RunLaunchCleanupSteps(
                CancelStartGameTransition,
                HideNoticeImmediate);
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
        }

        private void Update()
        {
            RefreshResponsiveLayout();
            RestoreMenuMusicIfReady();
        }

        private void RestoreMenuMusicIfReady()
        {
            if (!menuMusicPending || !menuStartupFinished || !isActiveAndEnabled
                || menuRoot == null || !menuRoot.activeInHierarchy || LaunchInProgress
                || BartenderStartupIntro.IsPlaying || BsAudio.Instance == null)
                return;

            menuMusicPending = false;
            BsAudio.Instance.StartBgm(BsBgm.Menu);
            BsAudio.Instance.RestoreBgmAfterResult();
        }

        private void SubscribeController()
        {
            if (subscribedController == controller) return;
            UnsubscribeController();
            subscribedController = controller;
            if (subscribedController != null)
                subscribedController.StateChanged += HandleControllerStateChanged;
        }

        private void UnsubscribeController()
        {
            if (subscribedController != null)
                subscribedController.StateChanged -= HandleControllerStateChanged;
            subscribedController = null;
        }

        private void HookButtons()
        {
            if (buttonsHooked) return;
            if (playButton != null) playButton.onClick.AddListener(StartGame);
            if (shopButton != null) shopButton.onClick.AddListener(ShowShopNotice);
            if (homeButton != null) homeButton.onClick.AddListener(SelectHome);
            if (recipesButton != null)
                recipesButton.onClick.AddListener(SelectRecipes);
            if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
            if (addLifeButton != null)
                addLifeButton.onClick.AddListener(ShowLifeStoreNotice);
            if (addLifeBadgeButton != null)
                addLifeBadgeButton.onClick.AddListener(ShowLifeStoreNotice);
            if (closeMoreLivesButton != null)
                closeMoreLivesButton.onClick.AddListener(CloseMoreLives);
            if (refillLivesButton != null)
                refillLivesButton.onClick.AddListener(RefillLives);
            if (addCoinButton != null)
                addCoinButton.onClick.AddListener(ShowCoinStoreNotice);
            if (addCoinBadgeButton != null)
                addCoinBadgeButton.onClick.AddListener(ShowCoinStoreNotice);
            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.AddListener(SetMusicVolume);
            if (soundVolumeSlider != null)
                soundVolumeSlider.onValueChanged.AddListener(SetSoundVolume);
            if (vibrationToggle != null)
                vibrationToggle.onValueChanged.AddListener(SetVibrationEnabled);
            if (notificationsToggle != null)
                notificationsToggle.onValueChanged.AddListener(
                    SetNotificationsEnabled);
            if (settingsBackdropButton != null)
                settingsBackdropButton.onClick.AddListener(CloseSettings);
            if (accountInfoButton != null)
                accountInfoButton.onClick.AddListener(RequestAccountInfo);
            if (helpSupportButton != null)
                helpSupportButton.onClick.AddListener(RequestHelpAndSupport);
            if (closeSettingsButton != null)
                closeSettingsButton.onClick.AddListener(CloseSettings);
            buttonsHooked = true;
        }

        private void UnhookButtons()
        {
            if (!buttonsHooked) return;
            if (playButton != null) playButton.onClick.RemoveListener(StartGame);
            if (shopButton != null) shopButton.onClick.RemoveListener(ShowShopNotice);
            if (homeButton != null) homeButton.onClick.RemoveListener(SelectHome);
            if (recipesButton != null)
                recipesButton.onClick.RemoveListener(SelectRecipes);
            if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
            if (addLifeButton != null)
                addLifeButton.onClick.RemoveListener(ShowLifeStoreNotice);
            if (addLifeBadgeButton != null)
                addLifeBadgeButton.onClick.RemoveListener(ShowLifeStoreNotice);
            if (closeMoreLivesButton != null)
                closeMoreLivesButton.onClick.RemoveListener(CloseMoreLives);
            if (refillLivesButton != null)
                refillLivesButton.onClick.RemoveListener(RefillLives);
            if (addCoinButton != null)
                addCoinButton.onClick.RemoveListener(ShowCoinStoreNotice);
            if (addCoinBadgeButton != null)
                addCoinBadgeButton.onClick.RemoveListener(ShowCoinStoreNotice);
            if (musicVolumeSlider != null)
                musicVolumeSlider.onValueChanged.RemoveListener(SetMusicVolume);
            if (soundVolumeSlider != null)
                soundVolumeSlider.onValueChanged.RemoveListener(SetSoundVolume);
            if (vibrationToggle != null)
                vibrationToggle.onValueChanged.RemoveListener(SetVibrationEnabled);
            if (notificationsToggle != null)
                notificationsToggle.onValueChanged.RemoveListener(
                    SetNotificationsEnabled);
            if (settingsBackdropButton != null)
                settingsBackdropButton.onClick.RemoveListener(CloseSettings);
            if (accountInfoButton != null)
                accountInfoButton.onClick.RemoveListener(RequestAccountInfo);
            if (helpSupportButton != null)
                helpSupportButton.onClick.RemoveListener(RequestHelpAndSupport);
            if (closeSettingsButton != null)
                closeSettingsButton.onClick.RemoveListener(CloseSettings);
            buttonsHooked = false;
        }

        /// <summary>The LEVEL button opens loading here. Automatic First Shift uses the same load without a cover.</summary>
        private void StartGame() => StartGame(GameLaunchOrigin.LevelButton);

        private void StartGame(GameLaunchOrigin origin)
        {
            if (!isActiveAndEnabled || LaunchInProgress || BartenderStartupIntro.IsPlaying
                || (homeRewardPresenter != null && homeRewardPresenter.IsPresenting))
                return;
            BartenderProgressService.Refresh();
            if (!BartenderProgressService.IsAvailable)
            {
                ShowNotice("PLAYER PROGRESS COULD NOT BE LOADED");
                return;
            }
            bool loadsGameplayScene = !string.IsNullOrWhiteSpace(gameplaySceneName);
            bool launchesFirstShift = loadsGameplayScene
                && BartenderFirstShiftProgress.ShouldStart;
            if (!loadsGameplayScene)
            {
                SubscribeController();
                if (controller == null)
                {
                    ShowNotice("GAME SCENE IS NOT READY");
                    return;
                }
            }

            // First Shift is outside the campaign and therefore does not require a life.
            if (!launchesFirstShift && BartenderProgressService.Lives <= 0)
            {
                OpenMoreLives();
                return;
            }

            CloseSettings();
            CloseMoreLives();
            bool showLoadingOverlay = ShowsLoadingOverlay(origin);
            if (showLoadingOverlay) ReportMissingLoadingOverlay();

            // Only this exact launch can authorize First Shift in gameplay. Automatic launch and manual
            // PLAY retry share this path.
            BartenderFirstShiftLaunchRequest firstShiftRequest = default;
            if (launchesFirstShift)
            {
                if (!BartenderFirstShiftInstaller.RequestLaunch(
                        gameplaySceneName, out firstShiftRequest))
                {
                    ShowNotice("FIRST SHIFT IS NOT READY");
                    return;
                }
            }

            BartenderLoadingOverlayPresenter launchCover = showLoadingOverlay
                ? loadingOverlay
                : null;
            if (!launchFlow.TryBegin(
                    origin,
                    gameplaySceneName,
                    launchCover,
                    firstShiftRequest,
                    out LaunchRunContext context))
            {
                BartenderFirstShiftInstaller.CancelPendingLaunch(firstShiftRequest);
                return;
            }

            if (playButton != null) playButton.interactable = false;
            Coroutine routine = null;
            try
            {
                context.HoldBackgroundMusic();
                routine = StartCoroutine(RunGuardedLaunch(context));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                TrySettleLaunch(
                    context,
                    LaunchFlowState.Failed,
                    "GAME SCENE COULD NOT BE LOADED");
                return;
            }
            if (!launchFlow.OwnsActive(context)) return;
            if (!context.TryAttachRoutine(routine))
                TrySettleLaunch(
                    context,
                    LaunchFlowState.Failed,
                    "GAME SCENE COULD NOT BE LOADED");
        }

        internal static bool ShowsLoadingOverlay(GameLaunchOrigin origin) =>
            origin == GameLaunchOrigin.LevelButton;

        // Unity logs iterator exceptions and stops the coroutine without returning to StartGame's
        // catch block. Drive our nested iterators here so every such failure releases this exact run.
        private IEnumerator RunGuardedLaunch(LaunchRunContext context)
        {
            var pending = new Stack<IEnumerator>();
            pending.Push(StartGameRoutine(context));
            try
            {
                while (pending.Count > 0 && launchFlow.OwnsActive(context))
                {
                    IEnumerator current = pending.Peek();
                    object next = null;
                    bool moved = false;
                    Exception failure = null;
                    try
                    {
                        context.AdvancingRoutine = true;
                        moved = current.MoveNext();
                        if (moved) next = current.Current;
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { context.AdvancingRoutine = false; }

                    if (failure != null)
                    {
                        Debug.LogException(failure, this);
                        context.AdvancingRoutine = true;
                        try
                        {
                            TrySettleLaunch(context, LaunchFlowState.Failed,
                                "GAME SCENE COULD NOT BE LOADED", true);
                        }
                        finally { context.AdvancingRoutine = false; }
                        yield break;
                    }
                    if (!moved)
                    {
                        pending.Pop();
                        DisposeLaunchIterator(current);
                        continue;
                    }
                    if (next is IEnumerator nested && !(next is CustomYieldInstruction))
                    {
                        pending.Push(nested);
                        continue;
                    }
                    yield return next;
                }
            }
            finally
            {
                context.TakeRoutine();
                while (pending.Count > 0) DisposeLaunchIterator(pending.Pop());
                if (launchFlow.OwnsActive(context))
                    TrySettleLaunch(context, LaunchFlowState.Cancelled);
            }
        }

        private void DisposeLaunchIterator(IEnumerator iterator)
        {
            try { (iterator as IDisposable)?.Dispose(); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private IEnumerator StartGameRoutine(LaunchRunContext context)
        {
            if (context.Origin == GameLaunchOrigin.LevelButton)
            {
                context.HoldMenuInput(menuCanvas != null ? menuCanvas.gameObject : menuRoot);
                // Submit/onClick can start its sound after this listener. Pointer taps already started it
                // on press, so sample the remaining voice on the next frame instead of replaying the clip.
                yield return null;
                if (!launchFlow.OwnsActive(context)) yield break;
                BsButtonSound buttonSound = playButton != null
                    ? playButton.GetComponent<BsButtonSound>() : null;
                float soundRemaining = buttonSound != null && buttonSound.EnableClickSound
                    && BsAudio.Instance != null
                    ? BsAudio.Instance.RemainingPlaybackSeconds(buttonSound.ClickSound) : 0f;
                yield return new WaitForSecondsRealtime(
                    soundRemaining + LevelButtonPauseAfterSoundSeconds);
                if (!launchFlow.OwnsActive(context)) yield break;
                context.ReleaseMenuInput();
                if (!launchFlow.OwnsActive(context)) yield break;
            }

            string rejectionReason = null;
            bool started = false;
            // Scene loads keep the cover until loading finishes. In-scene loads use the overlay's fixed
            // opening hold.
            bool overlayVisible = false;
            if (context.LoadingCover != null)
            {
                // Reserve the next version before Begin can invoke Animator/enable callbacks. A
                // reentrant cancellation can then release this cover without touching a newer one.
                context.LoadingPresentationVersion = unchecked(
                    context.LoadingCover.PresentationVersion + 1);
                overlayVisible = context.LoadingCover.Begin(context.UsesExternalScene)
                    && context.OwnsLoadingCover;
            }
            if (!launchFlow.OwnsActive(context))
            {
                if (context.OwnsLoadingCover) context.LoadingCover.HideImmediate();
                yield break;
            }

            if (overlayVisible)
            {
                // Render the cover for one frame before the synchronous load, then hold briefly to avoid a
                // flash.
                if (context.OwnsLoadingCover)
                    context.LoadingCover.AdvanceOpeningFirstMilestone();
                yield return null;
                if (!launchFlow.OwnsActive(context)) yield break;
                if (!context.UsesExternalScene)
                {
                    yield return new WaitForSecondsRealtime(0.18f);
                    if (!launchFlow.OwnsActive(context)) yield break;
                }
                if (context.OwnsLoadingCover)
                    context.LoadingCover.AdvanceOpeningSecondMilestone();
                if (!context.UsesExternalScene)
                {
                    yield return new WaitForSecondsRealtime(0.12f);
                    if (!launchFlow.OwnsActive(context)) yield break;
                }
                // No-op for the scene-load branch below, which is long enough already.
                yield return context.LoadingCover.HoldOpeningBeat(context.LoadingPresentationVersion);
            }
            else
            {
                // Preserve the render-before-load contract even if art is unavailable.
                yield return null;
            }

            if (!launchFlow.OwnsActive(context)) yield break;
            if (!launchFlow.TryEnterLaunchPath(context))
            {
                TrySettleLaunch(
                    context,
                    LaunchFlowState.Failed,
                    "GAME SCENE COULD NOT BE LOADED");
                yield break;
            }

            if (context.UsesExternalScene)
            {
                AsyncOperation sceneLoad = null;
                try
                {
                    sceneLoad = SceneManager.LoadSceneAsync(
                        context.SceneName,
                        LoadSceneMode.Single);
                    if (sceneLoad != null) outstandingSceneLoad = sceneLoad;
                }
                catch (Exception exception)
                {
                    rejectionReason = "Game scene could not be loaded";
                    Debug.LogException(exception, this);
                }

                if (sceneLoad != null)
                {
                    // Keep activation owned by this launch until the cover can survive scene replacement.
                    sceneLoad.allowSceneActivation = false;
                    if (!launchFlow.OwnsActive(context))
                    {
                        sceneLoad.allowSceneActivation = true;
                        yield break;
                    }
                    if (!context.TryAttachSceneOperation(sceneLoad))
                    {
                        sceneLoad.allowSceneActivation = true;
                        TrySettleLaunch(
                            context,
                            LaunchFlowState.Failed,
                            "GAME SCENE COULD NOT BE LOADED");
                        yield break;
                    }

                    double sceneWaitStartedAt = Time.realtimeSinceStartupAsDouble;
                    bool slowLoadReported = false;
                    while (sceneLoad.progress < 0.9f)
                    {
                        if (!launchFlow.OwnsActive(context)) yield break;
                        if (!slowLoadReported
                            && LaunchWaitTimedOut(
                                sceneWaitStartedAt,
                                Time.realtimeSinceStartupAsDouble,
                                SceneLoadWaitTimeoutSeconds))
                        {
                            // An async load cannot be cancelled. Keep its context, disabled Play button and
                            // First Shift request so a retry cannot race it.
                            if (!launchFlow.TryMarkSceneLoadSlow(context)) yield break;
                            slowLoadReported = true;
                            Debug.LogWarning(
                                "Game scene loading exceeded the watchdog threshold; "
                                + "the original operation remains authoritative.", this);
                        }
                        if (context.OwnsLoadingCover)
                        {
                            float progress = Mathf.Clamp01(sceneLoad.progress / 0.9f);
                            context.LoadingCover.ReportSceneLoadProgress(progress);
                        }
                        yield return null;
                    }

                    if (!launchFlow.TryAwaitActivation(context))
                    {
                        if (launchFlow.OwnsActive(context))
                            TrySettleLaunch(
                                context,
                                LaunchFlowState.Failed,
                                "GAME SCENE COULD NOT BE LOADED");
                        yield break;
                    }

                    if (context.OwnsLoadingCover
                        && !BartenderLoadingSceneHandoff.TryBegin(
                            context.LoadingCover, sceneLoad, context.LoadingPresentationVersion))
                    {
                        // Fall back to the original cover lifetime if this view cannot be detached.
                        yield return context.LoadingCover.FillToCompletion(context.LoadingPresentationVersion);
                        if (!launchFlow.OwnsActive(context)) yield break;
                    }

                    // Only TrySettleLaunch opens activation. A started scene load keeps its exact
                    // First Shift request even if its visual cover is interrupted.
                    TrySettleLaunch(context, LaunchFlowState.Completed);
                    yield break;
                }

                if (context.OwnsLoadingCover)
                {
                    yield return context.LoadingCover.CancelAndHide(context.LoadingPresentationVersion);
                    if (!launchFlow.OwnsActive(context)) yield break;
                }
                TrySettleLaunch(
                    context,
                    LaunchFlowState.Failed,
                    string.IsNullOrWhiteSpace(rejectionReason)
                    ? "GAME SCENE COULD NOT BE LOADED"
                    : rejectionReason.ToUpper(EnglishCulture));
                yield break;
            }

            if (controller == null)
            {
                rejectionReason = "Game scene is not ready";
            }
            else
            {
                BartenderShelfLevelView coveredShelf =
                    controller.GetComponent<BartenderShelfLevelView>();
                context.TrackCoveredShelf(coveredShelf, false);
                bool coveredShelfArmed = false;
                if (context.OwnsLoadingCover && coveredShelf != null)
                    coveredShelfArmed =
                        coveredShelf.ArmNextCoveredLoad(context.LoadingCover);
                if (!launchFlow.OwnsActive(context))
                {
                    if (coveredShelfArmed)
                        coveredShelf.DisarmCoveredLoad(context.LoadingCover);
                    yield break;
                }
                context.TrackCoveredShelf(coveredShelf, coveredShelfArmed);
                try
                {
                    started = controller.TryStartSavedCampaign(out rejectionReason);
                }
                catch (Exception exception)
                {
                    rejectionReason = "An unexpected error occurred while starting the level";
                    Debug.LogException(exception, this);
                }
                // Keep armed and promoted refresh ownership on this context. Every exit releases it through
                // TrySettleLaunch.
            }

            if (!launchFlow.OwnsActive(context)) yield break;

            if (started)
            {
                // The board is already Playing. Hold its clock until the cover finishes.
                if (!launchFlow.TryAwaitActivation(context))
                {
                    TrySettleLaunch(
                        context,
                        LaunchFlowState.Failed,
                        "LEVEL COULD NOT BE STARTED",
                        true);
                    yield break;
                }

                if (controller != null && controller.AcquirePresentationBarrier(context))
                {
                    if (!launchFlow.OwnsActive(context)
                        || !context.TryAttachBarrier(controller))
                    {
                        controller.ReleasePresentationBarrier(context);
                        if (launchFlow.OwnsActive(context))
                        {
                            TrySettleLaunch(
                                context,
                                LaunchFlowState.Failed,
                                "LEVEL COULD NOT BE STARTED",
                                true);
                        }
                        yield break;
                    }
                }

                if (context.OwnsLoadingCover)
                {
                    BartenderLevelIntroDirector coveredIntro = controller != null
                        ? controller.GetComponent<BartenderLevelIntroDirector>() : null;
                    double coveredWaitStartedAt = Time.realtimeSinceStartupAsDouble;
                    while (((context.CoveredShelf != null
                             && context.CoveredShelf.CoveredPresentationPending)
                            || (coveredIntro != null && coveredIntro.CoveredPreparationPending))
                           && !CoveredShelfWaitTimedOut(
                               coveredWaitStartedAt,
                               Time.realtimeSinceStartupAsDouble))
                    {
                        if (!launchFlow.OwnsActive(context)) yield break;
                        if (!context.OwnsLoadingCover) break;
                        yield return null;
                    }

                    if ((context.CoveredShelf != null
                         && context.CoveredShelf.CoveredPresentationPending)
                        || (coveredIntro != null && coveredIntro.CoveredPreparationPending))
                    {
                        Debug.LogWarning(
                            "[Main Menu] Covered shelf presentation exceeded its "
                            + $"{CoveredShelfWaitTimeoutSeconds:0.#} second launch wait; "
                            + "the loading barrier was released defensively.",
                            this);
                    }

                    yield return context.LoadingCover.CompleteAndHide(context.LoadingPresentationVersion);
                    if (!launchFlow.OwnsActive(context)) yield break;
                }

                TrySettleLaunch(context, LaunchFlowState.Completed);
            }
            else
            {
                if (context.OwnsLoadingCover)
                {
                    yield return context.LoadingCover.CancelAndHide(context.LoadingPresentationVersion);
                    if (!launchFlow.OwnsActive(context)) yield break;
                }
                TrySettleLaunch(
                    context,
                    LaunchFlowState.Failed,
                    string.IsNullOrWhiteSpace(rejectionReason)
                    ? "LEVEL COULD NOT BE STARTED"
                    : rejectionReason.ToUpper(EnglishCulture),
                    true);
            }
        }

        private void CancelStartGameTransition()
        {
            TrySettleLaunch(
                launchFlow.Current,
                LaunchFlowState.Cancelled);
        }

        private void TryAutoStartFirstShift()
        {
            if (BartenderStartupIntro.IsPlaying || firstShiftAutoStartAttemptedThisSession
                || !BartenderFirstShiftProgress.ShouldStart
                || string.IsNullOrWhiteSpace(gameplaySceneName)
                || (menuRoot != null && !menuRoot.activeInHierarchy)
                || (homeRewardPresenter != null && homeRewardPresenter.IsPresenting))
                return;

            // Mark before StartGame so aborted onboarding cannot auto-launch forever. PLAY can still
            // request a fresh retry.
            firstShiftAutoStartAttemptedThisSession = true;
            StartGame(GameLaunchOrigin.AutomaticFirstShift);
        }

        internal static bool CoveredShelfWaitTimedOut(
            double startedAt, double now,
            double timeoutSeconds = CoveredShelfWaitTimeoutSeconds)
            => LaunchWaitTimedOut(startedAt, now, timeoutSeconds);

        internal static bool LaunchWaitTimedOut(
            double startedAt, double now, double timeoutSeconds)
        {
            if (double.IsNaN(startedAt) || double.IsNaN(now)
                || double.IsNaN(timeoutSeconds) || timeoutSeconds < 0d
                || now < startedAt)
                return false;
            return now - startedAt >= timeoutSeconds;
        }

        /// <summary>Tries every cleanup even if one throws, then returns the first error.</summary>
        internal static Exception RunLaunchCleanupSteps(params Action[] cleanupSteps)
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

        /// <summary>
        /// Releases one launch's resources. Marking Settling first blocks repeated cleanup; exact context
        /// checks protect newer launches.
        /// </summary>
        private bool TrySettleLaunch(
            LaunchRunContext context,
            LaunchFlowState terminalState,
            string failureNotice = null,
            bool refreshOnFailure = false)
        {
            if (!launchFlow.TryBeginSettle(context, terminalState)) return false;

            AsyncOperation sceneOperation = context.TakeSceneOperation();
            BartenderShelfLevelView coveredShelf = context.TakeCoveredShelf();
            BartenderLevelController barrierController = context.TakeBarrier();
            Coroutine routine = context.TakeRoutine();
            IDisposable musicSuspension = context.TakeMusicSuspension();
            bool sceneHandoff = terminalState == LaunchFlowState.Completed
                && context.UsesExternalScene
                && sceneOperation != null;

            // Detach every handle before callbacks so re-entry cannot repeat cleanup and one error cannot
            // hide another lease.
            Exception cleanupFailure = RunLaunchCleanupSteps(
                context.ReleaseMenuInput,
                () =>
                {
                    if (coveredShelf != null && context.LoadingCover != null)
                        coveredShelf.DisarmCoveredLoad(context.LoadingCover);
                },
                () =>
                {
                    if (barrierController != null)
                        barrierController.ReleasePresentationBarrier(context);
                },
                () =>
                {
                    if (terminalState != LaunchFlowState.Completed && sceneOperation == null)
                        BartenderFirstShiftInstaller.CancelPendingLaunch(
                            context.FirstShiftRequest);
                },
                () =>
                {
                    // A successful scene load keeps its full cover until activation. Other exits hide it
                    // now.
                    if (context.OwnsLoadingCover && !sceneHandoff)
                        context.LoadingCover.HideImmediate();
                },
                () =>
                {
                    if (launchFlow.TryFinishSettle(context, terminalState)) return;
                    // Defensive fallback; TryBeginSettle already checked ownership and entered Settling.
                    if (launchFlow.Owns(context)) context.State = terminalState;
                    throw new InvalidOperationException(
                        $"Launch {context.OperationId} could not finish settlement.");
                },
                () =>
                {
                    if (terminalState != LaunchFlowState.Completed && playButton != null)
                        playButton.interactable = !LaunchInProgress;
                    if (terminalState != LaunchFlowState.Completed && sceneOperation == null)
                        menuMusicPending = true;
                },
                () =>
                {
                    if (terminalState == LaunchFlowState.Failed
                        && !string.IsNullOrWhiteSpace(failureNotice))
                        ShowNotice(failureNotice);
                },
                () =>
                {
                    if (terminalState == LaunchFlowState.Failed && refreshOnFailure)
                        RefreshAll();
                },
                () =>
                {
                    // Ownership is already finalized, so cancellation is safe even from this iterator.
                    if (routine != null && !context.AdvancingRoutine) StopCoroutine(routine);
                },
                () =>
                {
                    // Activate last to preserve the cover's final frame and audio. Cleanup errors must not
                    // leave Unity stuck at 0.9.
                    if (sceneOperation != null)
                    {
                        // Even a cancelled launch cannot cancel Unity's load. Keep music paused until its
                        // destination is ready, including automatic launches without a loading canvas.
                        BartenderSceneMusicHandoff.TakeOver(musicSuspension, sceneOperation);
                        musicSuspension = null;
                        sceneOperation.allowSceneActivation = true;
                    }
                },
                () => musicSuspension?.Dispose());

            // Do not throw during disable or scene teardown. All owned resources have already had a cleanup
            // attempt.
            if (cleanupFailure != null) Debug.LogException(cleanupFailure, this);
            return true;
        }

        private void OpenSettings()
        {
            if (settingsOverlay == null) return;
            if (menuRoot != null && !menuRoot.activeInHierarchy) return;
            CloseMoreLives();
            RefreshSettings();
            settingsOverlay.SetActive(true);
            BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.8f);
        }

        private void CloseSettings()
        {
            // Only play a close sound if the popup was open.
            bool wasOpen = settingsOverlay != null && settingsOverlay.activeSelf;
            if (settingsOverlay != null) settingsOverlay.SetActive(false);
            if (wasOpen) BsAudio.Instance?.Play(BsSfx.PopupClose, 0.8f);
        }

        private void ShowLifeStoreNotice()
        {
            if (ShouldOpenMoreLivesFromHud(BartenderProgressService.Lives))
            {
                OpenMoreLives();
                return;
            }

            ShowNotice("LIVES ARE FULL");
        }

        internal static bool ShouldOpenMoreLivesFromHud(int lives) =>
            lives < BartenderProgressService.MaxLives;

        private void OpenMoreLives()
        {
            if (!moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.Show)) return;
            BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.8f);
            CloseSettings();
            EnsureAuthoredMoreLivesView();
            if (authoredMoreLivesView != null && authoredMoreLivesView.IsReady)
            {
                authoredMoreLivesView.SetFeedback(string.Empty);
                authoredMoreLivesView.SetVisible(true);
                RefreshMoreLives();
                return;
            }

            ResetMoreLives();
            ShowNotice("MORE LIVES IS NOT AVAILABLE");
        }

        private void CloseMoreLives()
        {
            if (moreLivesFlow.State == BsPurchaseOverlayState.Hidden)
            {
                HideMoreLivesVisuals();
                return;
            }
            if (!moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.Dismiss)) return;
            HideMoreLivesVisuals();
        }

        private void ResetMoreLives()
        {
            _ = moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.Reset);
            HideMoreLivesVisuals();
        }

        private void HideMoreLivesVisuals()
        {
            if (authoredMoreLivesView != null)
                authoredMoreLivesView.SetVisible(false);
        }

        private void EnsureAuthoredMoreLivesView()
        {
            if (authoredMoreLivesPrepared) return;
            if (authoredMoreLivesView == null)
            {
                if (!authoredMoreLivesErrorReported)
                {
                    authoredMoreLivesErrorReported = true;
                    Debug.LogError(
                        "Authored More Lives View referansı menü Inspector'ında bağlı değil.",
                        this);
                }
                return;
            }
            if (!authoredMoreLivesView.IsReady)
            {
                if (!authoredMoreLivesErrorReported)
                {
                    authoredMoreLivesErrorReported = true;
                    Debug.LogError("The authored More Lives popup is incomplete.",
                        authoredMoreLivesView);
                }
                return;
            }

            authoredMoreLivesPrepared = true;
            authoredMoreLivesView.CloseButton.onClick.RemoveListener(CloseMoreLives);
            authoredMoreLivesView.CloseButton.onClick.AddListener(CloseMoreLives);
            authoredMoreLivesView.RefillButton.onClick.RemoveListener(RefillLives);
            authoredMoreLivesView.RefillButton.onClick.AddListener(RefillLives);
            CanvasScaler canvasScaler = GetComponent<CanvasScaler>();
            float referenceWidth = canvasScaler != null
                ? canvasScaler.referenceResolution.x
                : AuthoredPopupReferenceWidth;
            float cardScale = Mathf.Max(1f,
                referenceWidth / AuthoredPopupReferenceWidth);
            authoredMoreLivesView.CardPivot.localScale = Vector3.one * cardScale;
            authoredMoreLivesView.SetVisible(false);
        }

        private void RefillLives()
        {
            if (moreLivesFlow.State != BsPurchaseOverlayState.Visible
                || !moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.BeginPurchase))
                return;

            RefreshMoreLives(false);
            bool refilled = BartenderProgressService.TryRefillLivesToMaximum(
                    BartenderProgressService.FullLifeRefillCoinCost,
                    out string rejectionReason);
            if (refilled)
            {
                BsAudio.Instance?.Play(BsSfx.LifeRefill, LifeRefillVolume);
                if (!moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.PurchaseSucceeded))
                    Debug.LogWarning(
                        "More-lives flow refused PurchaseSucceeded from " + moreLivesFlow.State,
                        this);
                HideMoreLivesVisuals();
                return;
            }

            if (!moreLivesFlow.Dispatch(BsPurchaseOverlayTrigger.PurchaseRejected))
                Debug.LogWarning(
                    "More-lives flow refused PurchaseRejected from " + moreLivesFlow.State,
                    this);

            // Let the shop close this card after the purchase flow is dismissible again.
            if (BartenderProgressService.Lives < BartenderProgressService.MaxLives
                && BartenderProgressService.Coins
                    < BartenderProgressService.FullLifeRefillCoinCost
                && BartenderShopPresenter.TryOpenCurrentScene())
                return;

            if (moreLivesFeedbackLabel != null)
                moreLivesFeedbackLabel.text = string.IsNullOrWhiteSpace(rejectionReason)
                    ? "REFILL FAILED"
                    : rejectionReason.ToUpper(EnglishCulture);
            if (authoredMoreLivesView != null)
                authoredMoreLivesView.SetFeedback(
                    string.IsNullOrWhiteSpace(rejectionReason)
                        ? "REFILL FAILED"
                        : rejectionReason.ToUpper(EnglishCulture));
            RefreshMoreLives(false);
        }
        // Show the fallback notice if this scene cannot open the shop.
        private void ShowCoinStoreNotice()
        {
            if (!BartenderShopPresenter.TryOpenCurrentScene())
                ShowNotice("COIN STORE COMING SOON");
        }

        private void ShowShopNotice()
        {
            if (!BartenderShopPresenter.TryOpenCurrentScene())
                ShowNotice("SHOP COMING SOON");
        }
        private void SelectRecipes()
        {
            GetComponent<BartenderRecipeBookPresenter>()?.Open();
        }

        /// <summary>Closes the shop or returns to Home.</summary>
        private void SelectHome()
        {
            GetComponent<BartenderDailyOrdersPresenter>()?.Close();
            HideNoticeImmediate();
            if (!BartenderShopPresenter.CloseAny()) ExitShopPresentation();
        }

        /// <summary>Shows Shop in the shared page container while keeping bottom navigation visible.</summary>
        internal void EnterShopPresentation()
        {
            GetComponent<BartenderDailyOrdersPresenter>()?.Close();
            CloseSettings();
            CloseMoreLives();
            HideNoticeImmediate();
            ShowOnlyPage(shopPage);
            SelectNavigationTab(shopButton);
        }

        internal void RegisterRecipePage(GameObject page)
        {
            recipePage = page;
            if (recipePage != null) recipePage.SetActive(false);
        }

        internal void PrepareDailyOrdersPresentation()
        {
            CloseSettings();
            CloseMoreLives();
            HideNoticeImmediate();
        }

        /// <summary>Saves once, holding the HUD at its old value until the daily panel closes.</summary>
        internal bool TryClaimDailyOrdersForPresentation(long expectedDay, Vector2 sourceViewport,
            out string rejectionReason)
        {
            if (!CanUseHomeFeatures || dailyRewardCoinProjectionHeld)
            {
                rejectionReason = "Wait for the current reward to finish";
                return false;
            }
            int previousCoins = BartenderProgressService.Coins;
            dailyRewardCoinProjectionHeld = true;
            bool claimed = false;
            try
            {
                claimed = BartenderProgressService.TryClaimDailyOrdersReward(
                    expectedDay, out rejectionReason);
                if (!claimed) return false;

                int finalCoins = BartenderProgressService.Coins;
                // A prior interrupted receipt represents coins already saved. The newly claimed
                // daily reward takes its visual slot, following the existing win-reward policy.
                BartenderPendingHomeRewardStore.DiscardStalePending();
                dailyRewardCoinProjectionHeld = BartenderPendingHomeRewardStore.TryStageCoins(
                    previousCoins, finalCoins, sourceViewport, out _);
                return true;
            }
            finally
            {
                if (!claimed) dailyRewardCoinProjectionHeld = false;
                if (!dailyRewardCoinProjectionHeld)
                    HandleCoinsChanged(BartenderProgressService.Coins);
            }
        }

        /// <summary>Called after the popup is hidden, so the existing coin pool flies over the menu.</summary>
        internal void PresentDailyOrdersRewardAfterClose()
        {
            if (!dailyRewardCoinProjectionHeld) return;
            dailyRewardCoinProjectionHeld = false;
            if (isActiveAndEnabled && Visible) TryPresentHomeReward();
            RefreshAll();
        }

        private void TryPresentHomeReward()
        {
            if (!BartenderStartupIntro.IsPlaying && !dailyRewardCoinProjectionHeld)
                homeRewardPresenter?.TryPresentPending();
        }

        internal void EnterRecipeBookPresentation()
        {
            GetComponent<BartenderDailyOrdersPresenter>()?.Close();
            BartenderShopPresenter.CloseAny();
            CloseSettings();
            CloseMoreLives();
            HideNoticeImmediate();
            ShowOnlyPage(recipePage);
            SelectNavigationTab(recipesButton);
        }

        /// <summary>Returns to the authored home page while preserving shared chrome.</summary>
        internal void ExitShopPresentation()
        {
            ShowOnlyPage(homePage);
            SelectNavigationTab(homeButton);
        }

        private void ShowOnlyPage(GameObject selectedPage)
        {
            if (homePage != null) homePage.SetActive(selectedPage == homePage);
            if (shopPage != null) shopPage.SetActive(selectedPage == shopPage);
            if (recipePage != null) recipePage.SetActive(selectedPage == recipePage);
        }

        private void SelectNavigationTab(Button tab)
        {
            if (bottomNavigationPresenter != null && tab != null)
                bottomNavigationPresenter.SelectTab(tab);
        }

        private void SetMusicVolume(float volume)
        {
            BartenderSettingsStore.SetMusicVolume(volume);
            RefreshSettings();
        }

        private void SetSoundVolume(float volume)
        {
            BartenderSettingsStore.SetSoundVolume(volume);
            RefreshSettings();
        }

        private void SetVibrationEnabled(bool enabled)
        {
            BartenderSettingsStore.SetVibrationOn(enabled);
            RefreshSettings();
        }

        private void SetNotificationsEnabled(bool enabled)
        {
            BartenderSettingsStore.SetNotificationsOn(enabled);
            RefreshSettings();
        }

        public void RequestAccountInfo()
        {
            bool hasHandler = accountInfoRequested != null
                && accountInfoRequested.GetPersistentEventCount() > 0;
            accountInfoRequested?.Invoke();
            if (!hasHandler)
            {
                CloseSettings();
                ShowNotice("ACCOUNT INFO COMING SOON");
            }
        }

        public void RequestHelpAndSupport()
        {
            bool hasHandler = helpSupportRequested != null
                && helpSupportRequested.GetPersistentEventCount() > 0;
            helpSupportRequested?.Invoke();
            if (!hasHandler)
            {
                CloseSettings();
                ShowNotice("HELP & SUPPORT COMING SOON");
            }
        }

        private void ShowNotice(string message)
        {
            if (noticeLabel == null) return;

            StopNoticeAnimation(false);
            RefreshNoticePosition();

            RectTransform motionRect = noticeRect != null
                ? noticeRect
                : noticeLabel.rectTransform;
            noticeRestAnchoredPosition = motionRect.anchoredPosition;
            noticeRestScale = motionRect.localScale;
            noticeRestAlpha = noticeLabel.color.a;
            noticeRestPoseCaptured = true;

            noticeLabel.text = message ?? string.Empty;
            noticeLabel.gameObject.SetActive(true);

            Color hidden = noticeLabel.color;
            hidden.a = 0f;
            noticeLabel.color = hidden;
            motionRect.anchoredPosition = noticeRestAnchoredPosition + Vector2.down * 20f;
            motionRect.localScale = ScaledNoticePose(noticeRestScale, 0.74f);

            Sequence sequence = DOTween.Sequence()
                .SetUpdate(true)
                .SetRecyclable(true);
            sequence.Append(noticeLabel.DOFade(noticeRestAlpha, 0.18f)
                .SetEase(Ease.OutQuad));
            sequence.Join(motionRect.DOAnchorPos(noticeRestAnchoredPosition, 0.28f)
                .SetEase(Ease.OutCubic));
            sequence.Join(motionRect.DOScale(noticeRestScale, 0.32f)
                .SetEase(Ease.OutBack));
            sequence.AppendInterval(1.55f);
            sequence.Append(noticeLabel.DOFade(0f, 0.34f)
                .SetEase(Ease.InCubic));
            sequence.Join(motionRect.DOAnchorPos(
                    noticeRestAnchoredPosition + Vector2.up * 34f, 0.34f)
                .SetEase(Ease.InCubic));
            sequence.Join(motionRect.DOScale(
                    ScaledNoticePose(noticeRestScale, 0.94f), 0.34f)
                .SetEase(Ease.InCubic));
            sequence.OnComplete(() =>
            {
                if (noticeTween != sequence) return;
                RestoreNoticePose();
                if (noticeLabel != null) noticeLabel.gameObject.SetActive(false);
                noticeTween = null;
            });
            noticeTween = sequence;
        }

        private static Vector3 ScaledNoticePose(Vector3 pose, float scale) =>
            new Vector3(pose.x * scale, pose.y * scale, pose.z);

        private void HideNoticeImmediate()
        {
            StopNoticeAnimation(true);
        }

        private void StopNoticeAnimation(bool hide)
        {
            if (noticeTween != null && noticeTween.IsActive())
                noticeTween.Kill(false);
            noticeTween = null;
            RestoreNoticePose();
            if (hide && noticeLabel != null)
                noticeLabel.gameObject.SetActive(false);
        }

        private void RestoreNoticePose()
        {
            if (!noticeRestPoseCaptured) return;

            RectTransform motionRect = noticeRect != null
                ? noticeRect
                : noticeLabel != null ? noticeLabel.rectTransform : null;
            if (motionRect != null)
            {
                motionRect.anchoredPosition = noticeRestAnchoredPosition;
                motionRect.localScale = noticeRestScale;
            }

            if (noticeLabel != null)
            {
                Color restored = noticeLabel.color;
                restored.a = noticeRestAlpha;
                noticeLabel.color = restored;
            }
        }

        private void EnsureHomeRewardPresenter()
        {
            EnsureLevelButtonView();
            if (homeRewardPresenter == null)
            {
                if (!homeRewardErrorReported)
                {
                    homeRewardErrorReported = true;
                    Debug.LogError(
                        "Authored Home Reward Presenter referansı menü Inspector'ında bağlı değil.",
                        this);
                }
                return;
            }

            homeRewardPresenter.LevelAdvanceReady -= HandleHomeRewardLevelAdvanceReady;
            homeRewardPresenter.LevelAdvanceReady += HandleHomeRewardLevelAdvanceReady;
            homeRewardPresenter.PresentationCompleted -=
                HandleHomeRewardPresentationCompleted;
            homeRewardPresenter.PresentationCompleted +=
                HandleHomeRewardPresentationCompleted;
            homeRewardPresenter.PresentationInterrupted -= HandleHomeRewardPresentationInterrupted;
            homeRewardPresenter.PresentationInterrupted += HandleHomeRewardPresentationInterrupted;

            if (menuCanvas == null)
            {
                if (!homeRewardErrorReported)
                {
                    homeRewardErrorReported = true;
                    Debug.LogError("Menu Canvas referansı Inspector'da bağlı değil.", this);
                }
                return;
            }
            homeRewardPresenter.Bind(
                menuCanvas,
                coinHudIcon,
                coinLabel,
                playButton,
                playLabel,
                levelButtonView);
        }

        private void PrepareAuthoredLoadingOverlay()
        {
            if (loadingOverlay == null)
            {
                ReportMissingLoadingOverlay();
                return;
            }
            loadingOverlay.Prewarm();
        }

        private void ReportMissingLoadingOverlay()
        {
            if (loadingOverlay != null || loadingOverlayErrorReported) return;
            loadingOverlayErrorReported = true;
            Debug.LogError(
                "Authored Loading Overlay referansı menü Inspector'ında bağlı değil.", this);
        }

        private void EnsureLevelButtonView()
        {
            if (playButton != null && playLabel != null && levelButtonView == null
                && !levelButtonViewErrorReported)
            {
                // A missing override skips badge animation and difficulty styling even though rewards
                // continue. Report the broken reference.
                levelButtonViewErrorReported = true;
                Debug.LogError(
                    "Authored Level Button View referansı menü Inspector'ında bağlı "
                  + "değil. Sahnedeki prefab instance'ında bu alan override ile "
                  + "None'a çekilmiş olabilir.", this);
            }
            if (playButton == null || playLabel == null || levelButtonView == null)
                return;
            if (levelButtonView.gameObject != playButton.gameObject)
            {
                Debug.LogWarning(
                    "Level button view, authored LEVEL button üzerinde olmalı.", this);
                return;
            }
            levelButtonView.Bind(playButton, playLabel);
        }

        private void UnsubscribeHomeRewardPresenter()
        {
            if (homeRewardPresenter == null) return;
            homeRewardPresenter.LevelAdvanceReady -= HandleHomeRewardLevelAdvanceReady;
            homeRewardPresenter.PresentationCompleted -=
                HandleHomeRewardPresentationCompleted;
            homeRewardPresenter.PresentationInterrupted -= HandleHomeRewardPresentationInterrupted;
        }

        private void HandleHomeRewardLevelAdvanceReady() => RefreshLevel();

        private void HandleHomeRewardPresentationCompleted()
        {
            RefreshAll();
            TryAutoStartFirstShift();
        }

        // A later menu entry can retry the retained receipt. Do not retry here: unavailable visuals must
        // not create a loop or keep the menu locked.
        private void HandleHomeRewardPresentationInterrupted() => RefreshAll();

        private void HandleControllerStateChanged(BartenderLevelState _) =>
            ProjectControllerState();

        private void HandleCoinsChanged(int value)
        {
            if (homeRewardPresenter != null)
                homeRewardPresenter.SetAuthoritativeCoinBalance(value);
            if (coinLabel != null
                && !dailyRewardCoinProjectionHeld
                && (homeRewardPresenter == null
                    || !homeRewardPresenter.SuppressesCoinProjection))
                coinLabel.text = value.ToString(CultureInfo.InvariantCulture);
            if (moreLivesCoinBalanceLabel != null)
                moreLivesCoinBalanceLabel.text = value.ToString(CultureInfo.InvariantCulture);
        }

        private void HandleLivesChanged(int value)
        {
            int previous = lastKnownLives;
            lastKnownLives = value;
            // Play only for a single visible life refill. Keep offline catch-up refills silent.
            if (previous >= 0 && value == previous + 1)
                BsAudio.Instance?.Play(BsSfx.LifeRegained, LifeRegainedVolume);

            if (lifeCountLabel != null)
                lifeCountLabel.text = value.ToString(CultureInfo.InvariantCulture);
            if (moreLivesCountLabel != null)
                moreLivesCountLabel.text = value.ToString(CultureInfo.InvariantCulture);
            if (moreLivesFlow.State != BsPurchaseOverlayState.Hidden)
                RefreshMoreLives(false);
            RefreshLevel();
        }

        private void HandleLifeTimerChanged(TimeSpan remaining)
        {
            RefreshLifeTimer(remaining);
            if (authoredMoreLivesView != null)
            {
                authoredMoreLivesView.SetValues(
                    BartenderProgressService.Lives,
                    remaining,
                    BartenderProgressService.FullLifeRefillCoinCost);
            }
        }

        private void HandleProgressChanged(int _) => RefreshLevel();
        private void HandleSettingsChanged() => RefreshSettings();

        private void ProjectControllerState()
        {
            if (menuRoot == null) return;
            bool show = controller == null
                || controller.State == BartenderLevelState.Unloaded
                || controller.State == BartenderLevelState.CampaignComplete;
            // Apply after the controller's full transition dispatch: Session may still stop the old
            // gameplay track later in this same StateChanged call stack.
            menuMusicPending = show;
            menuRoot.SetActive(show);
            if (!show)
            {
                homeRewardPresenter?.CancelAndSnap();
                CloseSettings();
                ResetMoreLives();
            }
            if (show)
            {
                RefreshAll();
                TryPresentHomeReward();
                RefreshNoticePosition();
            }
        }

        private void RefreshAll()
        {
            HandleCoinsChanged(BartenderProgressService.Coins);
            HandleLivesChanged(BartenderProgressService.Lives);
            RefreshLifeTimer(BartenderProgressService.LifeTimer);
            RefreshLevel();
            RefreshSettings();
        }

        private void RefreshLifeTimer(TimeSpan remaining)
        {
            if (BartenderProgressService.IsLifeFull)
            {
                if (lifeTimerLabel != null) lifeTimerLabel.text = "FULL";
                if (moreLivesTimerLabel != null) moreLivesTimerLabel.text = "FULL";
                return;
            }

            long totalSeconds = Math.Max(0L, (long)Math.Ceiling(remaining.TotalSeconds));
            long totalMinutes = totalSeconds / 60L;
            long seconds = totalSeconds % 60L;
            string formatted = totalMinutes.ToString("00", CultureInfo.InvariantCulture)
                             + ":"
                             + seconds.ToString("00", CultureInfo.InvariantCulture);
            if (lifeTimerLabel != null) lifeTimerLabel.text = formatted;
            if (moreLivesTimerLabel != null) moreLivesTimerLabel.text = formatted;
        }

        private void RefreshMoreLives(bool clearFeedback = true)
        {
            int lives = BartenderProgressService.Lives;
            TimeSpan remaining = BartenderProgressService.LifeTimer;
            HandleCoinsChanged(BartenderProgressService.Coins);
            if (moreLivesCountLabel != null)
                moreLivesCountLabel.text = lives.ToString(CultureInfo.InvariantCulture);
            if (moreLivesRefillCostLabel != null)
                moreLivesRefillCostLabel.text =
                    BartenderProgressService.FullLifeRefillCoinCost
                        .ToString(CultureInfo.InvariantCulture);
            RefreshLifeTimer(remaining);
            if (refillLivesButton != null)
                refillLivesButton.interactable =
                    moreLivesFlow.State == BsPurchaseOverlayState.Visible
                    && lives < BartenderProgressService.MaxLives;

            if (authoredMoreLivesView != null)
            {
                authoredMoreLivesView.SetValues(
                    lives,
                    remaining,
                    BartenderProgressService.FullLifeRefillCoinCost);
                bool visible = moreLivesFlow.State == BsPurchaseOverlayState.Visible
                    && authoredMoreLivesView.gameObject.activeSelf;
                authoredMoreLivesView.SetButtonsInteractable(
                    visible,
                    visible && lives < BartenderProgressService.MaxLives);
                if (clearFeedback) authoredMoreLivesView.SetFeedback(string.Empty);
            }

            // Keep the ad button disabled until a verified rewarded-ad adapter can grant lives.
            if (rewardedLifeButton != null) rewardedLifeButton.interactable = false;
            if (clearFeedback && moreLivesFeedbackLabel != null)
                moreLivesFeedbackLabel.text = string.Empty;
        }

        private void RefreshLevel()
        {
            if (playButton == null || playLabel == null) return;
            EnsureLevelButtonView();
            if (homeRewardPresenter != null
                && homeRewardPresenter.SuppressesLevelProjection)
                return;
            bool rewardInputLocked = homeRewardPresenter != null
                                     && homeRewardPresenter.IsPresenting;
            int savedSlot = BartenderProgressService.NextUnlockedCampaignSlot;
            int playableSlot = BartenderLevelController.ResolveNextPlayableSlot();
            if (playableSlot < 0)
            {
                if (levelButtonView != null) levelButtonView.ApplyCompleted();
                else playLabel.text = "COMPLETED";
                RefreshNoticePosition();
                playButton.interactable = false;
                return;
            }

            int levelNumber = BartenderLevelController.ResolveCampaignLevelNumber(playableSlot);
            if (levelButtonView != null) levelButtonView.ApplyLevel(levelNumber);
            else
            {
                playLabel.text = "LEVEL " + Mathf.Max(1, levelNumber)
                    .ToString(CultureInfo.InvariantCulture);
            }
            if (playableSlot < savedSlot)
                playLabel.text = "REPLAY " + levelNumber.ToString(CultureInfo.InvariantCulture);
            RefreshNoticePosition();
            // Zero lives remains tappable so the timer/reason can be explained.
            playButton.interactable = !rewardInputLocked && !LaunchInProgress;
        }

        private void RefreshSettings()
        {
            if (musicVolumeSlider != null)
                musicVolumeSlider.SetValueWithoutNotify(
                    BartenderSettingsStore.EffectiveMusicVolume);
            if (soundVolumeSlider != null)
                soundVolumeSlider.SetValueWithoutNotify(
                    BartenderSettingsStore.EffectiveSoundVolume);
            if (vibrationToggle != null)
            {
                vibrationToggle.SetIsOnWithoutNotify(
                    BartenderSettingsStore.VibrationOn);
                vibrationToggle.GetComponent<BartenderHapticToggleVisual>()?.Refresh();
            }
            if (notificationsToggle != null)
            {
                notificationsToggle.SetIsOnWithoutNotify(
                    BartenderSettingsStore.NotificationsOn);
                notificationsToggle.GetComponent<BartenderHapticToggleVisual>()?.Refresh();
            }
        }

        private void RefreshResponsiveLayout(bool force = false)
        {
            if (layoutAreaRect == null || primaryActionRect == null || noticeRect == null)
                return;

            float height = layoutAreaRect.rect.height;
            if (height <= 0f) return;
            if (!force && Mathf.Approximately(height, lastLayoutHeight)) return;
            lastLayoutHeight = height;

            bool compact = height < 1680f;
            if (bottomNavigationRect != null)
                bottomNavigationRect.localScale = Vector3.one;

            if (settingsCardRect != null)
            {
                const float settingsFullHeight = 1492f;
                float settingsScale = Mathf.Clamp(
                    height / settingsFullHeight, 0.82f, 1f);
                settingsCardRect.localScale = Vector3.one * settingsScale;
            }

            Vector2 primaryPosition = new Vector2(0f, compact ? 480f : 520f);
            Vector2 primarySize = compact
                ? new Vector2(460f, 171f)
                : new Vector2(560f, 220f);
            primaryActionRect.anchoredPosition = primaryPosition;
            primaryActionRect.sizeDelta = primarySize;
            if (levelButtonView != null
                && primaryActionRect == (levelButtonView.transform as RectTransform))
            {
                levelButtonView.SetNormalLayout(primaryPosition, primarySize);
            }
            RefreshNoticePosition(compact);
        }

        private void RefreshNoticePosition()
        {
            if (layoutAreaRect == null || layoutAreaRect.rect.height <= 0f) return;
            RefreshNoticePosition(layoutAreaRect.rect.height < 1680f);
        }

        private void RefreshNoticePosition(bool compact)
        {
            if (noticeRect == null) return;

            float topExtension = 0f;
            RectTransform levelRect = levelButtonView != null
                ? levelButtonView.transform as RectTransform
                : null;
            // Reserve space only when both elements share the same coordinate system. Some scenes place the
            // live button elsewhere.
            if (levelRect != null && levelRect.parent == noticeRect.parent)
                topExtension = levelButtonView.CurrentTopExtension;

            noticeRect.anchoredPosition = new Vector2(
                0f,
                (compact ? 600f : 670f) + topExtension);
        }
    }
}
