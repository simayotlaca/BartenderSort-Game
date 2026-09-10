using System;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows the supplied level and difficulty skin on the home button. Does not read or change progress or
    /// balances.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderMainMenuLevelButtonView : MonoBehaviour
    {
        private const int SparkCount = 48;
        private const int RimGlowLayers = 4;

        // Anticipate, visibly swell, then change the skin inside a short white flash. The perimeter
        // sparkles clear before the existing coin transfer; all motion uses unscaled time.
        private const float ChangeAnticipate = 0.12f;
        private const float ChangeAnticipateHold = 0.025f;
        private const float ChangeCharge = 0.27f;
        private const float ChangePeakHold = 0.075f;
        private const float ChangeSettle = 0.42f;
        private const float ChangeWashOut = 0.10f;
        private const float ChangeQuietHold = 0.10f;
        private const float SparkStagger = 0.005f;
        private const float SparkLifetime = 0.64f;

        private const float ChangeSquashScale = 0.965f;
        private const float ChangeChargeScale = 1.18f;

        private static readonly Color DifficultyTextColor =
            new Color(1f, 0.976f, 0.91f, 1f);
        private static readonly Color RimGlowGold =
            new Color(1f, 0.80f, 0.26f, 1f);
        private static readonly Color RimGlowHot =
            new Color(1f, 0.96f, 0.80f, 1f);
        private static readonly Color HaloWarmWhite =
            new Color(1f, 1f, 0.97f, 1f);
        private static readonly Color SparkGold =
            new Color(1f, 0.91f, 0.54f, 1f);

        // Four closely spaced frame copies make one glow; wider gaps look like separate rings.
        private static readonly float[] RimGlowScales = { 1.018f, 1.038f, 1.065f, 1.10f };
        private static readonly float[] RimGlowAlphas = { 0.92f, 0.50f, 0.22f, 0.075f };
        private const float RimGlowStartFraction = 0.25f;

        private Button levelButton;
        private Image frameImage;
        private Text levelLabel;
        private RectTransform buttonRect;
        private RectTransform levelLabelRect;

        private Vector2 normalButtonSize;
        private Vector2 normalButtonPosition;
        private Vector2 normalLevelLabelPosition;
        private Vector3 buttonRestScale = Vector3.one;
        private Vector3 levelLabelRestScale = Vector3.one;
        private Color levelLabelRestColor = Color.white;
        private bool levelLabelIsButtonChild;
        private bool geometryCaptured;

        [Header("Authored difficulty frames")]
        [SerializeField] private Sprite normalPlayFrame;
        [SerializeField] private Sprite hardPlayFrame;
        [SerializeField] private Sprite veryHardPlayFrame;

        [Header("Authored hierarchy")]
        [SerializeField] private Text difficultyLabel;
        [SerializeField] private RectTransform fxRoot;
        [SerializeField] private Image burstHalo;
        [SerializeField] private Image[] rimGlowImages = new Image[RimGlowLayers];
        [SerializeField] private Image pillRedraw;
        [SerializeField] private Image whiteWash;
        [SerializeField] private Image[] sparkImages = new Image[SparkCount];

        private RectTransform burstHaloRect;
        private RectTransform[] rimGlowRects;
        private RectTransform[] sparkRects;
        private bool authoredFxErrorLogged;

        private Sequence transitionSequence;
        private Action transitionInterruptedCallback;
        private long transitionRevision;

        public BartenderResultPopupStyle CurrentStyle { get; private set; } =
            BartenderResultPopupStyle.Normal;
        public float CurrentTopExtension => geometryCaptured && buttonRect != null
            ? Mathf.Max(0f, buttonRect.sizeDelta.y - normalButtonSize.y)
            : 0f;

        public void Bind(Button targetButton, Text targetLevelLabel)
        {
            Image targetFrame = targetButton != null
                ? targetButton.targetGraphic as Image
                : null;
            if (targetFrame == null && targetButton != null)
                targetFrame = targetButton.GetComponent<Image>();

            bool hierarchyChanged = levelButton != targetButton
                                    || levelLabel != targetLevelLabel
                                    || frameImage != targetFrame;
            if (hierarchyChanged)
            {
                InterruptLevelTransition();
                geometryCaptured = false;
            }

            levelButton = targetButton;
            frameImage = targetFrame;
            levelLabel = targetLevelLabel;
            buttonRect = levelButton != null
                ? levelButton.transform as RectTransform
                : null;
            levelLabelRect = levelLabel != null
                ? levelLabel.rectTransform
                : null;

            if (!CanRender()) return;
            if (!geometryCaptured) CaptureAuthoredGeometry();

            // Reward presentation locks pointer input but should not wash the artwork out.
            ColorBlock colors = levelButton.colors;
            colors.disabledColor = Color.white;
            levelButton.colors = colors;
            frameImage.type = Image.Type.Simple;
            frameImage.preserveAspect = true;
            frameImage.color = Color.white;

            EnsureAuthoredVisuals();
        }

        public void ApplyLevel(int levelNumber)
        {
            int safeLevel = Mathf.Max(1, levelNumber);
            BartenderResultPopupStyle style =
                BartenderResultPopupStyleResolver.Resolve(safeLevel);
            ApplySkin(style);
            if (levelLabel != null) levelLabel.text = $"Level {safeLevel}";
        }

        public void ApplyCompleted()
        {
            ApplySkin(BartenderResultPopupStyle.Normal);
            if (levelLabel != null) levelLabel.text = "COMPLETED";
        }

        /// <summary>
        /// Updates the normal layout size after responsive fitting, then reapplies the difficulty skin.
        /// </summary>
        public void SetNormalLayout(Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            if (!CanRender() || !geometryCaptured) return;
            // Sibling labels must follow responsive movement just like button children do.
            if (!levelLabelIsButtonChild)
                normalLevelLabelPosition += anchoredPosition - normalButtonPosition;
            normalButtonPosition = anchoredPosition;
            normalButtonSize = new Vector2(
                Mathf.Max(1f, sizeDelta.x),
                Mathf.Max(1f, sizeDelta.y));
            ApplySkin(CurrentStyle);
        }

        /// <summary>
        /// A disabled or hidden view cannot provide a visible level-advance completion.
        /// </summary>
        internal bool CanPlayLevelAdvance() => isActiveAndEnabled
            && CanRender() && geometryCaptured && levelButton.isActiveAndEnabled
            && frameImage.isActiveAndEnabled && levelLabel.isActiveAndEnabled
            && EnsureAuthoredVisuals();

        /// <summary>
        /// Plays the level celebration and swaps any difficulty frame at the glint peak. Call back only after
        /// it settles so coin transfer starts separately.
        /// </summary>
        public bool PlayLevelAdvance(
            int completedLevelNumber,
            int nextLevelNumber,
            Action onSkinSwapped,
            Action onTransitionCompleted,
            Action onTransitionInterrupted)
        {
            if (!CanPlayLevelAdvance()) return false;

            if (!TryCancelLevelTransition()) return false;
            ApplyLevel(completedLevelNumber);

            int safeNextLevel = Mathf.Max(1, nextLevelNumber);
            BartenderResultPopupStyle nextStyle =
                BartenderResultPopupStyleResolver.Resolve(safeNextLevel);
            Sprite nextFrame = ResolvePlayFrame(nextStyle);
            Vector2 nextButtonSize = ResolveButtonSize(nextStyle, nextFrame);

            long revision = ++transitionRevision;
            bool skinSwapped = false;
            ResetTransientVisuals();

            Sequence sequence = DOTween.Sequence()
                .SetTarget(this)
                .SetUpdate(true)
                .SetRecyclable(true);
            transitionSequence = sequence;
            transitionInterruptedCallback = onTransitionInterrupted;

            float swapTime = BuildLevelAdvanceBeat(
                sequence, nextButtonSize);

            sequence.InsertCallback(swapTime, () =>
            {
                if (!IsCurrent(revision)
                    || !ReferenceEquals(transitionSequence, sequence)) return;
                if (!CanPlayLevelAdvance())
                {
                    InterruptLevelTransition();
                    return;
                }

                ApplyLevel(safeNextLevel);
                skinSwapped = true;
                onSkinSwapped?.Invoke();
                if (!IsCurrent(revision)
                    || !ReferenceEquals(transitionSequence, sequence)) return;

                // Keep the white lettering opaque. Fading outlined text makes the overlapping shadow
                // copies dominate the face and turns the caption muddy gray during the reveal.
                levelLabel.color = levelLabelRestColor;
                if (difficultyLabel != null && difficultyLabel.gameObject.activeSelf)
                    difficultyLabel.color = DifficultyTextColor;
                frameImage.color = Color.white;

                // Swap every frame copy together so the glow cannot show the old skin.
                SyncOwnedFrameSprites();
            });

            sequence.OnComplete(() =>
            {
                if (!ReferenceEquals(transitionSequence, sequence)
                    || !IsCurrent(revision)) return;
                if (!skinSwapped || !CanPlayLevelAdvance())
                {
                    InterruptLevelTransition();
                    return;
                }
                Action interrupted = transitionInterruptedCallback;
                transitionSequence = null;
                transitionInterruptedCallback = null;
                bool reset = TryResetTransientVisuals();
                if (!IsCurrent(revision)) return;
                if (reset) onTransitionCompleted?.Invoke();
                else interrupted?.Invoke();
            });
            sequence.OnKill(() =>
            {
                if (!ReferenceEquals(transitionSequence, sequence)
                    || !IsCurrent(revision)) return;
                transitionSequence = null;
                Action interrupted = transitionInterruptedCallback;
                transitionInterruptedCallback = null;
                TryResetTransientVisuals();
                if (IsCurrent(revision)) interrupted?.Invoke();
            });
            return true;
        }

        /// <summary>Builds the celebration and returns the glint peak time for swapping the level and skin.</summary>
        private float BuildLevelAdvanceBeat(
            Sequence sequence,
            Vector2 nextButtonSize)
        {
            float chargeStart = ChangeAnticipate + ChangeAnticipateHold;
            float chargeEnd = chargeStart + ChangeCharge;
            float swapTime = chargeEnd + ChangePeakHold;
            float flashStart = swapTime - 0.04f;
            float settleEnd = swapTime + ChangeSettle;
            float sparksEnd = swapTime + 7 * SparkStagger + SparkLifetime;

            PrepareChangeVisuals(nextButtonSize);

            InsertButtonScale(sequence, 0f, ChangeSquashScale, ChangeAnticipate, Ease.InOutSine);
            InsertButtonScale(sequence, chargeStart, ChangeChargeScale, ChangeCharge, Ease.OutCubic);
            sequence.InsertCallback(chargeStart,
                () => BsAudio.UI(BsSfx.DeliverSlide, 0.98f));

            for (int i = 0; i < RimGlowLayers; i++)
            {
                Image glow = rimGlowImages[i];
                float stagger = i * 0.012f;
                // One color tween owns RGB and alpha together, avoiding competing fade/color updates.
                sequence.Insert(chargeStart + stagger,
                    glow.DOColor(WithAlpha(RimGlowGold, RimGlowAlphas[i]),
                            ChangeCharge - stagger).SetEase(Ease.OutSine));
                sequence.Insert(chargeStart,
                    rimGlowRects[i].DOScale(ResolveRimScale(i, nextButtonSize), ChangeCharge)
                        .SetEase(Ease.OutCubic));
                sequence.Insert(chargeEnd,
                    glow.DOColor(WithAlpha(RimGlowHot, RimGlowAlphas[i]), ChangePeakHold)
                        .SetEase(Ease.InSine));
                sequence.Insert(swapTime,
                    glow.DOColor(WithAlpha(RimGlowGold, 0f), ChangeSettle * 0.9f)
                        .SetEase(Ease.OutCubic));
            }

            // The silhouette material ignores painted RGB, so the flash is equally bright on all skins.
            sequence.Insert(chargeStart,
                whiteWash.DOFade(0.025f, flashStart - chargeStart).SetEase(Ease.InSine));
            sequence.Insert(flashStart,
                whiteWash.DOFade(0.96f, swapTime - flashStart).SetEase(Ease.InCubic));
            sequence.Insert(swapTime,
                whiteWash.DOFade(0f, ChangeWashOut).SetEase(Ease.OutExpo));
            sequence.InsertCallback(swapTime,
                () => BsAudio.UI(BsSfx.Check, 1.06f));

            sequence.Insert(chargeStart,
                burstHalo.DOFade(0.38f, swapTime - chargeStart).SetEase(Ease.InSine));
            sequence.Insert(chargeStart,
                burstHaloRect.DOScale(1.04f, swapTime - chargeStart).SetEase(Ease.OutSine));
            sequence.Insert(swapTime,
                burstHaloRect.DOScale(1.25f, ChangeSettle).SetEase(Ease.OutCubic));
            sequence.Insert(swapTime,
                burstHalo.DOFade(0f, ChangeSettle).SetEase(Ease.OutSine));

            InsertSparkTweens(sequence, swapTime, nextButtonSize);

            // Release the held swell, undershoot slightly, then land once without an elastic wobble.
            InsertButtonScale(sequence, swapTime, 0.985f, 0.20f, Ease.OutCubic);
            InsertButtonScale(sequence, swapTime + 0.20f, 1f, ChangeSettle - 0.20f, Ease.InOutSine);

            sequence.InsertCallback(Mathf.Max(settleEnd, sparksEnd) + ChangeQuietHold, () => { });
            return swapTime;
        }

        private void InsertButtonScale(Sequence sequence, float at, float scale, float duration, Ease ease)
        {
            sequence.Insert(at, buttonRect.DOScale(Scaled(buttonRestScale, scale), duration).SetEase(ease));
            // The authored menu caption is a sibling: it must swell with the art, too.
            if (!levelLabelIsButtonChild)
                sequence.Insert(at,
                    levelLabelRect.DOScale(Scaled(levelLabelRestScale, scale), duration).SetEase(ease));
        }

        private static Vector3 ResolveRimScale(int index, Vector2 size)
        {
            float spread = RimGlowScales[index] - 1f;
            return new Vector3(1f + spread, 1f + spread * size.x / Mathf.Max(1f, size.y), 1f);
        }

        /// <summary>Stops owned motion silently while the reward presenter handles its own reset.</summary>
        public void CancelLevelTransition()
        {
            TryCancelLevelTransition();
        }

        internal bool TryCancelLevelTransition() => StopLevelTransition(false);

        private void InterruptLevelTransition() => StopLevelTransition(true);

        private bool StopLevelTransition(bool notifyInterruption)
        {
            Sequence sequence = transitionSequence;
            Action interrupted = transitionInterruptedCallback;
            bool hadTransition = sequence != null;

            long revision = ++transitionRevision;
            transitionSequence = null;
            transitionInterruptedCallback = null;
            bool stopped = true;
            try
            {
                if (sequence != null && sequence.IsActive()) sequence.Kill(false);
            }
            catch (Exception exception)
            {
                stopped = false;
                Debug.LogException(exception, this);
            }
            stopped = TryResetTransientVisuals() && stopped;
            if (notifyInterruption && hadTransition && IsCurrent(revision)) interrupted?.Invoke();
            return stopped;
        }

        private bool TryResetTransientVisuals()
        {
            try
            {
                ResetTransientVisuals();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return false;
            }
        }

        private void CaptureAuthoredGeometry()
        {
            normalButtonSize = buttonRect.sizeDelta;
            if (normalButtonSize.x <= 1f)
                normalButtonSize.x = Mathf.Max(1f, buttonRect.rect.width);
            if (normalButtonSize.y <= 1f)
                normalButtonSize.y = Mathf.Max(1f, buttonRect.rect.height);

            normalLevelLabelPosition = levelLabelRect.anchoredPosition;
            normalButtonPosition = buttonRect.anchoredPosition;
            buttonRestScale = buttonRect.localScale;
            levelLabelRestScale = levelLabelRect.localScale;
            levelLabelRestColor = levelLabel.color;
            levelLabelIsButtonChild = levelLabel.transform.IsChildOf(levelButton.transform);
            geometryCaptured = true;
        }

        private void ApplySkin(BartenderResultPopupStyle style)
        {
            if (!CanRender()) return;
            EnsureAuthoredVisuals();

            Sprite sprite = ResolvePlayFrame(style);
            if (sprite != null) frameImage.sprite = sprite;
            frameImage.preserveAspect = true;
            frameImage.color = Color.white;

            Vector2 targetSize = ResolveButtonSize(style, sprite);
            buttonRect.sizeDelta = targetSize;
            buttonRect.anchoredPosition = style == BartenderResultPopupStyle.Normal
                ? normalButtonPosition
                : normalButtonPosition
                  + Vector2.up * ((targetSize.y - normalButtonSize.y) * 0.5f);
            levelLabelRect.anchoredPosition = levelLabelIsButtonChild
                ? normalLevelLabelPosition
                  - Vector2.up * ((targetSize.y - normalButtonSize.y) * 0.5f)
                : normalLevelLabelPosition;

            CurrentStyle = style;
            ConfigureDifficultyLabel(style);
            SyncOwnedFrameSprites();
        }

        private Vector2 ResolveButtonSize(
            BartenderResultPopupStyle style,
            Sprite sprite)
        {
            if (style == BartenderResultPopupStyle.Normal || sprite == null)
                return normalButtonSize;

            float spriteWidth = Mathf.Max(1f, sprite.rect.width);
            float targetHeight = ResolveNormalVisualWidth()
                                 * sprite.rect.height / spriteWidth;
            return new Vector2(normalButtonSize.x, targetHeight);
        }

        private float ResolveNormalVisualWidth()
        {
            float authoredWidth = Mathf.Max(1f, normalButtonSize.x);
            Sprite normalFrame = ResolvePlayFrame(
                BartenderResultPopupStyle.Normal);
            if (normalFrame == null || normalFrame.rect.height <= 0f)
                return authoredWidth;

            float heightLimitedWidth = Mathf.Max(1f, normalButtonSize.y)
                                       * normalFrame.rect.width
                                       / normalFrame.rect.height;
            return Mathf.Min(authoredWidth, heightLimitedWidth);
        }

        private Sprite ResolvePlayFrame(BartenderResultPopupStyle style)
        {
            Sprite frame = style == BartenderResultPopupStyle.Hard
                ? hardPlayFrame
                : style == BartenderResultPopupStyle.VeryHard
                    ? veryHardPlayFrame
                    : normalPlayFrame;
            return frame != null ? frame : normalPlayFrame;
        }

        private void ConfigureDifficultyLabel(BartenderResultPopupStyle style)
        {
            if (difficultyLabel == null) return;

            bool visible = style != BartenderResultPopupStyle.Normal;
            difficultyLabel.gameObject.SetActive(visible);
            if (!visible) return;

            difficultyLabel.text = style == BartenderResultPopupStyle.Hard
                ? "HARD"
                : "VERY HARD";
        }

        private bool EnsureAuthoredVisuals()
        {
            if (!CanRender()) return false;

            bool valid = fxRoot != null
                         && burstHalo != null
                         && pillRedraw != null
                         && whiteWash != null
                         && AllAssigned(rimGlowImages, RimGlowLayers)
                         && AllAssigned(sparkImages, SparkCount);
            if (!valid)
            {
                if (!authoredFxErrorLogged)
                {
                    Debug.LogError(
                        "Level Transition FX authored hierarchy referansları eksik. "
                      + "FX Root, Burst Halo, dört Rim Glow, Pill Redraw, White Wash "
                      + "ve kırk sekiz Spark Inspector'dan bağlanmalı.", this);
                    authoredFxErrorLogged = true;
                }
                return false;
            }

            burstHaloRect = burstHalo.rectTransform;
            if (rimGlowRects == null || rimGlowRects.Length != RimGlowLayers)
                rimGlowRects = new RectTransform[RimGlowLayers];
            for (int i = 0; i < RimGlowLayers; i++)
                rimGlowRects[i] = rimGlowImages[i].rectTransform;

            if (sparkRects == null || sparkRects.Length != SparkCount)
                sparkRects = new RectTransform[SparkCount];
            for (int i = 0; i < SparkCount; i++)
                sparkRects[i] = sparkImages[i].rectTransform;

            authoredFxErrorLogged = false;
            return true;
        }

        private static bool AllAssigned(Image[] images, int expectedCount)
        {
            if (images == null || images.Length != expectedCount) return false;
            for (int i = 0; i < images.Length; i++)
                if (images[i] == null) return false;
            return true;
        }

        /// <summary>Updates every frame copy to match the button's current art.</summary>
        private void SyncOwnedFrameSprites()
        {
            if (frameImage == null) return;
            Sprite sprite = frameImage.sprite;
            if (sprite == null) return;

            if (pillRedraw != null) pillRedraw.sprite = sprite;
            if (whiteWash != null) whiteWash.sprite = sprite;
            if (rimGlowImages == null) return;
            for (int i = 0; i < rimGlowImages.Length; i++)
            {
                if (rimGlowImages[i] != null) rimGlowImages[i].sprite = sprite;
            }
        }

        private void PrepareChangeVisuals(Vector2 nextButtonSize)
        {
            if (fxRoot == null) return;
            fxRoot.gameObject.SetActive(true);
            SyncOwnedFrameSprites();

            if (pillRedraw != null)
            {
                pillRedraw.enabled = true;
                pillRedraw.color = Color.white;
            }
            if (whiteWash != null) whiteWash.color = WithAlpha(HaloWarmWhite, 0f);
            for (int i = 0; i < RimGlowLayers; i++)
            {
                if (rimGlowImages[i] == null) continue;
                rimGlowImages[i].color = WithAlpha(RimGlowGold, 0f);
                rimGlowRects[i].localScale = Vector3.LerpUnclamped(
                    Vector3.one, ResolveRimScale(i, nextButtonSize), RimGlowStartFraction);
            }

            float width = Mathf.Max(1f, nextButtonSize.x);
            float height = Mathf.Max(1f, nextButtonSize.y);
            if (burstHaloRect != null && burstHalo != null)
            {
                burstHalo.preserveAspect = false;
                burstHaloRect.sizeDelta = new Vector2(width * 1.55f, height * 2.15f);
                burstHaloRect.anchoredPosition = Vector2.zero;
                burstHaloRect.localScale = Vector3.one * 0.75f;
                burstHalo.color = WithAlpha(HaloWarmWhite, 0f);
            }

            for (int i = 0; i < SparkCount; i++)
            {
                Vector2 direction;
                Vector2 start = ResolveSparkOrigin(i, nextButtonSize, out direction);
                sparkRects[i].anchoredPosition = start;
                // A fine bright necklace with occasional larger stars, not equally sized radial spokes.
                float sparkSize = width * (i % 3 == 0 ? 0.050f : (i % 3 == 1 ? 0.032f : 0.023f));
                sparkRects[i].sizeDelta = Vector2.one * Mathf.Max(7f, sparkSize);
                sparkRects[i].localRotation = Quaternion.Euler(0f, 0f, (i * 37) % 90);
                sparkRects[i].localScale = Vector3.zero;
                sparkImages[i].color = WithAlpha(i % 4 == 0 ? SparkGold : Color.white, 0f);
                sparkImages[i].enabled = true;
            }
        }

        // Equally spaced samples of a rounded button perimeter. Starting at the edge keeps the
        // number clear and makes the sparkle burst trace the button, including its long straight sides.
        private static Vector2 ResolveSparkOrigin(int index, Vector2 size, out Vector2 direction)
        {
            float radius = Mathf.Max(1f, size.y * 0.43f);
            float halfStraight = Mathf.Max(0f, size.x * 0.46f - radius);
            float straight = 2f * halfStraight;
            float arc = Mathf.PI * radius;
            float distance = (index + 0.35f) / SparkCount * (2f * straight + 2f * arc);
            Vector2 position;
            if (distance < straight)
            {
                direction = Vector2.up;
                position = new Vector2(-halfStraight + distance, radius);
            }
            else if ((distance -= straight) < arc)
            {
                float angle = Mathf.PI * 0.5f - distance / radius;
                direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                position = new Vector2(halfStraight, 0f) + direction * radius;
            }
            else if ((distance -= arc) < straight)
            {
                direction = Vector2.down;
                position = new Vector2(halfStraight - distance, -radius);
            }
            else
            {
                distance -= straight;
                float angle = -Mathf.PI * 0.5f - distance / radius;
                direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                position = new Vector2(-halfStraight, 0f) + direction * radius;
            }
            return position + direction * (index % 3 - 1) * size.x * 0.006f;
        }

        private void InsertSparkTweens(Sequence sequence, float revealStart, Vector2 nextButtonSize)
        {
            float width = Mathf.Max(1f, nextButtonSize.x);
            for (int i = 0; i < SparkCount; i++)
            {
                Vector2 direction;
                Vector2 start = ResolveSparkOrigin(i, nextButtonSize, out direction);
                float travel = width * (0.075f + (i % 5) * 0.012f);
                Vector2 tangent = new Vector2(-direction.y, direction.x);
                Vector2 end = start + direction * travel
                    + tangent * ((i % 3 - 1) * width * 0.014f)
                    + Vector2.up * width * 0.035f;
                float launch = revealStart + (i * 5 % 8) * SparkStagger;
                float lifetime = SparkLifetime - (i % 3) * 0.055f;
                sequence.Insert(launch,
                    sparkRects[i].DOScale(Vector3.one, 0.055f).SetEase(Ease.OutCubic));
                sequence.Insert(launch,
                    sparkImages[i].DOFade(i % 3 == 0 ? 1f : 0.88f, 0.045f).SetEase(Ease.OutCubic));
                sequence.Insert(launch,
                    sparkRects[i].DOAnchorPos(end, lifetime).SetEase(Ease.OutCubic));
                sequence.Insert(launch,
                    sparkRects[i].DOLocalRotate(new Vector3(0f, 0f, (i * 37) % 90 + (i % 2 == 0 ? 65f : -50f)),
                        lifetime).SetEase(Ease.OutSine));
                sequence.Insert(launch + 0.11f,
                    sparkImages[i].DOFade(0f, lifetime - 0.11f).SetEase(Ease.InSine));
                sequence.Insert(launch + 0.075f,
                    sparkRects[i].DOScale(Vector3.one * 0.12f, lifetime - 0.075f).SetEase(Ease.InCubic));
            }
        }

        private void ResetTransientVisuals()
        {
            if (buttonRect != null) buttonRect.localScale = buttonRestScale;
            if (levelLabelRect != null) levelLabelRect.localScale = levelLabelRestScale;
            if (levelLabel != null) levelLabel.color = levelLabelRestColor;
            if (difficultyLabel != null && difficultyLabel.gameObject.activeSelf)
                difficultyLabel.color = DifficultyTextColor;

            if (whiteWash != null) whiteWash.color = WithAlpha(HaloWarmWhite, 0f);
            if (burstHalo != null) burstHalo.color = WithAlpha(HaloWarmWhite, 0f);
            if (pillRedraw != null) pillRedraw.enabled = false;
            if (rimGlowImages != null)
            {
                for (int i = 0; i < rimGlowImages.Length; i++)
                {
                    if (rimGlowImages[i] == null) continue;
                    rimGlowImages[i].color = WithAlpha(RimGlowGold, 0f);
                    if (rimGlowRects != null && i < rimGlowRects.Length
                        && rimGlowRects[i] != null && i < RimGlowScales.Length)
                    {
                        rimGlowRects[i].localScale = Vector3.one * RimGlowScales[i];
                    }
                }
            }
            if (sparkImages != null)
            {
                for (int i = 0; i < sparkImages.Length; i++)
                {
                    if (sparkImages[i] != null) sparkImages[i].enabled = false;
                }
            }
            if (fxRoot != null && fxRoot.gameObject.activeSelf)
                fxRoot.gameObject.SetActive(false);
        }

        private bool CanRender() => levelButton != null
                                    && frameImage != null
                                    && levelLabel != null
                                    && buttonRect != null
                                    && levelLabelRect != null;

        private bool IsCurrent(long revision) => revision == transitionRevision;

        private static Vector3 Scaled(Vector3 value, float multiplier) =>
            new Vector3(
                value.x * multiplier,
                value.y * multiplier,
                value.z);

        private static Color WithAlpha(Color color, float alpha) =>
            new Color(color.r, color.g, color.b, alpha);

        private void OnDisable() => InterruptLevelTransition();

        private void OnDestroy() => InterruptLevelTransition();
    }
}
