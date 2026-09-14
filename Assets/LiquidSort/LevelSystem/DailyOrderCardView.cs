using System.Globalization;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class DailyOrderCardView : MonoBehaviour
    {
        private static readonly Color CompletionTextColor = new Color32(255, 245, 213, 255);

        [Header("Authored hierarchy")]
        [SerializeField] private Image background = null;
        [SerializeField] private Image icon = null;
        [SerializeField] private TextMeshProUGUI titleText = null;
        [SerializeField] private Image track = null;
        [SerializeField] private Image fill = null;
        [SerializeField] private TextMeshProUGUI progressText = null;
        [SerializeField] private TextMeshProUGUI rewardText = null;
        [SerializeField] private TextMeshProUGUI rewardCaption = null;
        [SerializeField] private Image rewardArtwork = null;

        [Header("Completion")]
        [SerializeField] private Image flash = null;
        [Tooltip("Inactive plate behind the \"Completed\" label; sized to the label when shown.")]
        [SerializeField] private Image completionPlate = null;
        [SerializeField] private Image completionCheck = null;
        [SerializeField] private Material completionTextMaterial = null;
        [Tooltip("Inactive gold sparkles placed on the card; they burst once when the task completes.")]
        [SerializeField] private Image[] completionSparkles = null;

        [Header("Progress reveal")]
        [SerializeField, Min(0.1f)] private float progressDuration = 0.75f;
        private const float CompletionSoundGap = 0.35f;
        private static float lastCompletionSoundAt = float.NegativeInfinity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCompletionSound() => lastCompletionSoundAt = float.NegativeInfinity;

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
        private int appliedLayout = -1;
        // Pop-in order of the authored sparkles, in prefab order; extra sparkles follow on 0.05 s steps.
        private static readonly float[] SparkleDelays = { 0f, 0.07f, 0.14f, 0.04f, 0.11f, 0.17f };
        public bool IsAnimating { get; private set; }
        public int DisplayedProgress => Mathf.FloorToInt(displayedValue + 0.0001f);
        public int TargetProgress => targetValue;
        public bool IsComplete => goal > 0 && targetValue >= goal;

        public bool IsReady => background != null
                               && icon != null
                               && titleText != null
                               && track != null
                               && fill != null
                               && progressText != null
                               && rewardText != null;

        public void SetProgress(string title, int current, int target, int reward)
        {
            IsAnimating = false;
            goal = Mathf.Max(0, target);
            targetValue = Mathf.Clamp(current, 0, goal);
            displayedValue = targetValue;
            SetLabels(title, reward);
            DrawProgress();
        }

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
            if (!IsAnimating && IsComplete) Celebrate();
        }

        private void OnDisable()
        {
            IsAnimating = false;
            if (flash != null)
            {
                flash.DOKill();
                flash.gameObject.SetActive(false);
            }
            HideSparkles();
        }

        private void SetLabels(string title, int reward)
        {
            int safeReward = Mathf.Max(0, reward);
            bool showRewardArtwork = safeReward == 100 && rewardArtwork != null && rewardArtwork.sprite != null;
            if (rewardArtwork != null)
                rewardArtwork.gameObject.SetActive(showRewardArtwork);
            if (titleText != null)
                titleText.text = title ?? string.Empty;
            if (rewardText != null)
            {
                rewardText.gameObject.SetActive(!showRewardArtwork);
                string amount = "+" + safeReward.ToString(CultureInfo.InvariantCulture);
                rewardText.text = rewardCaption != null
                    ? amount : amount + "\n<size=64%>COINS</size>";
            }
            if (rewardCaption != null)
            {
                rewardCaption.gameObject.SetActive(!showRewardArtwork);
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

            // Fill type, method and origin are authored on the prefab (Filled, Horizontal, Left).
            fill.fillAmount = fraction;
            fill.gameObject.SetActive(!complete && fraction > 0f);
        }

        // Restyles the label only when it switches between counter and "Completed", not on every animated frame.
        private void UpdateProgressTextLayout(bool complete)
        {
            int layout = complete ? 1 : 0;
            if (layout == appliedLayout) return;
            appliedLayout = layout;
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
            progressText.alignment = complete ? TextAlignmentOptions.MidlineGeoAligned : progressTextAlignment;
            progressText.fontSize = complete ? 36f : progressTextFontSize;
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
            if (completionPlate == null || completionCheck == null) return;

            const float checkWidth = 38f;
            const float gap = 8f;
            float textWidth = progressText.GetPreferredValues("Completed").x;
            completionPlate.rectTransform.sizeDelta = new Vector2(
                Mathf.Max(260f, textWidth + checkWidth + gap + 40f), 60f);
            completionPlate.rectTransform.position = labelRect.TransformPoint(labelRect.rect.center);
            completionCheck.rectTransform.anchoredPosition = new Vector2(-(textWidth + gap) * .5f, 0f);
            labelRect.anchoredPosition += new Vector2((checkWidth + gap) * .5f, 0f);
            completionPlate.gameObject.SetActive(true);
        }

        private void Celebrate()
        {
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
            BurstSparkles();
            if (Time.unscaledTime - lastCompletionSoundAt >= CompletionSoundGap)
            {
                lastCompletionSoundAt = Time.unscaledTime;
                BsAudio.Instance?.Play(BsSfx.DailyTaskComplete, 0.8f);
            }
        }

        private void BurstSparkles()
        {
            if (completionSparkles == null) return;
            for (int i = 0; i < completionSparkles.Length; i++)
            {
                Image image = completionSparkles[i];
                if (image == null) continue;
                RectTransform rect = image.rectTransform;
                if (i == 0 && rect.parent != transform) rect.parent.SetAsLastSibling();
                float delay = i < SparkleDelays.Length ? SparkleDelays[i] : i * 0.05f;
                rect.DOKill();
                image.DOKill();
                float turn = (i & 1) == 0 ? 14f : -14f;
                rect.localScale = Vector3.zero;
                rect.localRotation = Quaternion.Euler(0f, 0f, -turn);
                image.color = Color.white;
                image.gameObject.SetActive(true);

                Sequence burst = DOTween.Sequence()
                    .SetTarget(rect)
                    .SetUpdate(true)
                    .SetLink(image.gameObject);
                burst.Insert(delay, rect.DOScale(1f, 0.24f).SetEase(Ease.OutBack));
                burst.Insert(delay, rect.DOLocalRotate(new Vector3(0f, 0f, turn), 0.70f)
                    .SetEase(Ease.OutSine));
                burst.Insert(delay + 0.40f, rect.DOScale(0f, 0.30f).SetEase(Ease.InQuad));
                burst.Insert(delay + 0.40f, DOTween.To(() => image.color.a, a =>
                    {
                        Color c = image.color;
                        c.a = a;
                        image.color = c;
                    }, 0f, 0.30f).SetTarget(image));
                burst.OnComplete(() => image.gameObject.SetActive(false));
            }
        }

        private void HideSparkles()
        {
            if (completionSparkles == null) return;
            foreach (Image image in completionSparkles)
            {
                if (image == null) continue;
                image.rectTransform.DOKill();
                image.DOKill();
                image.gameObject.SetActive(false);
            }
        }
    }
}
