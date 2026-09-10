using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Runs the existing in-game shop's barrier, entrance and exit. Package art is decorative; UI objects are
    /// wired in the Inspector.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderInGameShopOverlay : MonoBehaviour
    {
        internal const double TransitionTimeoutSeconds = 4d;

        internal enum PresentationState
        {
            Hidden = 0,
            Opening = 1,
            Open = 2,
            Closing = 3,
        }

        internal sealed class PresentationContext
        {
            internal readonly long RunId;
            internal PresentationState State;
            internal Sequence Transition;
            internal BartenderLevelController BarrierController;
            internal double Deadline;

            internal PresentationContext(
                long runId, PresentationState state, double deadline)
            {
                RunId = runId;
                State = state;
                Deadline = deadline;
            }
        }

        private const float ScrimFadeSeconds = 0.12f;
        private const float SlideInSeconds = 0.34f;
        private const float SlideOutSeconds = 0.22f;

        private static readonly List<BartenderInGameShopOverlay> registered =
            new List<BartenderInGameShopOverlay>(2);

        [Header("Authored shop hierarchy")]
        [SerializeField] private GameObject screen;
        [SerializeField] private RectTransform canvasRect;
        [SerializeField] private CanvasGroup fade;
        [SerializeField] private Image scrim;
        [SerializeField] private RectTransform panel;
        [SerializeField] private Button closeButton;
        [SerializeField] private BartenderCoinBalanceView coinBalanceView;

        [Header("Gameplay owner")]
        [SerializeField] private BartenderLevelController roundController;

        private Color scrimVisibleColor;
        private float panelRestX;
        private float panelOffscreenX;
        private PresentationContext activePresentation;
        private long nextPresentationRunId;
        private bool prepared;
        private bool bindingErrorReported;

        /// <summary>True while the overlay is open.</summary>
        public bool IsOpen => activePresentation != null
                              && screen != null && screen.activeInHierarchy;
        internal PresentationState CurrentPresentationState => activePresentation != null
            ? activePresentation.State
            : PresentationState.Hidden;
        internal PresentationContext ActivePresentation => activePresentation;

        // Setup.

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => registered.Clear();

        private void OnEnable()
        {
            registered.Remove(this);
            registered.Add(this);
            EnsurePrepared();
        }

        /// <summary>
        /// Finds the gameplay overlay only in the active scene so menu requests cannot reach an older scene's
        /// instance.
        /// </summary>
        public static BartenderInGameShopOverlay FindInScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            for (int i = registered.Count - 1; i >= 0; i--)
            {
                BartenderInGameShopOverlay candidate = registered[i];
                if (candidate == null)
                {
                    registered.RemoveAt(i);
                    continue;
                }
                if (candidate.gameObject.scene == activeScene
                    && candidate.isActiveAndEnabled)
                    return candidate;
            }
            return null;
        }

        /// <summary>Opens the overlay, pauses the round and slides the card in from the left.</summary>
        public void Open()
        {
            if (!EnsurePrepared()) return;
            if (activePresentation != null
                && (activePresentation.State == PresentationState.Open
                    || activePresentation.State == PresentationState.Opening))
                return;

            PresentationContext context = BeginPresentationOperation(
                PresentationState.Opening);
            screen.SetActive(true);
            if (!FreezeRound(context))
            {
                TrySettle(context);
                return;
            }

            fade.alpha = 1f;
            fade.interactable = true;
            fade.blocksRaycasts = true;

            Color transparentScrim = scrimVisibleColor;
            transparentScrim.a = 0f;
            scrim.color = transparentScrim;

            RefreshPanelMotionBounds();

            // Reuse the delivery tray's slide sound.
            BsAudio.Instance?.Play(BsSfx.DeliverSlide);

            Sequence opening = DOTween.Sequence().SetUpdate(true);
            context.Transition = opening;
            opening.Join(scrim.DOColor(scrimVisibleColor, ScrimFadeSeconds)
                .SetEase(Ease.OutQuad).SetRecyclable(true));

            Vector2 rest = panel.anchoredPosition;
            panel.anchoredPosition = new Vector2(panelRestX, rest.y);
            opening.Join(panel.DOAnchorPosX(panelRestX, SlideInSeconds)
                .From(new Vector2(panelOffscreenX, rest.y))
                .SetEase(Ease.OutQuart).SetRecyclable(true));
            opening.OnComplete(() => CompleteOpening(context, opening));
            opening.OnKill(() => SettleKilledTransition(
                context, opening, PresentationState.Opening));
        }

        /// <summary>Closes the overlay and resumes the round, keeping the prefab instance loaded.</summary>
        public void Close()
        {
            PresentationContext context = activePresentation;
            if (context == null || context.State == PresentationState.Closing
                || screen == null || !screen.activeSelf)
                return;

            context.State = PresentationState.Closing;
            context.Deadline = Time.realtimeSinceStartupAsDouble
                + TransitionTimeoutSeconds;
            KillTransitionOnly(context);

            // Disable buttons now but keep the barrier until the card leaves, so taps cannot reach gameplay
            // mid-tween.
            fade.interactable = false;
            fade.blocksRaycasts = true;

            Sequence closing = DOTween.Sequence().SetUpdate(true);
            context.Transition = closing;
            closing.Join(panel.DOAnchorPosX(panelOffscreenX, SlideOutSeconds)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            closing.Join(scrim.DOFade(0f, SlideOutSeconds)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            closing.OnComplete(() => CompleteClosing(context, closing));
            closing.OnKill(() => SettleKilledTransition(
                context, closing, PresentationState.Closing));
        }

        internal PresentationContext BeginPresentationOperation(PresentationState next)
        {
            if (activePresentation != null)
                TrySettle(activePresentation);
            nextPresentationRunId = nextPresentationRunId == long.MaxValue
                ? 1L
                : nextPresentationRunId + 1L;
            var context = new PresentationContext(
                nextPresentationRunId,
                next,
                Time.realtimeSinceStartupAsDouble + TransitionTimeoutSeconds);
            activePresentation = context;
            return context;
        }

        private void CompleteOpening(PresentationContext context, Sequence completed)
        {
            if (!OwnsTransition(context, completed, PresentationState.Opening)) return;
            context.Transition = null;
            context.State = PresentationState.Open;
            context.Deadline = double.PositiveInfinity;
            if (fade != null)
            {
                fade.interactable = true;
                fade.blocksRaycasts = true;
            }
        }

        private void CompleteClosing(PresentationContext context, Sequence completed)
        {
            if (!OwnsTransition(context, completed, PresentationState.Closing)) return;
            context.Transition = null;
            TrySettle(context);
        }

        private void SettleKilledTransition(PresentationContext context,
                                            Sequence killed,
                                            PresentationState expectedState)
        {
            if (!OwnsTransition(context, killed, expectedState)) return;
            context.Transition = null;
            TrySettle(context);
        }

        /// <summary>
        /// Handles close, tween kill, disable and destroy together. Invalidate this operation before killing
        /// its tween so callbacks cannot close a later Open.
        /// </summary>
        internal bool TrySettle(PresentationContext expected)
        {
            if (expected == null || expected.RunId <= 0L
                || !ReferenceEquals(activePresentation, expected)) return false;

            // Clear ownership before cleanup callbacks so a killed tween cannot affect a newer request.
            expected.State = PresentationState.Hidden;
            activePresentation = null;
            Sequence running = expected.Transition;
            expected.Transition = null;
            BartenderLevelController barrier = expected.BarrierController;
            expected.BarrierController = null;
            Exception failure = ExecuteSettleCleanup(
                running != null
                    ? (Action)(() =>
                    {
                        if (running.IsActive()) running.Kill();
                    })
                    : null,
                () =>
                {
                    if (fade == null) return;
                    fade.interactable = false;
                    fade.blocksRaycasts = false;
                },
                barrier != null
                    ? (Action)(() => barrier.ReleasePresentationBarrier(expected))
                    : null,
                screen != null ? (Action)(() => screen.SetActive(false)) : null,
                RestoreAuthoredPose);
            if (failure != null) Debug.LogException(failure, this);
            return true;
        }

        /// <summary>
        /// Try all cleanup effects independently so one broken tween cannot skip input reset, barrier release
        /// or screen teardown.
        /// </summary>
        internal static Exception ExecuteSettleCleanup(
            Action stopTransition,
            Action disableInput,
            Action releaseBarrier,
            Action hideScreen,
            Action restorePose)
        {
            Exception failure = null;
            TryCleanupStep(stopTransition, ref failure);
            TryCleanupStep(disableInput, ref failure);
            TryCleanupStep(releaseBarrier, ref failure);
            TryCleanupStep(hideScreen, ref failure);
            TryCleanupStep(restorePose, ref failure);
            return failure;
        }

        private static void TryCleanupStep(Action step, ref Exception failure)
        {
            if (step == null) return;
            try
            {
                step();
            }
            catch (Exception exception)
            {
                if (failure == null) failure = exception;
            }
        }

        private bool OwnsTransition(PresentationContext context,
                                    Sequence transition,
                                    PresentationState expectedState) =>
            context != null && transition != null
            && ReferenceEquals(activePresentation, context)
            && ReferenceEquals(context.Transition, transition)
            && context.State == expectedState;

        private static void KillTransitionOnly(PresentationContext context)
        {
            if (context == null) return;
            Sequence running = context.Transition;
            context.Transition = null;
            if (running != null && running.IsActive()) running.Kill();
        }

        private void OnDestroy()
        {
            registered.Remove(this);
            TrySettle(activePresentation);
        }

        private void OnDisable()
        {
            registered.Remove(this);
            TrySettle(activePresentation);
        }

        /// <summary>
        /// Release the barrier whenever the view is hidden, even if a closing tween never finished.
        /// </summary>
        private void Update()
        {
            PresentationContext context = activePresentation;
            if (context == null) return;
            if (TrySettleExpiredPresentation(context,
                    Time.realtimeSinceStartupAsDouble)) return;
            if (context.BarrierController == null) return;
            if (context.State != PresentationState.Hidden
                && screen != null && screen.activeInHierarchy)
                return;
            TrySettle(context);
        }

        internal bool TrySettleExpiredPresentation(
            PresentationContext expected,
            double now)
        {
            if (expected == null || double.IsNaN(now) || double.IsInfinity(now)
                || (expected.State != PresentationState.Opening
                    && expected.State != PresentationState.Closing)
                || now < expected.Deadline)
                return false;
            return TrySettle(expected);
        }

        // Pause the round.

        private bool FreezeRound(PresentationContext context)
        {
            if (context == null
                || !ReferenceEquals(activePresentation, context)
                || roundController == null)
                return false;
            if (context.BarrierController != null) return true;
            if (roundController.AcquireVisiblePresentationBarrier(context, screen))
            {
                context.BarrierController = roundController;
                return true;
            }
            return false;
        }

        // Bind existing scene objects.

        private bool EnsurePrepared()
        {
            if (prepared) return true;
            if (!ValidateAuthoredBindings(out string reason))
            {
                if (!bindingErrorReported)
                {
                    bindingErrorReported = true;
                    Debug.LogError("In-game shop authored binding error: " + reason, this);
                }
                return false;
            }

            prepared = true;
            scrimVisibleColor = scrim.color;
            panelRestX = panel.anchoredPosition.x;
            closeButton.onClick.AddListener(Close);
            coinBalanceView.SetMoment(BartenderCoinBalanceView.BalanceMoment.Current);
            fade.interactable = false;
            fade.blocksRaycasts = false;
            screen.SetActive(false);
            return true;
        }

        public bool ValidateAuthoredBindings(out string reason)
        {
            if (screen == null)
            {
                reason = "Screen referansı eksik.";
                return false;
            }
            if (!BartenderLevelController.TryValidatePresentationRoot(
                    screen, out reason))
            {
                reason = "Shop görünüm kökü geçersiz: " + reason;
                return false;
            }
            if (!screen.scene.IsValid() || screen.scene != gameObject.scene)
            {
                reason = "Screen presenter ile aynı authored sahnede değil.";
                return false;
            }
            if (canvasRect == null || !canvasRect.IsChildOf(screen.transform))
            {
                reason = "Canvas Rect referansı eksik veya Screen altında değil.";
                return false;
            }
            if (fade == null || (fade.transform != screen.transform
                && !fade.transform.IsChildOf(screen.transform)))
            {
                reason = "CanvasGroup referansı eksik veya yanlış hiyerarşide.";
                return false;
            }
            if (scrim == null || !scrim.transform.IsChildOf(screen.transform)
                || !scrim.raycastTarget)
            {
                reason = "Raycast alan authored Scrim Image referansı eksik.";
                return false;
            }
            if (panel == null || !panel.IsChildOf(screen.transform))
            {
                reason = "Shop Panel referansı eksik.";
                return false;
            }
            if (closeButton == null || !closeButton.transform.IsChildOf(screen.transform))
            {
                reason = "Close Button referansı eksik.";
                return false;
            }
            if (coinBalanceView == null || !coinBalanceView.IsReady
                || !coinBalanceView.transform.IsChildOf(screen.transform))
            {
                reason = "Coin Balance View veya count label referansı eksik.";
                return false;
            }
            if (roundController == null)
            {
                reason = "Gameplay controller referansı eksik.";
                return false;
            }

            reason = null;
            return true;
        }

        private void RefreshPanelMotionBounds()
        {
            Canvas.ForceUpdateCanvases();

            RectTransform viewport = panel.parent as RectTransform;
            float viewportWidth = viewport != null ? viewport.rect.width : 0f;
            if (viewportWidth <= 0f && canvasRect != null)
                viewportWidth = canvasRect.rect.width;

            // Move left by one screen plus one panel width so the card clears every aspect ratio and prefab
            // scale.
            panelOffscreenX = panelRestX - Mathf.Abs(viewportWidth) - Mathf.Abs(panel.rect.width);
        }

        private void RestoreAuthoredPose()
        {
            if (!prepared) return;
            if (panel != null)
            {
                Vector2 position = panel.anchoredPosition;
                panel.anchoredPosition = new Vector2(panelRestX, position.y);
            }
            if (scrim != null) scrim.color = scrimVisibleColor;
        }
    }
}
