using BartenderSort.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows settings only after the controller accepts pause, and retries taps held by presentation locks.
    /// User-pause restores the card; focus-pause does not, and visual references stay optional.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderPausePresenter : MonoBehaviour
    {
        [Header("Rig references")]
        [Tooltip("Uses a component on this object if empty.")]
        [SerializeField] private BartenderSession session;
        [Tooltip("Pause/resume controller. Uses the session if empty.")]
        [SerializeField] private BartenderLevelController controller;

        [Header("Gameplay")]
        [Tooltip("Settings button. Active only during gameplay.")]
        [SerializeField] private Button pauseButton = null;

        [Header("Settings panel")]
        [Tooltip("Overlay shown while the game is paused.")]
        [SerializeField] private GameObject settingsOverlay = null;
        [Tooltip("The settings card. Leave empty if it is the overlay itself.")]
        [SerializeField] private GameObject settingsCard = null;
        [SerializeField] private Button closeButton = null;
        [SerializeField] private Button resumeButton = null;
        [SerializeField] private Button exitButton = null;

        [Header("Figma ayar kontrolleri")]
        [Tooltip("Background music volume.")]
        [SerializeField] private Slider musicVolumeSlider = null;
        [Tooltip("Game and UI sound volume.")]
        [SerializeField] private Slider soundVolumeSlider = null;
        [SerializeField] private Toggle vibrationToggle = null;
        [SerializeField] private Button restartButton = null;
        [Tooltip("Full-screen button that resumes on an outside tap.")]
        [SerializeField] private Button backdropButton = null;
        [Tooltip("Raycast blocker under Settings Overlay.")]
        [SerializeField] private Image overlayBlocker = null;

        [Header("Exit confirmation")]
        [SerializeField] private GameObject exitConfirmationCard = null;
        [SerializeField] private Button confirmExitButton = null;
        [SerializeField] private Button cancelExitButton = null;
        [SerializeField] private Button exitConfirmationCloseButton = null;

        private readonly BsPauseOverlayStateMachine overlayFlow =
            new BsPauseOverlayStateMachine();

        private BartenderSession subscribedSession;
        private BartenderLevelController subscribedController;
        private bool buttonsHooked;
        private bool pauseMenuRequestQueued;
        private bool authoredBindingErrorReported;

#if UNITY_EDITOR
        internal BsPauseOverlayState OverlayState => overlayFlow.State;
#endif
        private void Awake()
        {
            ResolveDependencies();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            ReportAuthoredBindingError();
            HookButtons();
            Subscribe();
            BartenderSettingsStore.SettingsChanged -= ApplySettingsMarks;
            BartenderSettingsStore.SettingsChanged += ApplySettingsMarks;
            ApplySettingsMarks();
            Project(session != null ? session.State : BsFlowState.Menu);
        }

        private void OnDisable()
        {
            pauseMenuRequestQueued = false;
            BartenderSettingsStore.SettingsChanged -= ApplySettingsMarks;
            Unsubscribe();
            UnhookButtons();
        }

        private void Update()
        {
            RefreshPauseButtonAvailability();
            FlushQueuedPauseMenuRequest();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private bool ValidateAuthoredBindings(out string reason)
        {
            string missing = null;
            if (settingsOverlay == null) missing = "Settings Overlay";
            else if (overlayBlocker == null) missing = "RaycastBlocker Image";
            else if (exitConfirmationCard == null) missing = "Exit Confirmation Card";
            else if (confirmExitButton == null) missing = "Confirm Exit Button";
            else if (cancelExitButton == null) missing = "Cancel Exit Button";
            else if (exitConfirmationCloseButton == null)
                missing = "Exit Confirmation Close Button";

            if (missing != null)
            {
                reason = missing + " must be assigned in the Inspector.";
                return false;
            }
            if (!BartenderLevelController.TryValidatePresentationRoot(
                    settingsOverlay, out reason))
            {
                reason = "Settings Overlay cannot be presented: " + reason;
                return false;
            }

            reason = null;
            return true;
        }

        private void ReportAuthoredBindingError()
        {
            if (authoredBindingErrorReported
                || ValidateAuthoredBindings(out string reason))
                return;
            authoredBindingErrorReported = true;
            Debug.LogError(
                "Pause authored hierarchy binding is incomplete: " + reason, this);
        }

        /// <summary>Sends Pause from the gear button; the state change opens the card.</summary>
        public void OpenPauseMenu()
        {
            if (!ValidateAuthoredBindings(out _))
            {
                pauseMenuRequestQueued = false;
                ReportAuthoredBindingError();
                return;
            }
            if (session == null || controller == null)
            {
                pauseMenuRequestQueued = false;
                return;
            }
            if (session.State != BsFlowState.Playing
                || overlayFlow.State != BsPauseOverlayState.Closed)
            {
                pauseMenuRequestQueued = false;
                return;
            }

            RequestPauseMenuNow();
        }

        private void RequestPauseMenuNow()
        {
            if (session == null || controller == null) return;

            bool blockedByPresentation = controller.PresentationLocked;
            bool accepted = controller.Pause();

            // Keep a tap during the presentation lock and retry when it releases.
            pauseMenuRequestQueued = !accepted
                                  && blockedByPresentation
                                  && session.State == BsFlowState.Playing
                                  && overlayFlow.State == BsPauseOverlayState.Closed;
        }

        private void FlushQueuedPauseMenuRequest()
        {
            if (!pauseMenuRequestQueued) return;
            if (session == null || controller == null
                || session.State != BsFlowState.Playing
                || overlayFlow.State != BsPauseOverlayState.Closed)
            {
                pauseMenuRequestQueued = false;
                return;
            }
            if (controller.PresentationLocked) return;
            pauseMenuRequestQueued = false;
            RequestPauseMenuNow();
        }

        /// <summary>X and Continue both send Paused -> Playing.</summary>
        public void ResumeGameplay()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.Settings) return;
            controller.Resume();
        }

        /// <summary>Leaves standalone tutorials directly; campaign exits open confirmation.</summary>
        public void RequestExitToMainMenu()
        {
            if (session == null || controller == null
                || session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.Settings) return;
            if (TryExitStandalone()) return;
            if (!overlayFlow.Dispatch(BsPauseOverlayTrigger.ExitRequested)) return;
            ApplyProjection(BsFlowState.Paused);
        }

        public void CancelExitToMainMenu()
        {
            if (session == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.ExitConfirmation) return;

            // Going back only changes the overlay. Continue or X must explicitly resume gameplay.
            if (!overlayFlow.Dispatch(BsPauseOverlayTrigger.ExitCancelled)) return;
            ApplyProjection(BsFlowState.Paused);
        }

        /// <summary>
        /// Saves abandonment and charges one life exactly once. Success shows the failure card; a save failure
        /// keeps confirmation open.
        /// </summary>
        public void ConfirmExitToMainMenu()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.ExitConfirmation) return;
            if (TryExitStandalone()) return;
            session.RequestQuitToFailureFromPause();
        }

        private bool TryExitStandalone()
        {
            if (controller == null || !controller.IsStandaloneRound) return false;
            // The tutorial director owns cleanup and menu navigation. Standalone runs have no life to
            // forfeit, so they must not enter the campaign abandonment confirmation.
            controller.RequestAbortStandalone(out _);
            return true;
        }

        /// <summary>
        /// Restarts the board within the same attempt. A successful load closes the overlay; rejection keeps
        /// pause and its panel.
        /// </summary>
        public void RestartGameplay()
        {
            if (session == null || controller == null) return;
            if (session.State != BsFlowState.Paused
                || overlayFlow.State != BsPauseOverlayState.Settings
                || controller.IsStandaloneRound) return;
            controller.TryRestartPausedAttempt(out _);
        }

        // Settings buttons.

        public void ToggleMusic()
        {
            PlayToggleSound(!BartenderSettingsStore.MusicOn);
            BartenderSettingsStore.ToggleMusic();
            ApplySettingsMarks();
        }

        public void ToggleSound()
        {
            // Play before muting when switching off, and after unmuting when switching on.
            bool wasOn = BartenderSettingsStore.SoundOn;
            if (wasOn) PlayToggleSound(false);
            bool enabled = BartenderSettingsStore.ToggleSound();
            if (!wasOn && enabled) PlayToggleSound(true);
            ApplySettingsMarks();
        }

        public void SetMusicVolume(float volume)
        {
            BartenderSettingsStore.SetMusicVolume(volume);
            ApplySettingsMarks();
        }

        public void SetSoundVolume(float volume)
        {
            BartenderSettingsStore.SetSoundVolume(volume);
            ApplySettingsMarks();
        }

        public void SetVibrationEnabled(bool enabled)
        {
            BartenderSettingsStore.SetVibrationOn(enabled);
            ApplySettingsMarks();
        }

        public void ToggleVibration()
        {
            PlayToggleSound(!BartenderSettingsStore.VibrationOn);
            BartenderSettingsStore.ToggleVibration();
            ApplySettingsMarks();
        }

        /// <summary>
        /// Plays on/off sounds for the settings buttons. For SFX, play before muting or after unmuting so the
        /// click remains audible.
        /// </summary>
        private static void PlayToggleSound(bool nowOn) =>
            BsAudio.UI(nowOn ? BsSfx.ToggleOn : BsSfx.ToggleOff);

        private void ApplySettingsMarks()
        {
            if (musicVolumeSlider)
                musicVolumeSlider.SetValueWithoutNotify(
                    BartenderSettingsStore.EffectiveMusicVolume);
            if (soundVolumeSlider)
                soundVolumeSlider.SetValueWithoutNotify(
                    BartenderSettingsStore.EffectiveSoundVolume);
            if (vibrationToggle)
            {
                vibrationToggle.SetIsOnWithoutNotify(
                    BartenderSettingsStore.VibrationOn);
                vibrationToggle.GetComponent<BartenderHapticToggleVisual>()?.Refresh();
            }
        }

        // Update the view.

        private void Subscribe()
        {
            if (subscribedSession == session && subscribedController == controller) return;
            Unsubscribe();
            subscribedSession = session;
            subscribedController = controller;
            if (subscribedSession != null) subscribedSession.FlowChanged += HandleFlowChanged;
            if (subscribedController != null)
                subscribedController.StateChanged += HandleControllerStateChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedSession != null) subscribedSession.FlowChanged -= HandleFlowChanged;
            if (subscribedController != null)
                subscribedController.StateChanged -= HandleControllerStateChanged;
            subscribedSession = null;
            subscribedController = null;
        }

        private void HandleFlowChanged(BsRoundTransition transition)
        {
            if (transition == null) return;
            Project(session != null ? session.State : BsFlowState.Menu);
        }

        private void HandleControllerStateChanged(BartenderLevelState _) =>
            Project(session != null ? session.State : BsFlowState.Menu);

        private void Project(BsFlowState state)
        {
            if (state != BsFlowState.Playing)
                pauseMenuRequestQueued = false;

            bool showUserPause = state == BsFlowState.Paused
                && controller != null && controller.IsUserPaused;
            if (!showUserPause)
            {
                if (overlayFlow.State != BsPauseOverlayState.Closed)
                    _ = overlayFlow.Dispatch(BsPauseOverlayTrigger.PauseEnded);
            }
            else if (overlayFlow.State == BsPauseOverlayState.Closed)
            {
                // A restored round has no new PlayerPause transition to replay.
                _ = overlayFlow.Dispatch(BsPauseOverlayTrigger.PauseAccepted);
            }

            ApplyProjection(state);
        }

        // Play sounds only when the visible card changes, since ApplyProjection can run without a transition.
        private BsPauseOverlayState lastVoicedOverlayState = BsPauseOverlayState.Closed;

        private void ApplyProjection(BsFlowState state)
        {
            bool playing = state == BsFlowState.Playing;
            bool paused = state == BsFlowState.Paused;
            bool presentPauseUi = paused
                               && overlayFlow.State != BsPauseOverlayState.Closed;
            bool settings = paused
                         && overlayFlow.State == BsPauseOverlayState.Settings;
            bool confirmation = paused
                                && overlayFlow.State == BsPauseOverlayState.ExitConfirmation;

            if (pauseButton)
                SetPauseButtonAvailability(playing
                                           && controller != null);

            if (settingsOverlay) settingsOverlay.SetActive(presentPauseUi);
            if (overlayBlocker)
                overlayBlocker.color = confirmation
                    ? new Color(0.035f, 0.012f, 0.08f, 0.72f)
                    : Color.clear;
            if (settingsCard && settingsCard != settingsOverlay)
                settingsCard.SetActive(settings);
            if (exitConfirmationCard) exitConfirmationCard.SetActive(confirmation);

            BsPauseOverlayState voiced = paused
                ? overlayFlow.State
                : BsPauseOverlayState.Closed;
            if (voiced != lastVoicedOverlayState)
            {
                bool opening = voiced != BsPauseOverlayState.Closed;
                bool wasOpen = lastVoicedOverlayState != BsPauseOverlayState.Closed;
                // Switching to confirmation plays an opening sound. Play closing only when the overlay fully
                // closes.
                if (opening) BsAudio.Instance?.Play(BsSfx.PopupOpen, 0.8f);
                else if (wasOpen) BsAudio.Instance?.Play(BsSfx.PopupClose, 0.8f);
                lastVoicedOverlayState = voiced;
            }

            SetInteractable(closeButton, settings);
            SetInteractable(resumeButton, settings);
            SetInteractable(exitButton, settings);
            SetInteractable(musicVolumeSlider, settings);
            SetInteractable(soundVolumeSlider, settings);
            SetInteractable(vibrationToggle, settings);
            SetInteractable(restartButton, settings
                && controller != null && !controller.IsStandaloneRound);
            SetInteractable(backdropButton, settings);
            SetInteractable(confirmExitButton, confirmation);
            SetInteractable(cancelExitButton, confirmation);
            SetInteractable(exitConfirmationCloseButton, confirmation);
        }

        private void RefreshPauseButtonAvailability()
        {
            if (!pauseButton) return;
            bool available = session != null && controller != null
                             && session.State == BsFlowState.Playing;
            SetPauseButtonAvailability(available);
        }

        private void SetPauseButtonAvailability(bool available)
        {
            if (!pauseButton) return;
            if (pauseButton.interactable != available)
                pauseButton.interactable = available;
            if (pauseButton.gameObject.activeSelf != available)
                pauseButton.gameObject.SetActive(available);
        }

        private static void SetInteractable(Selectable selectable, bool interactable)
        {
            if (selectable) selectable.interactable = interactable;
        }

        // Button bindings.

        private void HookButtons()
        {
            if (buttonsHooked) return;
            if (pauseButton) pauseButton.onClick.AddListener(OpenPauseMenu);
            if (closeButton)
                closeButton.onClick.AddListener(ResumeGameplay);
            if (resumeButton) resumeButton.onClick.AddListener(ResumeGameplay);
            if (exitButton) exitButton.onClick.AddListener(RequestExitToMainMenu);
            if (restartButton) restartButton.onClick.AddListener(RestartGameplay);
            if (backdropButton) backdropButton.onClick.AddListener(ResumeGameplay);
            if (confirmExitButton)
                confirmExitButton.onClick.AddListener(ConfirmExitToMainMenu);
            if (cancelExitButton)
                cancelExitButton.onClick.AddListener(CancelExitToMainMenu);
            if (exitConfirmationCloseButton)
                exitConfirmationCloseButton.onClick.AddListener(CancelExitToMainMenu);
            if (musicVolumeSlider)
                musicVolumeSlider.onValueChanged.AddListener(SetMusicVolume);
            if (soundVolumeSlider)
                soundVolumeSlider.onValueChanged.AddListener(SetSoundVolume);
            if (vibrationToggle)
                vibrationToggle.onValueChanged.AddListener(SetVibrationEnabled);
            buttonsHooked = true;
        }

        private void UnhookButtons()
        {
            if (!buttonsHooked) return;
            if (pauseButton) pauseButton.onClick.RemoveListener(OpenPauseMenu);
            if (closeButton) closeButton.onClick.RemoveListener(ResumeGameplay);
            if (resumeButton) resumeButton.onClick.RemoveListener(ResumeGameplay);
            if (exitButton) exitButton.onClick.RemoveListener(RequestExitToMainMenu);
            if (restartButton) restartButton.onClick.RemoveListener(RestartGameplay);
            if (backdropButton) backdropButton.onClick.RemoveListener(ResumeGameplay);
            if (confirmExitButton)
                confirmExitButton.onClick.RemoveListener(ConfirmExitToMainMenu);
            if (cancelExitButton)
                cancelExitButton.onClick.RemoveListener(CancelExitToMainMenu);
            if (exitConfirmationCloseButton)
                exitConfirmationCloseButton.onClick.RemoveListener(
                    CancelExitToMainMenu);
            if (musicVolumeSlider)
                musicVolumeSlider.onValueChanged.RemoveListener(SetMusicVolume);
            if (soundVolumeSlider)
                soundVolumeSlider.onValueChanged.RemoveListener(SetSoundVolume);
            if (vibrationToggle)
                vibrationToggle.onValueChanged.RemoveListener(SetVibrationEnabled);
            buttonsHooked = false;
        }
    }
}
