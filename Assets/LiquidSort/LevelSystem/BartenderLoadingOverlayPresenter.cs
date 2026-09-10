using System;
using System.Collections;
using System.Globalization;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows loading progress without changing game state. In-scene loads wait for whole sip loops; scene
    /// loads follow real progress.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderLoadingOverlayPresenter : MonoBehaviour
    {
        // I use 0.89-second loops to match the loading track's two-beat units at 134.8 BPM.
        private const float SipLoopSeconds = 0.89f;

        internal const float GlassLiquidMinimumFill = 0.18f;
        private const float GlassSipStartSeconds = 0.08f;
        private const float GlassSipEndSeconds = 0.58f;
        private const float GlassLiquidSipSeconds =
            GlassSipEndSeconds - GlassSipStartSeconds;

        // Feet stay at or above rest to hide the cropped ankles. Each slap drops faster than it rises.
        private const float FootPaddleRiseSeconds = 0.12f;
        private const float FootPaddleHoldSeconds = 0.04f;
        private const float FootPaddleFallSeconds = 0.07f;
        private const float FootPaddleLandSeconds = FootPaddleRiseSeconds
            + FootPaddleHoldSeconds + FootPaddleFallSeconds;
        private const float FootFlutterRiseSeconds = 0.1f;
        private const float FootFlutterFallSeconds = 0.08f;
        // Time for the splash to settle after a slap.
        private const float FootWaterTailSeconds = 0.3f;

        // The tray floats after each slap, with lift and roll a quarter cycle apart. Both must return to
        // rest before the loop ends.
        private const float TrayDriftSettleStartSeconds = 0.7f;
        private const float TrayDriftSettleSeconds = 0.12f;

        // I space the three dots evenly across one loop so the label keeps the beat without a long pause.
        private const float LoadingDotFirstBeatSeconds = 0.04f;
        private const float LoadingDotSettleSeconds = 0.09f;
        // These text dimensions match the old PNG frames. If the label size or timing changes, retune its
        // weight and shadow together.
        private const int LoadingTitleDotCount = 3;
        private const float LoadingTitleBounceEm = 0.1f;
        // I build the TMP tag with invariant culture so decimal commas cannot break it.
        private static readonly string LoadingTitleBounceTag =
            "<voffset=" + LoadingTitleBounceEm.ToString(CultureInfo.InvariantCulture)
            + "em>.</voffset>";
        internal const float CompletionFillSeconds = 0.16f;
        internal const double CompletionFillTimeoutSeconds = 2d;
        internal const float CompletedBarHoldSeconds = 0.06f;

        // The settle and fallback wait run together. The longer fallback guarantees cleanup without
        // extending the audio deadline.
        internal const float ExitSettleSeconds = 0.15f;
        internal const float ExitSettleTailSeconds = 0.16f;

        // Scene activation skips the settle and destroys this view. Resolve music just before activation,
        // within the full-bar hold.
        internal const float LongFormVisibleExitSeconds =
            CompletionFillSeconds + CompletedBarHoldSeconds;
        internal const float ShortFormVisibleExitSeconds =
            LongFormVisibleExitSeconds + ExitSettleSeconds;
        // Even an instant hide needs a short audio fade to avoid clicks.
        internal const float AbruptExitSeconds = 0.12f;

        // Two sips give the artwork one complete cycle. Audio fades to this deadline; it never extends it.
        // The fixed minimum also keeps muted timing identical.
        internal const float LoadingTrackSeconds = SipLoopSeconds * 2f;

        // Leave room in the bar for the remaining music hold so progress keeps moving after the load
        // finishes.
        internal const float SceneLoadCoveredCeiling = 0.62f;

        // The first milestone clears the pill's minimum width. The second stays small so the timed hold
        // owns most of the fill.
        internal const float OpeningFirstMilestoneProgress = 0.20f;
        internal const float OpeningFirstMilestoneSeconds = 0.26f;
        internal const float OpeningSecondMilestoneProgress = 0.30f;
        internal const float OpeningSecondMilestoneSeconds = 0.45f;
        internal const float CoveredLoadMilestoneProgress = 0.90f;
        internal const float CoveredLoadMilestoneSeconds = 0.14f;
        internal const float CoveredLoadMilestoneDisplaySeconds = 0.10f;
        internal const float SceneLoadMilestoneProgress = 0.96f;
        // Smooth coarse scene-load jumps so progress reads as motion.
        internal const float SceneLoadCatchUpSeconds = 0.28f;
        // Drift slows as it nears the milestone. This is a time constant, so a slow load keeps moving
        // without reaching the ceiling.
        internal const float SceneLoadDriftTimeConstant = 1.4f;

        // I hold in-scene loads for whole sip loops. Change the loop count to adjust screen time.
        private const int OpeningHoldSipLoops = 2;
        internal const float OpeningHoldSeconds = SipLoopSeconds * OpeningHoldSipLoops;
        // Two sips leave a little liquid so long loads can loop without exposing the sprite's transparent
        // bottom.
        private const float GlassLiquidDropPerSip =
            (1f - GlassLiquidMinimumFill) / OpeningHoldSipLoops;

        // Two sips drain 82%, so the hold fills 82% of the bar. Load milestones finish the rest.
        internal const float OpeningHoldFillCeiling = 0.82f;

        // Every slap and splash must finish within one loop or the sequence will stretch and drift off the
        // music.
        private static readonly float[] LeadFootPaddleBeats = { 0f };
        private static readonly float[] TrailFootPaddleBeats = { 0.26f };
        // These later slaps keep both feet from idling near the loop's end.
        private static readonly float[] LeadFootFlutterBeats = { 0.55f };
        private static readonly float[] TrailFootFlutterBeats = { 0.62f };

        private static readonly int ArtworkRestStateHash =
            Animator.StringToHash("Loading Artwork Rest");
        private static readonly int ArtworkLoopStateHash =
            Animator.StringToHash("Loading Artwork Loop");

        [Header("Authored View")]
        [SerializeField] private Canvas loadingCanvas;
        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private RectTransform barFrame;
        [SerializeField] private RectTransform frontCap;
        [SerializeField] private Image progressFill;
        [SerializeField] private TextMeshProUGUI titleLabel;

        [Header("Authored Animation")]
        [SerializeField] private Animator artworkAnimator;

        [Header("Dynamic Loading State")]
        [SerializeField] private Image glassLiquidBody;
        [SerializeField] private RectTransform glassLiquidCapRect;

        private bool authoredPoseCaptured;
        private bool reportedMissingAuthoredView;
        private Vector2 glassLiquidCapFullPosition;
        private float progressTrackWidth;
        private float progressTrackHeight;
        private float glassLiquidTravel;

        private float displayedProgress;
        private float targetProgress;
        private float progressUnitsPerSecond;
        private int presentationVersion;
        private bool visible;
        private IDisposable musicSuspension;
        private bool closing;
        private float visibleSinceUnscaledTime;
        private int presentationId;

        private static BartenderLoadingOverlayPresenter activePresentation;
        private static int presentationIdCounter;

        private bool longFormPresentation;
        private Tween exitPoseTween;
        private float artworkLoopStartedAtUnscaledTime;
        private float glassLiquidFill = 1f;

        /// <summary>Tells audio whether its ducked music survives this exit or is destroyed with the scene.</summary>
        public enum LoadingExitKind { Settle, SceneHandoff }

        /// <summary>
        /// Announces the remaining visible time when exit starts. Listeners cannot extend this deadline;
        /// audio must finish within it.
        /// </summary>
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

#if UNITY_EDITOR
        /// <summary>Length of one sip loop, which every beat inside it has to fit.</summary>
        internal static float SipLoopLengthSeconds => SipLoopSeconds;

        /// <summary>Last paddle effect's end time. It must fit within <see cref="SipLoopLengthSeconds"/>.</summary>
        internal static float FootPaddleEndSeconds
        {
            get
            {
                float end = 0f;
                foreach (float beat in LeadFootPaddleBeats)
                    end = Mathf.Max(end, FootPaddleBeatEnd(beat));
                foreach (float beat in TrailFootPaddleBeats)
                    end = Mathf.Max(end, FootPaddleBeatEnd(beat));
                return end;
            }
        }

        private static float FootPaddleBeatEnd(float beatStart)
        {
            // The water outlasts both the pose and the frame swap back to rest.
            return beatStart + FootPaddleLandSeconds + FootWaterTailSeconds;
        }

        /// <summary>Longest gap with neither foot moving. Keep it short so the loop does not look stuck.</summary>
        internal static float LongestFootStillnessSeconds
        {
            get
            {
                var spans = new System.Collections.Generic.List<Vector2>();
                foreach (float b in LeadFootPaddleBeats)
                    spans.Add(new Vector2(b, b + FootPaddleLandSeconds));
                foreach (float b in TrailFootPaddleBeats)
                    spans.Add(new Vector2(b, b + FootPaddleLandSeconds));
                float flutter = FootFlutterRiseSeconds + FootFlutterFallSeconds;
                foreach (float b in LeadFootFlutterBeats)
                    spans.Add(new Vector2(b, b + flutter));
                foreach (float b in TrailFootFlutterBeats)
                    spans.Add(new Vector2(b, b + flutter));
                spans.Sort((a, b) => a.x.CompareTo(b.x));

                float longest = spans.Count > 0 ? spans[0].x : SipLoopSeconds;
                float reached = spans.Count > 0 ? spans[0].y : 0f;
                for (int i = 1; i < spans.Count; i++)
                {
                    longest = Mathf.Max(longest, spans[i].x - reached);
                    reached = Mathf.Max(reached, spans[i].y);
                }
                return Mathf.Max(longest, SipLoopSeconds - reached);
            }
        }

        /// <summary>
        /// Time when the tray returns to rest. Its lift and roll must finish within the loop to avoid a
        /// visible snap.
        /// </summary>
        internal static float TrayDriftEndSeconds =>
            TrayDriftSettleStartSeconds + TrayDriftSettleSeconds;
#endif

        /// <summary>Dot spacing: three steps fill one loop so restarting stays on the beat.</summary>
        internal static float LoadingDotBeatSeconds => SipLoopSeconds / 3f;

#if UNITY_EDITOR
        /// <summary>Time when the last dot stops moving.</summary>
        internal static float LoadingDotsEndSeconds =>
            LoadingDotFirstBeatSeconds + 2f * LoadingDotBeatSeconds
            + LoadingDotSettleSeconds;
#endif
        public static bool AnyVisible => activePresentation != null
                                      && activePresentation.visible;

        /// <summary>
        /// True for scene loads, which follow destination progress. In-scene swaps use the fixed sip hold.
        /// </summary>
        public static bool AnyVisibleIsLongForm => activePresentation != null
                                                && activePresentation.visible
                                                && activePresentation.longFormPresentation;

        /// <summary>
        /// Current presentation ID, or zero when hidden.
        /// </summary>
        public static int VisiblePresentationId => activePresentation != null
                                                && activePresentation.visible
                                                    ? activePresentation.presentationId
                                                    : 0;

        public void Prewarm()
        {
            if (!EnsureView()) return;
            HideImmediate();
        }

        /// <param name="longForm">
        /// True for scene loads. In-scene swaps use <see cref="HoldOpeningBeat"/> instead.
        /// </param>
        public bool Begin(bool longForm = false)
        {
            if (!EnsureView()) return false;
            try
            {
                KillOwnedTweens();
                if (musicSuspension == null)
                    musicSuspension = BsAudio.SuspendBackgroundMusic();
                presentationVersion++;
                longFormPresentation = longForm;
                visible = true;
                closing = false;
                activePresentation = this;
                presentationId = ++presentationIdCounter;
                visibleSinceUnscaledTime = Time.unscaledTime;
                SetLoadingTitleFrame(1);
                targetProgress = 0f;
                progressUnitsPerSecond = 0f;
                SetProgressImmediate(0f);

                if (!loadingCanvas.gameObject.activeSelf)
                    loadingCanvas.gameObject.SetActive(true);
                // Reveal the cover immediately so the menu does not show through its moving artwork.
                canvasGroup.alpha = 1f;
                canvasGroup.blocksRaycasts = true;
                canvasGroup.interactable = true;
                barFrame.localScale = Vector3.one;
                ResetGlassLiquidLevel();

                StartArtworkLoop();
                return true;
            }
            catch
            {
                // A partial Begin never leaves a canvas blocking input after its caller rejects it.
                HideImmediate();
                throw;
            }
        }

        public void AdvanceTo(float normalizedProgress, float duration)
        {
            if (!visible || closing) return;
            float target = Mathf.Clamp01(normalizedProgress);
            target = Mathf.Max(displayedProgress, Mathf.Max(targetProgress, target));
            if (target <= targetProgress + 0.0001f) return;
            targetProgress = target;
            if (duration <= 0.001f)
            {
                SetProgressImmediate(target);
                progressUnitsPerSecond = 0f;
                return;
            }

            // I keep the progress target here to avoid rebuilding a tween on every report.
            progressUnitsPerSecond = Mathf.Max(0.001f,
                (targetProgress - displayedProgress) / duration);
        }

        public void AdvanceOpeningFirstMilestone()
            => AdvanceTo(OpeningFirstMilestoneProgress,
                         OpeningFirstMilestoneSeconds);

        public void AdvanceOpeningSecondMilestone()
            => AdvanceTo(OpeningSecondMilestoneProgress,
                         OpeningSecondMilestoneSeconds);

        public void ReportSceneLoadProgress(float normalizedProgress)
            => AdvanceTo(
                Mathf.Lerp(OpeningSecondMilestoneProgress,
                           SceneLoadReportingCeiling(),
                           Mathf.Clamp01(normalizedProgress)),
                SceneLoadCatchUpSeconds);

        /// <summary>Progress ceiling for the load itself. Reserve the rest for any remaining music hold.</summary>
        private float SceneLoadReportingCeiling()
            => longFormPresentation
               && CoverSecondsLeft() > LongFormVisibleExitSeconds
                ? SceneLoadCoveredCeiling
                : SceneLoadMilestoneProgress;

        /// <summary>
        /// Remaining handoff time since the artwork appeared. Negative means the load already outlasted the
        /// track.
        /// </summary>
        private float CoverSecondsLeft()
            => LoadingTrackSeconds - (Time.unscaledTime - visibleSinceUnscaledTime);

        public void AdvanceCoveredLoadMilestone()
            => AdvanceTo(CoveredLoadMilestoneProgress,
                         CoveredLoadMilestoneSeconds);

        /// <summary>
        /// Moves the bar while load reports are quiet. It slows near <paramref name="ceiling"/> without
        /// reaching it; real progress can still pull it ahead.
        /// </summary>
        public static float PacedSceneLoadProgress(float current, float ceiling,
                                                   float timeConstantSeconds,
                                                   float deltaSeconds)
        {
            float gap = ceiling - current;
            if (gap <= 0f || deltaSeconds <= 0f) return current;
            if (timeConstantSeconds <= 0.0001f) return ceiling;
            return current + gap * (1f - Mathf.Exp(-deltaSeconds / timeConstantSeconds));
        }

        /// <summary>Waits for the opening sip loops before an in-scene load. Scene loads skip this hold.</summary>
        public IEnumerator HoldOpeningBeat() => HoldOpeningBeat(presentationVersion);

        internal IEnumerator HoldOpeningBeat(int run)
        {
            if (!IsCurrentPresentation(run) || closing || longFormPresentation) yield break;
            float remaining = OpeningHoldSeconds
                              - (Time.unscaledTime - visibleSinceUnscaledTime);
            if (remaining <= 0f) yield break;
            yield return new WaitForSecondsRealtime(remaining);
        }

        public IEnumerator CompleteAndHide() => CompleteAndHide(presentationVersion);

        internal IEnumerator CompleteAndHide(int run)
        {
            if (!IsCurrentPresentation(run) || closing) yield break;
            yield return FillToCompletion(run);
            if (!IsCurrentPresentation(run)) yield break;
            BeginExitSettle(run);
            yield return new WaitForSecondsRealtime(ExitSettleTailSeconds);
            if (IsCurrentPresentation(run)) HideImmediate();
        }

        /// <summary>
        /// Fills the bar and renders a brief full hold. Scene callers must activate immediately afterward so
        /// the audio deadline still matches the exit.
        /// </summary>
        public IEnumerator FillToCompletion() => FillToCompletion(presentationVersion);

        internal IEnumerator FillToCompletion(int run, bool sceneAlreadyActivated = false)
        {
            if (!IsCurrentPresentation(run) || closing) yield break;

            // Keep the two-sip minimum before announcing its resolve. Keep the artwork looping
            // during this wait.
            if (longFormPresentation)
            {
                float coverEnd = visibleSinceUnscaledTime
                                 + LoadingTrackSeconds - LongFormVisibleExitSeconds;
                while (IsCurrentPresentation(run) && Time.unscaledTime < coverEnd)
                {
                    // Pace the remaining fill against the artwork's remaining visible time.
                    float paced = PacedOpeningProgress(
                        displayedProgress, 0f, coverEnd - Time.unscaledTime,
                        SceneLoadMilestoneProgress, Time.unscaledDeltaTime);
                    if (paced > displayedProgress) SetProgressImmediate(paced);
                    yield return null;
                }
                if (!IsCurrentPresentation(run)) yield break;
            }

            // Scene activation skips the settle, so its exit deadline is shorter.
            AnnounceResolve(
                longFormPresentation && !sceneAlreadyActivated
                    ? LoadingExitKind.SceneHandoff
                    : LoadingExitKind.Settle,
                longFormPresentation
                    ? LongFormVisibleExitSeconds
                    : ShortFormVisibleExitSeconds);
            // Resolve subscribers may synchronously replace or hide this presentation.
            if (!IsCurrentPresentation(run) || closing) yield break;

            targetProgress = 1f;
            progressUnitsPerSecond = Mathf.Max(0.001f,
                (1f - displayedProgress) / CompletionFillSeconds);
            double fillStartedAt = Time.realtimeSinceStartupAsDouble;
            while (IsCurrentPresentation(run) && displayedProgress < 0.9995f)
            {
                if (closing) yield break;
                if (Time.realtimeSinceStartupAsDouble - fillStartedAt
                    >= CompletionFillTimeoutSeconds)
                    throw new TimeoutException(
                        "Loading progress did not finish its visible fill within the presentation deadline.");
                yield return null;
            }
            if (!IsCurrentPresentation(run)) yield break;

            // Snap the last fraction to full before rendering so the cap meets the frame.
            SetProgressImmediate(1f);
            targetProgress = 1f;
            progressUnitsPerSecond = 0f;
            yield return null;
            if (!IsCurrentPresentation(run)) yield break;
            yield return new WaitForSecondsRealtime(CompletedBarHoldSeconds);
        }

        public IEnumerator CancelAndHide() => CancelAndHide(presentationVersion);

        internal IEnumerator CancelAndHide(int run)
        {
            if (!IsCurrentPresentation(run) || closing) yield break;
            BeginExitSettle(run);
            yield return new WaitForSecondsRealtime(ExitSettleTailSeconds);
            if (IsCurrentPresentation(run)) HideImmediate();
        }

        public void HideImmediate()
        {
            // Detach before callbacks: a replacement Begin owns a separate suspension.
            IDisposable suspension = musicSuspension;
            musicSuspension = null;
            try
            {
                bool announceAbruptExit = visible && !closing;
                // Revoke ownership before invoking callbacks, including Animator and OnDisable callbacks.
                // A listener that calls HideImmediate again must see an already hidden presentation.
                presentationVersion++;
                visible = false;
                closing = false;
                longFormPresentation = false;
                targetProgress = 0f;
                progressUnitsPerSecond = 0f;
                if (ReferenceEquals(activePresentation, this)) activePresentation = null;
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = 0f;
                    canvasGroup.blocksRaycasts = false;
                    canvasGroup.interactable = false;
                }
                KillOwnedTweens();
                ResetArtworkPose();
                ResetGlassLiquidLevel();
                SetProgressImmediate(0f);

                // I deactivate the hidden canvas to stop rendering work. Begin reactivates it; zero alpha alone
                // may not cull its meshes.
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
            if (!visible || closing) return;

            UpdateAuthoredLoopState();

            float milestoneNext = displayedProgress;
            if (milestoneNext + 0.0001f < targetProgress)
            {
                milestoneNext = Mathf.MoveTowards(milestoneNext, targetProgress,
                    progressUnitsPerSecond * Time.unscaledDeltaTime);
            }

            float next = milestoneNext;

            // Synchronous milestones finish too quickly, so in-scene fill follows the hold clock. Scene
            // loads use reported progress.
            if (!longFormPresentation)
            {
                next = MergeOpeningProgress(displayedProgress, milestoneNext,
                    Time.unscaledTime - visibleSinceUnscaledTime, OpeningHoldSeconds,
                    OpeningHoldFillCeiling, Time.unscaledDeltaTime);
            }
            else
            {
                // Use whichever is ahead: real load progress or gradual drift. Quiet load reports should
                // not freeze the bar.
                next = Mathf.Max(milestoneNext, PacedSceneLoadProgress(
                    displayedProgress, SceneLoadReportingCeiling(),
                    SceneLoadDriftTimeConstant, Time.unscaledDeltaTime));
            }

            if (next > displayedProgress)
                SetProgressImmediate(next);

            // Scene loads drain the drink with the bar because they have no fixed sip hold.
            if (longFormPresentation)
            {
                SetGlassLiquidLevel(Mathf.Lerp(
                    1f, GlassLiquidMinimumFill, displayedProgress));
            }
        }

        /// <summary>Use the greater of milestone and timed progress. Adding both would make the bar move too fast.</summary>
        internal static float MergeOpeningProgress(float current,
                                                   float milestoneProgress,
                                                   float elapsedSeconds,
                                                   float holdSeconds,
                                                   float ceiling,
                                                   float deltaSeconds)
        {
            float paced = PacedOpeningProgress(current, elapsedSeconds,
                                               holdSeconds, ceiling, deltaSeconds);
            return Mathf.Max(milestoneProgress, paced);
        }

        /// <summary>
        /// Moves current fill toward <paramref name="ceiling"/> by the hold's end. New milestones reset the
        /// starting point without moving back or overshooting.
        /// </summary>
        public static float PacedOpeningProgress(float current, float elapsedSeconds,
                                                 float holdSeconds, float ceiling,
                                                 float deltaSeconds)
        {
            if (current >= ceiling) return current;
            float remaining = holdSeconds - elapsedSeconds;
            if (remaining <= deltaSeconds) return ceiling;
            return current + (ceiling - current) * (deltaSeconds / remaining);
        }

        private void OnDisable()
        {
            // Only handle external disables here. HideImmediate already clears visible before disabling
            // this root.
            if (Application.isPlaying && visible) HideImmediate();
        }

        private void OnDestroy()
        {
            musicSuspension?.Dispose();
            musicSuspension = null;
            KillOwnedTweens();
            if (ReferenceEquals(activePresentation, this)) activePresentation = null;
        }

        private bool EnsureView()
        {
            bool complete = loadingCanvas != null
                && canvasGroup != null
                && barFrame != null
                && frontCap != null
                && progressFill != null
                && titleLabel != null
                && artworkAnimator != null
                && artworkAnimator.runtimeAnimatorController != null
                && glassLiquidBody != null
                && glassLiquidCapRect != null;

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

            if (!authoredPoseCaptured)
            {
                authoredPoseCaptured = true;
                glassLiquidCapFullPosition = glassLiquidCapRect.anchoredPosition;
                Vector2 trackSize = progressFill.rectTransform.sizeDelta;
                progressTrackWidth = Mathf.Abs(trackSize.x);
                progressTrackHeight = Mathf.Abs(trackSize.y);
                glassLiquidTravel = Mathf.Abs(
                    glassLiquidBody.rectTransform.sizeDelta.y);
            }

            if (progressTrackWidth <= 0.001f
                || progressTrackHeight <= 0.001f
                || glassLiquidTravel <= 0.001f)
            {
                authoredPoseCaptured = false;
                if (!reportedMissingAuthoredView)
                {
                    reportedMissingAuthoredView = true;
                    Debug.LogError(
                        "Royal Level Loading Canvas has invalid authored fill geometry.",
                        this);
                }
                return false;
            }

            return true;
        }

        private void SetLoadingTitleFrame(int dots, bool bounced = false)
        {
            if (titleLabel == null) return;

            int shown = Mathf.Clamp(dots, 1, LoadingTitleDotCount);
            string settled = new string('.', shown - 1);
            string newest = bounced ? LoadingTitleBounceTag : ".";
            // The trailing tag hides unused dots while keeping their width.
            string unlit = shown < LoadingTitleDotCount
                ? "<alpha=#00>" + new string('.', LoadingTitleDotCount - shown)
                : string.Empty;

            string copy = "LOADING" + settled + newest + unlit;
            if (titleLabel.text != copy) titleLabel.text = copy;
        }

        private void SetProgressImmediate(float value)
        {
            displayedProgress = Mathf.Clamp01(value);
            float radius = progressTrackHeight * 0.5f;
            bool showFill = displayedProgress > 0.0001f;
            // The pill needs three radii to hide the cap's cut edge. Show nothing below that width; the
            // opening milestone clears it.
            float minimumWidth = radius * 3f;
            float width = showFill
                ? Mathf.Max(minimumWidth, progressTrackWidth * displayedProgress)
                : 0f;

            if (progressFill != null)
            {
                float wantedFill = showFill
                    ? Mathf.Clamp01((width - radius) / progressTrackWidth)
                    : 0f;
                if (!Mathf.Approximately(progressFill.fillAmount, wantedFill))
                    progressFill.fillAmount = wantedFill;
            }

            if (frontCap != null)
            {
                if (frontCap.gameObject.activeSelf != showFill)
                    frontCap.gameObject.SetActive(showFill);
                if (showFill)
                {
                    var wantedPosition = new Vector2(
                        -progressTrackWidth * 0.5f + width - radius, 0f);
                    if (frontCap.anchoredPosition != wantedPosition)
                        frontCap.anchoredPosition = wantedPosition;
                }
            }
        }

        /// <summary>
        /// The prefab Animator holds two 0.89-second variants in one 1.78-second clip. It stays on the music
        /// without building runtime tweens.
        /// </summary>
        private void StartArtworkLoop()
        {
            if (artworkAnimator == null) return;

            artworkAnimator.enabled = true;
            artworkAnimator.Rebind();
            artworkAnimator.Play(ArtworkLoopStateHash, 0, 0f);
            artworkAnimator.Update(0f);
            artworkLoopStartedAtUnscaledTime = Time.unscaledTime;
            SetLoadingTitleFrame(1);
        }

        private void ResetArtworkPose()
        {
            if (artworkAnimator == null) return;

            if (artworkAnimator.gameObject.activeInHierarchy)
            {
                artworkAnimator.enabled = true;
                artworkAnimator.Rebind();
                artworkAnimator.Play(ArtworkRestStateHash, 0, 0f);
                artworkAnimator.Update(0f);
            }
            artworkAnimator.enabled = false;
        }

        private void UpdateAuthoredLoopState()
        {
            float elapsed = Mathf.Max(0f,
                Time.unscaledTime - artworkLoopStartedAtUnscaledTime);
            float phase = Mathf.Repeat(elapsed, SipLoopSeconds);

            int shownDots = 1;
            bool bounced = false;
            for (int dot = 1; dot <= LoadingTitleDotCount; dot++)
            {
                float beat = LoadingDotFirstBeatSeconds
                             + (dot - 1) * LoadingDotBeatSeconds;
                if (phase < beat) break;
                shownDots = dot;
                bounced = phase < beat + LoadingDotSettleSeconds;
            }
            SetLoadingTitleFrame(shownDots, bounced);

            if (longFormPresentation) return;

            int completedSips = Mathf.FloorToInt(elapsed / SipLoopSeconds);
            if (completedSips >= OpeningHoldSipLoops)
            {
                SetGlassLiquidLevel(GlassLiquidMinimumFill);
                return;
            }

            float startFill = 1f - completedSips * GlassLiquidDropPerSip;
            float endFill = Mathf.Max(GlassLiquidMinimumFill,
                startFill - GlassLiquidDropPerSip);
            if (phase <= GlassSipStartSeconds)
            {
                SetGlassLiquidLevel(startFill);
                return;
            }
            if (phase >= GlassSipEndSeconds)
            {
                SetGlassLiquidLevel(endFill);
                return;
            }

            float progress = (phase - GlassSipStartSeconds) / GlassLiquidSipSeconds;
            float eased = progress * progress * (3f - 2f * progress);
            SetGlassLiquidLevel(Mathf.Lerp(startFill, endFill, eased));
        }

        private void ResetGlassLiquidLevel()
        {
            SetGlassLiquidLevel(1f);
        }

        private void SetGlassLiquidLevel(float fill)
        {
            glassLiquidFill = Mathf.Clamp(fill, GlassLiquidMinimumFill, 1f);
            if (glassLiquidBody != null
                && !Mathf.Approximately(glassLiquidBody.fillAmount, glassLiquidFill))
            {
                glassLiquidBody.fillAmount = glassLiquidFill;
            }

            if (glassLiquidCapRect == null) return;
            Vector2 wantedPosition = glassLiquidCapFullPosition
                + Vector2.down * ((1f - glassLiquidFill) * glassLiquidTravel);
            if (glassLiquidCapRect.anchoredPosition != wantedPosition)
                glassLiquidCapRect.anchoredPosition = wantedPosition;
        }

        internal bool IsCurrentPresentation(int run)
        {
            return visible && presentationVersion == run;
        }

        private void BeginExitSettle(int run)
        {
            if (!IsCurrentPresentation(run) || closing) return;

            // Freeze at the exit frame so the Animator can blend back to rest. C# only handles the fade and
            // cleanup deadline.
            UpdateAuthoredLoopState();
            closing = true;
            AnnounceResolve(LoadingExitKind.Settle, ExitSettleSeconds);
            if (!IsCurrentPresentation(run) || !closing) return;
            SetLoadingTitleFrame(3);

            if (artworkAnimator != null && artworkAnimator.enabled)
            {
                artworkAnimator.CrossFadeInFixedTime(
                    ArtworkRestStateHash, ExitSettleSeconds, 0, 0f);
            }

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

        private void KillOwnedTweens()
        {
            KillTween(ref exitPoseTween);
        }

        private static void KillTween(ref Tween tween)
        {
            Tween current = tween;
            tween = null;
            if (current != null && current.IsActive()) current.Kill(false);
        }
    }
}
