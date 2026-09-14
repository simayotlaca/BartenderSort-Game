using System;
using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderLoadingOverlayPresenter : MonoBehaviour
    {
        // Hippo, eyes, dots, drink and bar share one authored, non-looping clip.
        private const float LoadingTrackSeconds = 1.78f;
        private const float CompletionAudioFadeSeconds = 0.22f;
        private const float ExitSettleSeconds = 0.15f;
        private const float ExitSettleTailSeconds = 0.16f;
        private const float AbruptExitSeconds = 0.12f;

        private static readonly int ArtworkRestStateHash =
            Animator.StringToHash("Loading Artwork Rest");
        private static readonly int ArtworkStateHash =
            Animator.StringToHash("Loading Artwork Loop");

        [Header("Authored View")]
        [SerializeField] private Canvas loadingCanvas;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private TextMeshProUGUI titleLabel;
        private TextMeshProUGUI slowLoadLabel;

        [Header("Authored Animation")]
        [SerializeField] private Animator artworkAnimator;

        private bool reportedMissingAuthoredView;

        private int presentationVersion;
        private bool visible;
        private IDisposable musicSuspension;
        private bool closing;
        private bool holdingSceneActivation;
        private bool resolveAnnounced;
        private float visibleSinceUnscaledTime;
        private int presentationId;

        private static BartenderLoadingOverlayPresenter activePresentation;
        private static int presentationIdCounter;

        private bool longFormPresentation;
        private Tween exitPoseTween;

        public enum LoadingExitKind { Settle, SceneHandoff }

        public static event Action<LoadingExitKind, float> PresentationResolving;

        private static void AnnounceResolve(LoadingExitKind kind, float visibleSecondsLeft)
        {
            Action<LoadingExitKind, float> listeners = PresentationResolving;
            if (listeners == null) return;
            foreach (Delegate listener in listeners.GetInvocationList())
            {
                try
                {
                    ((Action<LoadingExitKind, float>)listener)(
                        kind, Mathf.Max(0f, visibleSecondsLeft));
                }
                catch (Exception exception)
                {
                    // One broken subscriber must not suppress the remaining audio cleanup.
                    Debug.LogException(exception);
                }
            }
        }

        // Clear static listeners when domain reload is off so old audio bridges cannot stay subscribed.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLoadingOverlayStatics()
        {
            PresentationResolving = null;
            activePresentation = null;
            presentationIdCounter = 0;
        }

        public bool Visible => visible;
        internal int PresentationVersion => presentationVersion;

        public static bool AnyVisible => activePresentation != null
                                      && activePresentation.visible;

        public static bool AnyVisibleIsLongForm => activePresentation != null
                                                && activePresentation.visible
                                                && activePresentation.longFormPresentation;

        public static int VisiblePresentationId => activePresentation != null
                                                && activePresentation.visible
                                                    ? activePresentation.presentationId
                                                    : 0;

        public void Prewarm()
        {
            if (!EnsureView()) return;
            // Unity cannot bind or sample an Animator on an inactive GameObject. Warm the
            // authored view while transparent so its first visible frame does not pay for binding.
            // A repeated prewarm must not interrupt an in-flight presentation.
            if (visible) return;
            try
            {
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = false;
                canvasGroup.interactable = false;
                if (!loadingCanvas.gameObject.activeSelf)
                    loadingCanvas.gameObject.SetActive(true);
                PrimeArtworkAnimator();
            }
            finally
            {
                HideImmediate();
            }
        }

        public bool Begin(bool longForm = false)
        {
            if (!EnsureView()) return false;
            try
            {
                KillExitTween();
                if (musicSuspension == null)
                    musicSuspension = BsAudio.SuspendBackgroundMusic();
                presentationVersion++;
                longFormPresentation = longForm;
                visible = true;
                closing = false;
                holdingSceneActivation = false;
                resolveAnnounced = false;
                activePresentation = this;
                presentationId = ++presentationIdCounter;
                visibleSinceUnscaledTime = Time.unscaledTime;
                SetSlowLoadNotice(false);

                if (!loadingCanvas.gameObject.activeSelf)
                    loadingCanvas.gameObject.SetActive(true);
                canvasGroup.alpha = 1f;
                canvasGroup.blocksRaycasts = true;
                canvasGroup.interactable = true;

                PlayArtwork();
                return true;
            }
            catch
            {
                // A partial Begin never leaves a canvas blocking input after its caller rejects it.
                HideImmediate();
                throw;
            }
        }

        internal void ReportSlowSceneLoad(int expectedPresentationVersion)
        {
            if (IsCurrentPresentation(expectedPresentationVersion)) SetSlowLoadNotice(true);
        }

        internal void ClearSlowSceneLoad(int expectedPresentationVersion)
        {
            if (IsCurrentPresentation(expectedPresentationVersion)) SetSlowLoadNotice(false);
        }

        private void SetSlowLoadNotice(bool show)
        {
            if (slowLoadLabel == null && show && titleLabel != null)
            {
                var notice = new GameObject("Slow Loading Notice", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                notice.layer = titleLabel.gameObject.layer;
                var rect = (RectTransform)notice.transform;
                rect.SetParent(titleLabel.rectTransform, false);
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = new Vector2(0f, -14f);
                rect.sizeDelta = new Vector2(0f, 64f);
                slowLoadLabel = notice.GetComponent<TextMeshProUGUI>();
                slowLoadLabel.font = titleLabel.font;
                slowLoadLabel.fontSharedMaterial = titleLabel.fontSharedMaterial;
                slowLoadLabel.color = Color.white;
                slowLoadLabel.alignment = TextAlignmentOptions.Center;
                slowLoadLabel.raycastTarget = false;
                slowLoadLabel.enableAutoSizing = true;
                slowLoadLabel.fontSize = 24f;
                slowLoadLabel.fontSizeMin = 18f;
                slowLoadLabel.fontSizeMax = 24f;
                slowLoadLabel.textWrappingMode = TextWrappingModes.Normal;
                slowLoadLabel.text = "THIS IS TAKING LONGER THAN USUAL.\nPLEASE KEEP THE GAME OPEN.";
            }
            if (slowLoadLabel != null) slowLoadLabel.gameObject.SetActive(show);
        }

        internal IEnumerator CompleteAndHide(int run)
        {
            if (!IsCurrentPresentation(run) || closing) yield break;
            yield return WaitForAnimationCompletion(run);
            if (!IsCurrentPresentation(run)) yield break;
            BeginExitSettle(run);
            yield return new WaitForSecondsRealtime(ExitSettleTailSeconds);
            if (IsCurrentPresentation(run)) HideImmediate();
        }

        internal IEnumerator WaitForAnimationCompletion(int run)
        {
            while (IsCurrentPresentation(run) && !closing
                   && Time.unscaledTime - visibleSinceUnscaledTime < LoadingTrackSeconds)
                yield return null;
            if (!IsCurrentPresentation(run) || closing) yield break;

            HoldCompletedArtwork();
            // Render the full bar and settled face before allowing synchronous scene activation.
            yield return null;
        }

        internal IEnumerator CancelAndHide(int run)
        {
            if (!IsCurrentPresentation(run) || closing) yield break;
            BeginExitSettle(run);
            yield return new WaitForSecondsRealtime(ExitSettleTailSeconds);
            if (IsCurrentPresentation(run)) HideImmediate();
        }

        public void HideImmediate()
        {
            IDisposable suspension = musicSuspension;
            musicSuspension = null;
            try
            {
                bool announceAbruptExit = visible && !closing && !resolveAnnounced;
                // Revoke ownership before invoking callbacks, including Animator and OnDisable callbacks.
                // A listener that calls HideImmediate again must see an already hidden presentation.
                presentationVersion++;
                visible = false;
                closing = false;
                holdingSceneActivation = false;
                resolveAnnounced = false;
                longFormPresentation = false;
                SetSlowLoadNotice(false);
                if (ReferenceEquals(activePresentation, this)) activePresentation = null;
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = 0f;
                    canvasGroup.blocksRaycasts = false;
                    canvasGroup.interactable = false;
                }
                KillExitTween();
                if (artworkAnimator != null) artworkAnimator.enabled = false;

                if (loadingCanvas != null && loadingCanvas.gameObject.activeSelf)
                    loadingCanvas.gameObject.SetActive(false);

                if (announceAbruptExit)
                    AnnounceResolve(LoadingExitKind.Settle, AbruptExitSeconds);
            }
            finally
            {
                suspension?.Dispose();
            }
        }

        private void Update()
        {
            if (!visible || closing || holdingSceneActivation) return;
            int run = presentationVersion;
            float elapsed = Time.unscaledTime - visibleSinceUnscaledTime;
            if (!resolveAnnounced && elapsed >= LoadingTrackSeconds - CompletionAudioFadeSeconds)
            {
                resolveAnnounced = true;
                AnnounceResolve(longFormPresentation ? LoadingExitKind.SceneHandoff : LoadingExitKind.Settle,
                    Mathf.Max(0f, LoadingTrackSeconds - elapsed));
            }
            if (IsCurrentPresentation(run) && !closing && elapsed >= LoadingTrackSeconds)
                HoldCompletedArtwork();
        }

        private void HoldCompletedArtwork()
        {
            if (holdingSceneActivation) return;
            if (!resolveAnnounced)
            {
                int run = presentationVersion;
                resolveAnnounced = true;
                AnnounceResolve(longFormPresentation ? LoadingExitKind.SceneHandoff : LoadingExitKind.Settle, 0f);
                if (!IsCurrentPresentation(run) || closing) return;
            }
            holdingSceneActivation = true;
            if (artworkAnimator == null) return;
            artworkAnimator.Play(ArtworkStateHash, 0, 1f);
            artworkAnimator.Update(0f);
            artworkAnimator.enabled = false;
        }

        private void OnDisable()
        {
            if (Application.isPlaying && visible) HideImmediate();
        }

        private void OnDestroy()
        {
            musicSuspension?.Dispose();
            musicSuspension = null;
            KillExitTween();
            if (ReferenceEquals(activePresentation, this)) activePresentation = null;
        }

        private bool EnsureView()
        {
            bool complete = loadingCanvas != null
                && canvasGroup != null
                && titleLabel != null
                && artworkAnimator != null
                && artworkAnimator.runtimeAnimatorController != null;

            string presentationRootError = null;
            if (complete && !BartenderLevelController.TryValidatePresentationRoot(
                    loadingCanvas.gameObject, out presentationRootError))
                complete = false;

            if (!complete)
            {
                if (!reportedMissingAuthoredView)
                {
                    reportedMissingAuthoredView = true;
                    Debug.LogError(
                        presentationRootError == null
                            ? "Royal Level Loading Canvas has incomplete authored Inspector bindings."
                            : "Royal Level Loading Canvas cannot be presented: "
                              + presentationRootError,
                        this);
                }
                return false;
            }

            return true;
        }

        private void PlayArtwork()
        {
            if (artworkAnimator == null) return;

            artworkAnimator.keepAnimatorStateOnDisable = true;
            artworkAnimator.enabled = true;
            artworkAnimator.Play(ArtworkStateHash, 0, 0f);
            artworkAnimator.Update(0f);
        }

        private void PrimeArtworkAnimator()
        {
            if (artworkAnimator == null || loadingCanvas == null
                || !loadingCanvas.gameObject.activeInHierarchy) return;

            bool restoredAnimatorEnabled = artworkAnimator.enabled;

            try
            {
                artworkAnimator.enabled = true;
                artworkAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
                artworkAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                artworkAnimator.keepAnimatorStateOnDisable = true;
                if (!artworkAnimator.isInitialized) artworkAnimator.Rebind();
                if (!artworkAnimator.HasState(0, ArtworkRestStateHash)) return;
                artworkAnimator.Play(ArtworkRestStateHash, 0, 0f);
                artworkAnimator.Update(0f);
            }
            finally
            {
                if (artworkAnimator != null)
                {
                    artworkAnimator.enabled = restoredAnimatorEnabled;
                }
            }
        }

        internal bool IsCurrentPresentation(int run)
        {
            return visible && presentationVersion == run;
        }

        private void BeginExitSettle(int run)
        {
            if (!IsCurrentPresentation(run) || closing) return;

            closing = true;
            if (!resolveAnnounced)
            {
                resolveAnnounced = true;
                AnnounceResolve(LoadingExitKind.Settle, ExitSettleSeconds);
            }
            if (!IsCurrentPresentation(run) || !closing) return;

            if (artworkAnimator != null) artworkAnimator.enabled = false;

            Sequence settle = DOTween.Sequence();
            settle.AppendInterval(ExitSettleSeconds);
            if (canvasGroup != null)
            {
                settle.Insert(0.03f, canvasGroup.DOFade(0f, 0.12f)
                    .SetEase(Ease.InQuad));
            }

            settle.SetUpdate(true).SetTarget(this);
            exitPoseTween = settle;
            settle.OnComplete(() =>
            {
                if (!ReferenceEquals(exitPoseTween, settle)) return;
                exitPoseTween = null;
                if (IsCurrentPresentation(run)) HideImmediate();
            });
        }

        private void KillExitTween()
        {
            Tween current = exitPoseTween;
            exitPoseTween = null;
            if (current != null && current.IsActive()) current.Kill(false);
        }
    }
}
