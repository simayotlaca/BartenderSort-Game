using System.Globalization;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Projects caller-owned daily-order data onto one authored card hierarchy.
    /// Mission selection, rewards and persistence deliberately stay outside this view.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DailyOrderCardView : MonoBehaviour
    {
        private const string CompletionPlateSpritePath = "Ui/DailyOrders/DailyOrders_Card_Purple_Empty";
        private static readonly Color CompletionTextColor = new Color32(255, 245, 213, 255);
        private static readonly int TintId = Shader.PropertyToID("_Color");
        private static readonly Color FillColorGain = new Color(1.05f, 1.18f, 0.72f, 1f);
        private static readonly Color TrackColorGain = new Color(1.2f, 1.45f, 1.65f, 1f);

        [Header("Authored hierarchy")]
        [SerializeField] private Image background = null;
        [SerializeField] private Image icon = null;
        [SerializeField] private Image iconPlate = null;
        [SerializeField] private TextMeshProUGUI titleText = null;
        [SerializeField] private Image track = null;
        [SerializeField] private Image fill = null;
        [SerializeField] private TextMeshProUGUI progressText = null;
        [SerializeField] private TextMeshProUGUI rewardText = null;
        [SerializeField] private TextMeshProUGUI rewardCaption = null;

        [Header("Completion (optional - built at runtime when left empty)")]
        [SerializeField] private Image flash = null;
        [SerializeField] private Material completionTextMaterial = null;

        [Header("Progress reveal")]
        [SerializeField, Min(0.1f)] private float progressDuration = 0.75f;
        private float displayedValue;
        private float animationFrom;
        private float animationStart;
        private int targetValue;
        private int goal;
        private bool progressLayoutCached;
        private Vector2 progressTextPosition;
        private TextAlignmentOptions progressTextAlignment;
        private float progressTextFontSize;
        private Color progressTextColor;
        private Material progressTextMaterial;
        private Image completionPlate;
        private Material fillColorMaterial;
        private Material trackColorMaterial;
        public bool IsAnimating { get; private set; }
        public int DisplayedProgress => Mathf.FloorToInt(displayedValue + 0.0001f);
        /// <summary>True once the committed value reaches the goal (independent of the reveal tween).</summary>
        public bool IsComplete => goal > 0 && targetValue >= goal;

        /// <summary>Whether every authored element required by the card is connected.</summary>
        public bool IsReady => background != null
                               && icon != null
                               && titleText != null
                               && track != null
                               && fill != null
                               && progressText != null
                               && rewardText != null;

        private void Awake()
        {
            fillColorMaterial = ApplyProgressColor(fill, FillColorGain, "DailyOrders_VividFill");
            trackColorMaterial = ApplyProgressColor(track, TrackColorGain, "DailyOrders_VividTrack");
        }

        private void OnDestroy()
        {
            if (fillColorMaterial != null) Destroy(fillColorMaterial);
            if (trackColorMaterial != null) Destroy(trackColorMaterial);
        }

        private static Material ApplyProgressColor(Image image, Color gain, string materialName)
        {
            if (image == null) return null;
            // Material tint can brighten the authored bevel; Image.color clamps at 1.
            Material material = new Material(image.material) { name = materialName };
            material.SetColor(TintId, gain);
            image.material = material;
            return material;
        }

        /// <summary>Sets the card artwork and matching flash.</summary>
        public void SetBackground(Sprite backgroundSprite)
        {
            if (background == null) return;
            background.sprite = backgroundSprite;
            background.enabled = backgroundSprite != null;
            if (flash != null) flash.sprite = backgroundSprite;
        }

        /// <summary>Coloured plate drawn behind the icon; one authored variant per task.</summary>
        public void SetIconPlate(Sprite plateSprite)
        {
            if (iconPlate == null) return;
            iconPlate.sprite = plateSprite;
            iconPlate.preserveAspect = true;
            iconPlate.enabled = plateSprite != null;
        }

        /// <summary>
        /// Applies one complete presentation snapshot. Values are sanitized here so a
        /// transient caller error cannot invert the progress bar or leak stale icon art.
        /// </summary>
        public void Setup(
            string title,
            int current,
            int target,
            int reward,
            Sprite iconSprite)
        {
            SetProgress(title, current, target, reward);
            if (icon != null)
            {
                icon.sprite = iconSprite;
                icon.preserveAspect = true;
                icon.enabled = iconSprite != null;
            }
        }

        /// <summary>Updates live values while preserving the prefab's icon and layout.</summary>
        public void SetProgress(string title, int current, int target, int reward)
        {
            IsAnimating = false;
            goal = Mathf.Max(0, target);
            targetValue = Mathf.Clamp(current, 0, goal);
            displayedValue = targetValue;
            SetLabels(title, reward);
            DrawProgress();
        }

        /// <summary>
        /// Repeated timer refreshes keep the current reveal running. Only a changed
        /// destination starts a new tween, from the value the player actually sees.
        /// </summary>
        public void AnimateProgress(string title, int current, int target, int reward,
            float delay = 0f)
        {
            int nextGoal = Mathf.Max(0, target);
            int nextValue = Mathf.Clamp(current, 0, nextGoal);
            SetLabels(title, reward);
            if (goal == nextGoal && targetValue == nextValue) return;
            if (!isActiveAndEnabled || nextValue < displayedValue)
            {
                SetProgress(title, nextValue, nextGoal, reward);
                return;
            }
            goal = nextGoal;
            targetValue = nextValue;
            animationFrom = displayedValue;
            animationStart = Time.unscaledTime + Mathf.Max(0f, delay);
            IsAnimating = !Mathf.Approximately(displayedValue, targetValue);
            DrawProgress();
        }

        private void Update()
        {
            if (!IsAnimating || Time.unscaledTime < animationStart) return;
            float t = Mathf.Clamp01((Time.unscaledTime - animationStart)
                / Mathf.Max(0.1f, progressDuration));
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            displayedValue = Mathf.Lerp(animationFrom, targetValue, eased);
            if (t >= 1f)
            {
                displayedValue = targetValue;
                IsAnimating = false;
            }
            DrawProgress();
            // The reveal just landed on the goal: the one-time completion moment.
            if (!IsAnimating && IsComplete) Celebrate();
        }

        // Preserve the visible value when a popup is closed mid-reveal.
        private void OnDisable()
        {
            IsAnimating = false;
            if (flash != null)
            {
                flash.DOKill();
                flash.gameObject.SetActive(false);
            }
        }

        private void SetLabels(string title, int reward)
        {
            int safeReward = Mathf.Max(0, reward);
            if (titleText != null)
                titleText.text = title ?? string.Empty;
            if (rewardText != null)
            {
                string amount = "+" + safeReward.ToString(CultureInfo.InvariantCulture);
                // The authored caption has its own size and baseline, independent of the amount.
                rewardText.text = rewardCaption != null
                    ? amount : amount + "\n<size=64%>COINS</size>";
            }
            if (rewardCaption != null)
            {
                rewardCaption.text = "COINS";
            }
        }

        private void DrawProgress()
        {
            float fraction = goal > 0 ? Mathf.Clamp01(displayedValue / goal) : 0f;
            bool complete = IsComplete && !IsAnimating;
            if (progressText != null)
            {
                UpdateProgressTextLayout(complete);
                progressText.text = complete
                    ? "Completed"
                    : DisplayedProgress.ToString(CultureInfo.InvariantCulture)
                        + " / " + goal.ToString(CultureInfo.InvariantCulture);
            }

            if (track != null) track.gameObject.SetActive(!complete);

            if (fill == null) return;

            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillClockwise = true;
            fill.fillAmount = fraction;
            fill.gameObject.SetActive(!complete && fraction > 0f);
        }

        private void UpdateProgressTextLayout(bool complete)
        {
            RectTransform rect = progressText.rectTransform;
            if (!progressLayoutCached)
            {
                progressTextPosition = rect.anchoredPosition;
                progressTextAlignment = progressText.alignment;
                progressTextFontSize = progressText.fontSize;
                progressTextColor = progressText.color;
                progressTextMaterial = progressText.fontSharedMaterial;
                progressLayoutCached = true;
            }

            rect.anchoredPosition = progressTextPosition;
            progressText.alignment = complete ? TextAlignmentOptions.Midline : progressTextAlignment;
            progressText.fontSize = complete ? 32f : progressTextFontSize;
            progressText.color = complete ? CompletionTextColor : progressTextColor;
            Material labelMaterial = complete && completionTextMaterial != null
                ? completionTextMaterial : progressTextMaterial;
            if (progressText.fontSharedMaterial != labelMaterial)
                progressText.fontSharedMaterial = labelMaterial;
            if (!complete)
            {
                if (completionPlate != null) completionPlate.gameObject.SetActive(false);
                return;
            }

            // Seat the status just beneath the bar's centre, leaving a clear gap below the title.
            if (track != null)
            {
                Vector3 barCentre = rect.parent.InverseTransformPoint(
                    track.rectTransform.TransformPoint(track.rectTransform.rect.center));
                Vector3 textCentre = rect.localPosition + (Vector3)rect.rect.center;
                rect.anchoredPosition += (Vector2)(barCentre - textCentre) + new Vector2(0f, -8f);
            }
            ShowCompletionPlate(rect);
        }

        private void ShowCompletionPlate(RectTransform labelRect)
        {
            if (completionPlate == null)
            {
                GameObject plate = NewUi("CompletionStatus", labelRect.parent);
                completionPlate = plate.AddComponent<Image>();
                completionPlate.sprite = Resources.Load<Sprite>(CompletionPlateSpritePath);
                completionPlate.type = Image.Type.Simple;
                completionPlate.preserveAspect = true;
                completionPlate.raycastTarget = false;
                completionPlate.color = completionPlate.sprite != null
                    ? Color.white : (Color)new Color32(72, 28, 91, 255);
                RectTransform plateRect = completionPlate.rectTransform;
                plateRect.anchorMin = plateRect.anchorMax = labelRect.anchorMin;
                plateRect.pivot = new Vector2(0.5f, 0.5f);
                const float width = 244f;
                float height = completionPlate.sprite != null
                    ? width * completionPlate.sprite.rect.height / completionPlate.sprite.rect.width
                    : 64f;
                plateRect.sizeDelta = new Vector2(width, height);
                plate.transform.SetSiblingIndex(labelRect.GetSiblingIndex());
            }

            // A passive status label: retain the source artwork's proportions and draw behind the text.
            completionPlate.rectTransform.position = labelRect.TransformPoint(labelRect.rect.center);
            completionPlate.gameObject.SetActive(true);
        }

        /// <summary>One-shot: a white flash sweeps over the card when progress reaches its goal.</summary>
        private void Celebrate()
        {
            EnsureCompletionVisuals();
            if (flash != null)
            {
                flash.DOKill();
                flash.gameObject.SetActive(true);
                Color start = flash.color;
                start.a = 0.6f;
                flash.color = start;
                DOTween.To(() => flash.color.a, a =>
                    {
                        Color c = flash.color;
                        c.a = a;
                        flash.color = c;
                    }, 0f, 0.55f)
                    .SetEase(Ease.OutQuad)
                    .SetUpdate(true)
                    .SetTarget(flash)
                    .SetLink(flash.gameObject)
                    .OnComplete(() => flash.gameObject.SetActive(false));
            }
            BsAudio.Instance?.Play(BsSfx.StepComplete, 0.8f);
        }

        private void EnsureCompletionVisuals()
        {
            if (flash == null && background != null)
            {
                GameObject flashObject = NewUi("Flash", transform);
                flash = flashObject.AddComponent<Image>();
                flash.sprite = background.sprite;
                flash.type = background.type;
                flash.pixelsPerUnitMultiplier = background.pixelsPerUnitMultiplier;
                flash.color = new Color(1f, 1f, 1f, 0f);
                flash.raycastTarget = false;
                RectTransform rect = flash.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                flashObject.transform.SetAsLastSibling();
                flashObject.SetActive(false);
            }
        }

        private static GameObject NewUi(string name, Transform parent)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.layer = parent.gameObject.layer;
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }
    }
}
