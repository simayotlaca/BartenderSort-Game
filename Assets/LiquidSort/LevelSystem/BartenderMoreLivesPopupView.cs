using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Exposes the lives popup to its presenter. It makes no economy, session or round decisions.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderMoreLivesPopupView : MonoBehaviour
    {
        private static readonly CultureInfo InvariantCulture =
            CultureInfo.InvariantCulture;

        [Header("Overlay")]
        [SerializeField] private CanvasGroup overlayGroup = null;
        [SerializeField] private Image dimmer = null;

        [Header("Card")]
        [SerializeField] private RectTransform cardPivot = null;

        [Header("Actions")]
        [SerializeField] private Button closeButton = null;
        [SerializeField] private Button refillButton = null;

        [Header("Values")]
        [SerializeField] private Text lifeCountLabel = null;
        [SerializeField] private Text timerLabel = null;
        [SerializeField] private Text refillCostLabel = null;
        [SerializeField] private Text feedbackLabel = null;
        [SerializeField] private BartenderCoinBalanceView coinBalance = null;

        public RectTransform CardPivot => cardPivot;
        public Button CloseButton => closeButton;
        public Button RefillButton => refillButton;
        public BartenderCoinBalanceView CoinBalance => coinBalance;

        public bool IsReady => overlayGroup != null && dimmer != null
                               && cardPivot != null && closeButton != null
                               && refillButton != null && lifeCountLabel != null
                               && timerLabel != null && refillCostLabel != null
                               && feedbackLabel != null && coinBalance != null;

        public void SetVisible(bool visible)
        {
            if (visible) FitOverlayToParent();
            gameObject.SetActive(visible);
            if (overlayGroup == null) return;
            overlayGroup.alpha = visible ? 1f : 0f;
            overlayGroup.interactable = visible;
            overlayGroup.blocksRaycasts = visible;
        }

        private void FitOverlayToParent()
        {
            // Nested prefab overrides must not move the fullscreen overlay off its canvas.
            // Card size and animation remain on CardPivot.
            if (!(transform is RectTransform rect)) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition3D = Vector3.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }

        public void SetValues(int lifeCount, TimeSpan remaining, int refillCost)
        {
            if (lifeCountLabel != null)
                lifeCountLabel.text = Mathf.Max(0, lifeCount)
                    .ToString(InvariantCulture);

            if (timerLabel != null)
            {
                long totalSeconds = remaining <= TimeSpan.Zero
                    ? 0L
                    : (long)Math.Ceiling(remaining.TotalSeconds);
                long totalMinutes = totalSeconds / 60L;
                long seconds = totalSeconds % 60L;
                timerLabel.text = totalMinutes.ToString("00", InvariantCulture)
                                  + ":"
                                  + seconds.ToString("00", InvariantCulture);
            }

            if (refillCostLabel != null)
                refillCostLabel.text = Mathf.Max(0, refillCost)
                    .ToString(InvariantCulture);
        }

        public void SetFeedback(string message)
        {
            if (feedbackLabel != null) feedbackLabel.text = message ?? string.Empty;
        }

        public void SetButtonsInteractable(bool canClose, bool canRefill)
        {
            if (closeButton != null) closeButton.interactable = canClose;
            if (refillButton != null) refillButton.interactable = canRefill;
        }
    }
}
