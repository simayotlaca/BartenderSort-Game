using System.Globalization;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Exposes the existing failure card's visuals and actions. The presenter and session decide round, life
    /// and retry behaviour.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderFailurePopupView : MonoBehaviour
    {
        [Header("Overlay")]
        [SerializeField] private CanvasGroup overlayGroup = null;
        [SerializeField] private Image dimmer = null;
        [SerializeField] private Image flash = null;

        [Header("Card")]
        [SerializeField] private RectTransform cardPivot = null;
        [SerializeField] private Image cardFrame = null;
        [SerializeField] private Image levelBadge = null;
        [SerializeField] private TMP_Text levelNumberLabel = null;

        [Header("Difficulty Art")]
        [SerializeField] private Sprite normalCardSprite = null;
        [SerializeField] private Sprite hardCardSprite = null;
        [SerializeField] private Sprite veryHardCardSprite = null;
        [SerializeField] private Sprite normalBadgeSprite = null;
        [SerializeField] private Sprite hardBadgeSprite = null;
        [SerializeField] private Sprite veryHardBadgeSprite = null;

        [Header("Actions")]
        [SerializeField] private Button paidContinueButton = null;
        [SerializeField] private TMP_Text paidContinueCostLabel = null;
        [SerializeField] private Button retryButton = null;
        [SerializeField] private Button closeButton = null;
        [SerializeField] private BartenderCoinBalanceView coinBalance = null;

        public CanvasGroup OverlayGroup => overlayGroup;
        public Image Dimmer => dimmer;
        public Image Flash => flash;
        public RectTransform CardPivot => cardPivot;
        public Button PaidContinueButton => paidContinueButton;
        public Button RetryButton => retryButton;
        public Button CloseButton => closeButton;
        public BartenderCoinBalanceView CoinBalance => coinBalance;

        public bool IsReady => overlayGroup != null && dimmer != null && flash != null
                               && cardPivot != null && cardFrame != null
                               && levelBadge != null && levelNumberLabel != null
                               && normalCardSprite != null && hardCardSprite != null
                               && veryHardCardSprite != null && normalBadgeSprite != null
                               && hardBadgeSprite != null && veryHardBadgeSprite != null
                               && paidContinueButton != null
                               && paidContinueCostLabel != null && retryButton != null
                               && closeButton != null && coinBalance != null;

        public void SetLevel(BsLevel level)
        {
            BartenderResultPopupStyle style =
                BartenderResultPopupStyleResolver.Resolve(level);
            ApplyStyle(style);
            if (levelNumberLabel != null)
            {
                levelNumberLabel.text = level == null
                    ? string.Empty
                    : level.Index.ToString(CultureInfo.InvariantCulture);
            }
        }

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
            if (overlayGroup == null) return;
            overlayGroup.alpha = visible ? 1f : 0f;
            overlayGroup.interactable = visible;
            overlayGroup.blocksRaycasts = visible;
        }

        public void SetPurchaseCost(int coinCost)
        {
            if (paidContinueCostLabel == null) return;
            paidContinueCostLabel.text = Mathf.Max(0, coinCost)
                .ToString(CultureInfo.InvariantCulture);
        }

        public void SetButtonsInteractable(bool canRetry, bool canPaidContinue,
                                           bool canClose)
        {
            if (retryButton != null) retryButton.interactable = canRetry;
            if (paidContinueButton != null)
                paidContinueButton.interactable = canPaidContinue;
            if (closeButton != null) closeButton.interactable = canClose;
        }

        private void ApplyStyle(BartenderResultPopupStyle style)
        {
            if (cardFrame != null) cardFrame.sprite = CardSpriteFor(style);
            if (levelBadge != null) levelBadge.sprite = BadgeSpriteFor(style);
        }

        private Sprite CardSpriteFor(BartenderResultPopupStyle style)
        {
            switch (style)
            {
                case BartenderResultPopupStyle.Hard:
                    return hardCardSprite;
                case BartenderResultPopupStyle.VeryHard:
                    return veryHardCardSprite;
                default:
                    return normalCardSprite;
            }
        }

        private Sprite BadgeSpriteFor(BartenderResultPopupStyle style)
        {
            switch (style)
            {
                case BartenderResultPopupStyle.Hard:
                    return hardBadgeSprite;
                case BartenderResultPopupStyle.VeryHard:
                    return veryHardBadgeSprite;
                default:
                    return normalBadgeSprite;
            }
        }
    }
}
