using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// First Shift tutorial behaviour for the authored overlay prefab.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderFirstShiftOverlayView : MonoBehaviour
    {
        private static readonly Color DimColor =
            new Color(0.015f, 0.035f, 0.085f, 0.57f);

        [Header("Authored style")]
        [SerializeField] private BartenderFirstShiftStyle style;

        [Header("Authored hierarchy")]
        [SerializeField] private Canvas canvas;
        [SerializeField] private RectTransform canvasRect;
        [SerializeField] private CanvasGroup overlayGroup;
        [SerializeField] private CanvasGroup panelGroup;
        [SerializeField] private Image fullScrim;
        [SerializeField] private Button advanceButton;
        [SerializeField] private Image[] focusScrims = new Image[4];
        [SerializeField] private RectTransform panelRect;
        [SerializeField] private Image panelImage;
        [SerializeField] private Image hippoImage;
        [SerializeField] private TMP_Text messageLabel;
        [SerializeField] private RectTransform focusRoot;
        [SerializeField] private Image focusHalo;
        [SerializeField] private RectTransform handRect;
        [SerializeField] private Image handImage;

        private Transform focusWorldTarget;
        private Camera focusCamera;
        private RectTransform focusUiTarget;
        private Coroutine fadeRoutine;
        private Coroutine nudgeRoutine;
        private bool wantsHand;
        private bool wantsHalo;
        private bool compactFocus;
        private bool built;
        private bool advanceListenerBound;
        private bool advanceCapture;
        private bool outsideFocusCapture;
        private bool panelLayoutCaptured;
        private bool panelFollowsUiTarget;
        private Vector2 panelRestPosition;
        private Vector2 panelPresentationPosition;
        private Vector3 panelRestScale = Vector3.one;
        private Vector3 panelPresentationScale = Vector3.one;
        private Color fullScrimRestColor = DimColor;
        private readonly Vector3[] uiTargetWorldCorners = new Vector3[4];

        public event Action AdvanceRequested;

        public bool Visible => overlayGroup != null && overlayGroup.alpha > 0.001f;

        public bool ValidateAuthoredBindings(out string reason)
        {
            if (!HasSerializedHierarchy())
            {
                reason = "The authored tutorial hierarchy is incomplete.";
                return false;
            }
            if (!BartenderLevelController.TryValidatePresentationRoot(
                    gameObject, out reason))
            {
                reason = "The tutorial overlay cannot be presented: " + reason;
                return false;
            }

            reason = null;
            return true;
        }

        private void Awake()
        {
            built = ValidateAuthoredBindings(out string reason);
            if (!built)
            {
                Debug.LogError(
                    "First Shift Tutorial Overlay binding error: " + reason,
                    this);
                enabled = false;
                return;
            }
            BindAdvanceButton();
            CapturePanelRestLayout();
            if (Application.isPlaying) HideImmediate();
        }

        private void OnDisable()
        {
            if (fadeRoutine != null) StopCoroutine(fadeRoutine);
            fadeRoutine = null;
            if (nudgeRoutine != null) StopCoroutine(nudgeRoutine);
            nudgeRoutine = null;
            if (built) HideImmediate();
            else ClearFocus();
        }

        private void OnDestroy()
        {
            if (advanceListenerBound && advanceButton != null)
                advanceButton.onClick.RemoveListener(HandleAdvancePressed);
        }

        private void LateUpdate()
        {
            if (focusRoot == null || !focusRoot.gameObject.activeSelf) return;

            bool uiFocus = focusUiTarget != null;
            Rect screenRect;
            bool hasScreenRect = uiFocus
                ? TryGetRectTransformScreenRect(focusUiTarget, out screenRect)
                : TryGetWorldTargetScreenRect(
                    focusWorldTarget, focusCamera, out screenRect);
            if (!hasScreenRect)
            {
                // Bounds may disappear for one refresh frame. Keep checking so the highlight can recover.
                SetScrimMode(false);
                if (fullScrim != null)
                    fullScrim.raycastTarget = advanceCapture || outsideFocusCapture;
                if (focusHalo != null) focusHalo.enabled = false;
                if (handImage != null) handImage.enabled = false;
                return;
            }

            SetScrimMode(true);
            if (fullScrim != null) fullScrim.raycastTarget = advanceCapture;
            focusHalo.enabled = wantsHalo && focusHalo.sprite != null;
            if (handImage != null)
                handImage.enabled = wantsHand && handImage.sprite != null;

            Vector2 screenCenter = screenRect.center;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, screenCenter, null, out Vector2 localCenter)) return;

            float scaleFactor = canvas != null ? Mathf.Max(0.01f, canvas.scaleFactor) : 1f;
            if (uiFocus)
            {
                if (panelFollowsUiTarget)
                    PositionPanelBelowUiTarget(screenRect);
                float minimumWidth = compactFocus ? 170f : 260f;
                float minimumHeight = compactFocus ? 130f : 210f;
                float widthPadding = compactFocus ? 1.30f : 1.16f;
                float heightPadding = compactFocus ? 1.38f : 1.18f;
                float cardWidth = Mathf.Max(minimumWidth,
                    screenRect.width / scaleFactor * widthPadding);
                float cardHeight = Mathf.Max(minimumHeight,
                    screenRect.height / scaleFactor * heightPadding);
                focusRoot.anchoredPosition = localCenter;
                focusRoot.sizeDelta = new Vector2(cardWidth, cardHeight);
                UpdateSpotlightWindow(localCenter, new Vector2(cardWidth, cardHeight));
                float focusPulse = 1f + Mathf.Sin(Time.unscaledTime * 4.5f) * 0.045f;
                focusHalo.rectTransform.localScale = Vector3.one * focusPulse;
                PositionCompactHand(cardWidth, cardHeight);
                return;
            }

            if (compactFocus)
            {
                float compactWidth = Mathf.Max(190f,
                    screenRect.width / scaleFactor * 1.34f);
                float compactHeight = Mathf.Max(170f,
                    screenRect.height / scaleFactor * 1.36f);
                focusRoot.anchoredPosition = localCenter;
                focusRoot.sizeDelta = new Vector2(compactWidth, compactHeight);
                UpdateSpotlightWindow(localCenter,
                    new Vector2(compactWidth, compactHeight));
                float compactPulse = 1f + Mathf.Sin(Time.unscaledTime * 4.5f) * 0.045f;
                focusHalo.rectTransform.localScale = Vector3.one * compactPulse;
                PositionCompactHand(compactWidth, compactHeight);
                return;
            }

            float targetWidth = Mathf.Max(245f, screenRect.width / scaleFactor * 2.05f);
            float targetHeight = Mathf.Max(350f, screenRect.height / scaleFactor * 2.15f);
            // Raise the light sprite so its lower ellipse sits under the glass and its cone points up.
            focusRoot.anchoredPosition = localCenter + Vector2.up * targetHeight * 0.16f;
            UpdateSpotlightWindow(localCenter,
                new Vector2(targetWidth * 1.18f, targetHeight * 1.08f));
            float pulse = 1f + Mathf.Sin(Time.unscaledTime * 4.5f) * 0.055f;
            focusRoot.sizeDelta = new Vector2(targetWidth * 1.31f, targetHeight * 1.22f);
            focusHalo.rectTransform.localScale = Vector3.one * pulse;
            if (handRect != null)
            {
                // Restore the large glass pointer after smaller card or timer highlights.
                handRect.sizeDelta = new Vector2(180f, 205f);
                float bob = Mathf.Sin(Time.unscaledTime * 3.7f) * 5f;
                handRect.anchoredPosition = new Vector2(
                    targetWidth * 0.16f, targetHeight * 0.28f + bob);
            }
        }

        public void Show(string message, BartenderFirstShiftPose pose,
                         LiquidBottle target, Camera targetCamera,
                         bool showHand = true, bool animate = true,
                         bool advanceOnTap = false)
        {
            ShowInternal(message, pose,
                target != null ? target.transform : null, targetCamera, null,
                showHand, true, false, animate, advanceOnTap, 0f);
        }

        public void ShowUiTarget(string message, BartenderFirstShiftPose pose,
                                 RectTransform target, bool animate = true,
                                 bool advanceOnTap = true)
        {
            ShowInternal(message, pose, null, null, target,
                false, false, false, animate, advanceOnTap, 0f,
                placePanelBelowTarget: true);
        }

        /// <summary>
        /// Highlights a UI control with the shared gold ring and hand. First Shift keeps its existing
        /// ShowUiTarget look.
        /// </summary>
        public void ShowUiTargetWithCoachMark(
            string message, BartenderFirstShiftPose pose, RectTransform target,
            bool compact = true, bool animate = true, bool advanceOnTap = true,
            float panelYOffset = 0f)
        {
            ShowInternal(message, pose, null, null, target,
                true, true, compact, animate, advanceOnTap, panelYOffset);
        }

        /// <summary>
        /// Highlights a world-space sprite control. Taps still belong to Continue; this never activates or
        /// buys the target.
        /// </summary>
        public void ShowWorldTargetWithCoachMark(
            string message, BartenderFirstShiftPose pose, Transform target,
            Camera targetCamera, bool animate = true, bool advanceOnTap = true,
            float panelYOffset = 0f)
        {
            ShowInternal(message, pose, target, targetCamera, null,
                true, true, true, animate, advanceOnTap, panelYOffset);
        }

        // Keep bubble and voice sounds below SFX_StepComplete so the successful action is easiest to hear.
        private const float PopVolume = 0.38f;
        private const float BlipVolume = 0.26f;

        // Eight varied step pitches, kept near the F# minor scale within about two semitones.
        private static readonly float[] BlipPitches =
            { 1.00f, 1.06f, 0.95f, 1.12f, 0.98f, 1.03f, 0.92f, 1.09f };

        private int stepVoiceIndex;

        private void ShowInternal(string message, BartenderFirstShiftPose pose,
                                  Transform worldTarget, Camera targetCamera,
                                  RectTransform uiTarget, bool showHand,
                                  bool showHalo, bool useCompactFocus,
                                  bool animate, bool advanceOnTap,
                                  float panelYOffset,
                                  bool placePanelBelowTarget = false)
        {
            EnsureBuilt();
            if (!built) return;
            EnsurePanelRestLayout();

            messageLabel.text = message ?? string.Empty;
            Sprite hippo = style != null ? style.Pose(pose) : null;
            hippoImage.sprite = hippo;
            hippoImage.enabled = hippo != null;
            focusWorldTarget = worldTarget;
            focusCamera = targetCamera;
            focusUiTarget = uiTarget;
            panelFollowsUiTarget = placePanelBelowTarget && uiTarget != null;
            wantsHand = showHand;
            wantsHalo = showHalo;
            compactFocus = useCompactFocus;
            bool hasFocus = uiTarget != null
                || (worldTarget != null && targetCamera != null);
            ConfigureFocusArtwork(hasFocus);
            focusRoot.gameObject.SetActive(hasFocus);
            handImage.gameObject.SetActive(hasFocus && showHand && handImage.sprite != null);
            SetScrimMode(hasFocus);

            overlayGroup.gameObject.SetActive(true);
            panelGroup.gameObject.SetActive(true);
            // The overlay group owns the fade; the authored panel starts transparent.
            panelGroup.alpha = 1f;
            SetInputCapture(advanceOnTap, hasFocus && !advanceOnTap);
            if (nudgeRoutine != null) StopCoroutine(nudgeRoutine);
            nudgeRoutine = null;
            panelPresentationPosition = panelRestPosition
                + Vector2.up * panelYOffset;
            // Order explanations fit between the card and the first shelf.
            panelPresentationScale = panelRestScale * (panelFollowsUiTarget ? 0.65f : 1f);
            panelRect.anchoredPosition = panelPresentationPosition;
            panelRect.localScale = animate ? panelPresentationScale * 0.92f : panelPresentationScale;
            if (panelFollowsUiTarget
                && TryGetRectTransformScreenRect(focusUiTarget, out Rect targetScreenRect))
                PositionPanelBelowUiTarget(targetScreenRect);
            StartFade(1f, animate ? 0.18f : 0f);
            if (animate) PlayStepVoice();
        }

        /// <summary>Plays the pop and voice with a cycling pitch. Restored steps skip this call.</summary>
        private void PlayStepVoice()
        {
            BsAudio audio = BsAudio.Instance;
            if (audio == null) return;
            audio.Play(BsSfx.TutorialPop, PopVolume);
            float pitch = BlipPitches[stepVoiceIndex % BlipPitches.Length];
            stepVoiceIndex++;
            audio.Play(BsSfx.HippoBlip, BlipVolume, pitch);
        }

        public void SuspendForGameplayPresentation()
        {
            if (!isActiveAndEnabled)
            {
                HideImmediate();
                return;
            }
            SetInputCapture(false, false);
            ClearFocus();
            StartFade(0f, 0.10f);
        }

        public void HideImmediate()
        {
            // Children may be destroyed before another director's OnDisable. Check the CanvasGroup itself;
            // built alone does not prove it exists.
            if (!built || !HasSerializedHierarchy())
            {
                built = false;
                ClearFocus();
                return;
            }
            if (fadeRoutine != null) StopCoroutine(fadeRoutine);
            fadeRoutine = null;
            overlayGroup.alpha = 0f;
            SetInputCapture(false, false);
            overlayGroup.gameObject.SetActive(false);
            if (nudgeRoutine != null) StopCoroutine(nudgeRoutine);
            nudgeRoutine = null;
            EnsurePanelRestLayout();
            panelPresentationPosition = panelRestPosition;
            panelPresentationScale = panelRestScale;
            panelRect.anchoredPosition = panelRestPosition;
            panelRect.localScale = panelRestScale;
            ClearFocus();
        }

        public void Nudge()
        {
            if (!built || !panelGroup.gameObject.activeInHierarchy) return;
            if (nudgeRoutine != null) StopCoroutine(nudgeRoutine);
            EnsurePanelRestLayout();
            panelRect.anchoredPosition = panelPresentationPosition;
            nudgeRoutine = StartCoroutine(NudgeRoutine());
        }

        private void ClearFocus()
        {
            focusWorldTarget = null;
            focusCamera = null;
            focusUiTarget = null;
            wantsHand = false;
            wantsHalo = false;
            compactFocus = false;
            panelFollowsUiTarget = false;
            if (focusRoot != null) focusRoot.gameObject.SetActive(false);
        }

        private void ConfigureFocusArtwork(bool hasFocus)
        {
            if (!hasFocus || focusHalo == null) return;
            focusHalo.preserveAspect = false;
        }

        private void PositionPanelBelowUiTarget(Rect targetScreenRect)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, new Vector2(targetScreenRect.center.x, targetScreenRect.yMin),
                    null, out Vector2 targetBottom)) return;

            // Include the badge, which extends past the speech panel's authored rectangle.
            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
                canvasRect, panelRect);
            const float gap = 24f;
            Vector2 offset = new Vector2(canvasRect.rect.center.x - bounds.center.x,
                targetBottom.y - gap - bounds.max.y);
            panelPresentationPosition = panelRect.anchoredPosition + offset;
            panelRect.anchoredPosition = panelPresentationPosition;
        }

        private void PositionCompactHand(float targetWidth, float targetHeight)
        {
            if (handRect == null) return;
            handRect.sizeDelta = compactFocus
                ? new Vector2(130f, 148f)
                : new Vector2(150f, 171f);
            float bob = Mathf.Sin(Time.unscaledTime * 3.7f) * 5f;
            // Place the palm below-right so the up-left fingertip lands inside the highlight.
            handRect.anchoredPosition = new Vector2(
                targetWidth * 0.34f, -targetHeight * 0.34f + bob);
        }

        private void StartFade(float target, float duration)
        {
            if (fadeRoutine != null) StopCoroutine(fadeRoutine);
            fadeRoutine = StartCoroutine(FadeRoutine(target, duration));
        }

        private IEnumerator FadeRoutine(float target, float duration)
        {
            float start = overlayGroup.alpha;
            Vector3 startScale = panelRect.localScale;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = duration <= 0f ? 1f : Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                overlayGroup.alpha = Mathf.Lerp(start, target, eased);
                panelRect.localScale = Vector3.Lerp(startScale,
                    panelPresentationScale, eased);
                yield return null;
            }
            overlayGroup.alpha = target;
            panelRect.localScale = panelPresentationScale;
            fadeRoutine = null;
            if (target <= 0.001f) overlayGroup.gameObject.SetActive(false);
        }

        private IEnumerator NudgeRoutine()
        {
            Vector2 origin = panelPresentationPosition;
            const float duration = 0.22f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                panelRect.anchoredPosition = origin
                    + Vector2.right * Mathf.Sin(t * Mathf.PI * 4f) * (1f - t) * 13f;
                yield return null;
            }
            panelRect.anchoredPosition = origin;
            nudgeRoutine = null;
        }

        private void EnsureBuilt()
        {
            built = ValidateAuthoredBindings(out _);
        }

        private void BindAdvanceButton()
        {
            if (advanceListenerBound || advanceButton == null) return;
            advanceButton.onClick.AddListener(HandleAdvancePressed);
            advanceListenerBound = true;
        }

        private void HandleAdvancePressed()
        {
            if (advanceButton == null || !advanceButton.interactable || !Visible) return;
            AdvanceRequested?.Invoke();
        }

        private void SetInputCapture(bool advanceOnTap, bool blockOutsideFocus)
        {
            advanceCapture = advanceOnTap;
            outsideFocusCapture = blockOutsideFocus;
            if (overlayGroup != null)
            {
                overlayGroup.blocksRaycasts = advanceOnTap || blockOutsideFocus;
                overlayGroup.interactable = advanceOnTap || blockOutsideFocus;
            }
            if (fullScrim != null) fullScrim.raycastTarget = advanceOnTap;
            if (advanceButton != null) advanceButton.interactable = advanceOnTap;
            if (focusScrims == null) return;
            for (int i = 0; i < focusScrims.Length; i++)
                if (focusScrims[i] != null)
                    focusScrims[i].raycastTarget = blockOutsideFocus;
        }

        private void CapturePanelRestLayout()
        {
            if (panelRect == null) return;
            panelRestPosition = panelRect.anchoredPosition;
            panelPresentationPosition = panelRestPosition;
            panelRestScale = panelRect.localScale;
            panelPresentationScale = panelRestScale;
            if (fullScrim != null) fullScrimRestColor = fullScrim.color;
            panelLayoutCaptured = true;
        }

        private void EnsurePanelRestLayout()
        {
            if (!panelLayoutCaptured) CapturePanelRestLayout();
        }

        private bool HasSerializedHierarchy()
        {
            if (canvas == null || canvasRect == null || overlayGroup == null
                || panelGroup == null || fullScrim == null || advanceButton == null
                || panelRect == null
                || panelImage == null || hippoImage == null || messageLabel == null
                || focusRoot == null || focusHalo == null || handRect == null
                || handImage == null || focusScrims == null
                || focusScrims.Length != 4 || panelImage.sprite == null
                || hippoImage.sprite == null || messageLabel.font == null
                || messageLabel.fontSharedMaterial == null
                || focusHalo.sprite == null || handImage.sprite == null) return false;

            for (int i = 0; i < focusScrims.Length; i++)
                if (focusScrims[i] == null) return false;
            return true;
        }

#if UNITY_EDITOR
        /// <summary>Preview and runtime use the same copy asset so tutorial text stays in sync.</summary>
        private static string[] EditorPreviewMessages =>
            BartenderTutorialCopy.Resolve().FirstShiftSequence;

        private static readonly BartenderFirstShiftPose[] EditorPreviewPoses =
        {
            BartenderFirstShiftPose.Point,
            BartenderFirstShiftPose.Point,
            BartenderFirstShiftPose.Celebrate,
            BartenderFirstShiftPose.Celebrate,
        };

        public int EditorPreviewStepCount => EditorPreviewMessages.Length;

        public void EditorSetWelcomePreview(bool visible)
            => EditorSetStepPreview(0, visible);

        public void EditorSetStepPreview(int stepIndex, bool visible = true)
        {
            if (Application.isPlaying || !HasSerializedHierarchy()) return;
            built = true;

            canvas.gameObject.SetActive(true);
            overlayGroup.alpha = visible ? 1f : 0f;
            SetInputCapture(false, false);
            panelGroup.gameObject.SetActive(true);
            panelGroup.alpha = 1f;
            panelGroup.blocksRaycasts = false;
            panelGroup.interactable = false;
            CapturePanelRestLayout();
            panelRect.anchoredPosition = panelRestPosition;
            panelRect.localScale = panelRestScale;
            int safeIndex = Mathf.Clamp(stepIndex, 0, EditorPreviewMessages.Length - 1);
            messageLabel.text = EditorPreviewMessages[safeIndex];
            Sprite welcome = style != null
                ? style.Pose(EditorPreviewPoses[safeIndex])
                : null;
            hippoImage.sprite = welcome;
            hippoImage.enabled = welcome != null;
            ClearFocus();
            SetScrimMode(false);
        }
#endif

        private void SetScrimMode(bool focused)
        {
            if (fullScrim != null)
            {
                // Keep the transparent full-screen Button for Continue taps while four masks draw the
                // spotlight.
                fullScrim.gameObject.SetActive(true);
                fullScrim.color = focused
                    ? new Color(fullScrimRestColor.r, fullScrimRestColor.g,
                        fullScrimRestColor.b, 0.001f)
                    : fullScrimRestColor;
            }
            for (int i = 0; i < focusScrims.Length; i++)
                if (focusScrims[i] != null) focusScrims[i].gameObject.SetActive(focused);
        }

        private void UpdateSpotlightWindow(Vector2 center, Vector2 size)
        {
            if (canvasRect == null) return;
            Rect bounds = canvasRect.rect;
            float left = Mathf.Clamp(center.x - size.x * 0.5f, bounds.xMin, bounds.xMax);
            float right = Mathf.Clamp(center.x + size.x * 0.5f, bounds.xMin, bounds.xMax);
            float bottom = Mathf.Clamp(center.y - size.y * 0.5f, bounds.yMin, bounds.yMax);
            float top = Mathf.Clamp(center.y + size.y * 0.5f, bounds.yMin, bounds.yMax);

            SetLocalRect(focusScrims[0].rectTransform,
                bounds.xMin, bounds.yMin, bounds.xMax, bottom);
            SetLocalRect(focusScrims[1].rectTransform,
                bounds.xMin, top, bounds.xMax, bounds.yMax);
            SetLocalRect(focusScrims[2].rectTransform,
                bounds.xMin, bottom, left, top);
            SetLocalRect(focusScrims[3].rectTransform,
                right, bottom, bounds.xMax, top);
        }

        private static void SetLocalRect(RectTransform rect,
                                         float left, float bottom,
                                         float right, float top)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2((left + right) * 0.5f,
                (bottom + top) * 0.5f);
            rect.sizeDelta = new Vector2(Mathf.Max(0f, right - left),
                Mathf.Max(0f, top - bottom));
        }

        private bool TryGetRectTransformScreenRect(RectTransform target,
                                                    out Rect screenRect)
        {
            screenRect = default;
            if (target == null || !target.gameObject.activeInHierarchy) return false;

            Canvas targetCanvas = target.GetComponentInParent<Canvas>();
            if (targetCanvas == null) return false;
            Canvas rootCanvas = targetCanvas.rootCanvas != null
                ? targetCanvas.rootCanvas
                : targetCanvas;
            Camera eventCamera = rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : rootCanvas.worldCamera != null
                    ? rootCanvas.worldCamera
                    : Camera.main;

            target.GetWorldCorners(uiTargetWorldCorners);
            Vector2 screenMin = new Vector2(float.PositiveInfinity,
                float.PositiveInfinity);
            Vector2 screenMax = new Vector2(float.NegativeInfinity,
                float.NegativeInfinity);
            for (int i = 0; i < uiTargetWorldCorners.Length; i++)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(
                    eventCamera, uiTargetWorldCorners[i]);
                screenMin = Vector2.Min(screenMin, point);
                screenMax = Vector2.Max(screenMax, point);
            }

            if (float.IsInfinity(screenMin.x) || float.IsInfinity(screenMax.x)
                || float.IsNaN(screenMin.x) || float.IsNaN(screenMax.x))
                return false;
            screenRect = Rect.MinMaxRect(screenMin.x, screenMin.y,
                screenMax.x, screenMax.y);
            return screenRect.width > 0f && screenRect.height > 0f;
        }

        private static bool TryGetWorldTargetScreenRect(
            Transform target, Camera camera, out Rect screenRect)
        {
            screenRect = default;
            if (target == null || camera == null) return false;
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(false);
            if (renderers == null || renderers.Length == 0) return false;

            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds) return false;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            Vector2 screenMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 screenMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                Vector3 world = new Vector3(x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
                Vector3 point = camera.WorldToScreenPoint(world);
                if (point.z <= 0f) continue;
                screenMin = Vector2.Min(screenMin, point);
                screenMax = Vector2.Max(screenMax, point);
            }
            if (float.IsInfinity(screenMin.x) || float.IsInfinity(screenMax.x)) return false;
            screenRect = Rect.MinMaxRect(screenMin.x, screenMin.y,
                screenMax.x, screenMax.y);
            return screenRect.width > 0f && screenRect.height > 0f;
        }
    }
}
