using System.Globalization;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Exposes the success popup's roots and buttons to <see cref="BartenderRoundFeedbackPresenter"/>. It
    /// owns no round or economy logic.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderResultPopupView : MonoBehaviour
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

        // CheersToast_Manual owns the visible toast through its Animator.

        [Header("Difficulty Art")]
        [SerializeField] private Sprite normalCardSprite = null;
        [SerializeField] private Sprite hardCardSprite = null;
        [SerializeField] private Sprite veryHardCardSprite = null;
        [SerializeField] private Sprite normalBadgeSprite = null;
        [SerializeField] private Sprite hardBadgeSprite = null;
        [SerializeField] private Sprite veryHardBadgeSprite = null;

        [Header("Reward Art")]
        [SerializeField] private Image rewardShadow = null;
        [SerializeField] private RectTransform rewardRays = null;
        [SerializeField] private GameObject normalRewardGroup = null;
        [SerializeField] private GameObject premiumRewardGroup = null;
        [SerializeField] private TMP_Text rewardValueLabel = null;
        [SerializeField] private BartenderCoinBalanceView coinBalance = null;
        [SerializeField] private Sprite normalRewardShadowSprite = null;
        [SerializeField] private Sprite hardRewardShadowSprite = null;
        [SerializeField] private Sprite veryHardRewardShadowSprite = null;

        [Header("Actions")]
        [SerializeField] private Button rewardButton = null;
        [SerializeField] private Button continueButton = null;
        [SerializeField] private Button closeButton = null;

        private BartenderCheersSequence cheersSequence;

        public CanvasGroup OverlayGroup => overlayGroup;
        public Image Dimmer => dimmer;
        public Image Flash => flash;
        public RectTransform CardPivot => cardPivot;

        /// <summary>
        /// Reward origin for the later menu animation. Read the active artwork group because difficulty
        /// variants change its layout.
        /// </summary>
        public RectTransform RewardPresentationAnchor
        {
            get
            {
                if (premiumRewardGroup != null && premiumRewardGroup.activeInHierarchy)
                    return premiumRewardGroup.transform as RectTransform;
                if (normalRewardGroup != null && normalRewardGroup.activeInHierarchy)
                    return normalRewardGroup.transform as RectTransform;
                return rewardRays;
            }
        }

        public Button RewardButton => rewardButton;
        public Button ContinueButton => continueButton;
        public Button CloseButton => closeButton;
        public BartenderCoinBalanceView CoinBalance => coinBalance;

        public BartenderCheersSequence CheersSequence
        {
            get
            {
                EnsureCheersSequence();
                return cheersSequence;
            }
        }

        public bool IsReady => overlayGroup != null && dimmer != null && flash != null
                               && cardPivot != null && cardFrame != null
                               && levelBadge != null && levelNumberLabel != null
                               && normalCardSprite != null && hardCardSprite != null
                               && veryHardCardSprite != null && normalBadgeSprite != null
                               && hardBadgeSprite != null && veryHardBadgeSprite != null
                               && rewardShadow != null && rewardRays != null
                               && normalRewardGroup != null
                               && premiumRewardGroup != null
                               && rewardValueLabel != null
                               && coinBalance != null
                               && normalRewardShadowSprite != null
                               && hardRewardShadowSprite != null
                               && veryHardRewardShadowSprite != null
                               && rewardButton != null
                               && continueButton != null && closeButton != null;

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

        public void ResetCheersPresentation()
        {
            if (cheersSequence != null) cheersSequence.ResetPresentation();
        }

        public void SetRewardValue(int coinReward)
        {
            if (rewardValueLabel == null) return;
            rewardValueLabel.text = Mathf.Max(0, coinReward)
                .ToString(CultureInfo.InvariantCulture);
        }

        public void SetButtonsInteractable(bool canContinue, bool canClose, bool canReward)
        {
            if (continueButton != null) continueButton.interactable = canContinue;
            if (closeButton != null) closeButton.interactable = canClose;
            if (rewardButton != null) rewardButton.interactable = canReward;
        }

        private void ApplyStyle(BartenderResultPopupStyle style)
        {
            if (cardFrame != null) cardFrame.sprite = CardSpriteFor(style);
            if (levelBadge != null) levelBadge.sprite = BadgeSpriteFor(style);
            ApplyRewardStyle(style);
        }

        private void ApplyRewardStyle(BartenderResultPopupStyle style)
        {
            bool useHardVeryHardCoinPile =
                style != BartenderResultPopupStyle.Normal;
            if (normalRewardGroup != null)
                normalRewardGroup.SetActive(useHardVeryHardCoinPile);
            if (premiumRewardGroup != null)
                premiumRewardGroup.SetActive(!useHardVeryHardCoinPile);
            if (rewardRays != null)
            {
                rewardRays.anchoredPosition = new Vector2(
                    0f, style == BartenderResultPopupStyle.Normal ? -20f : 0f);
            }

            if (style == BartenderResultPopupStyle.VeryHard)
            {
                if (rewardShadow != null) rewardShadow.sprite = veryHardRewardShadowSprite;
                return;
            }

            if (style == BartenderResultPopupStyle.Hard)
            {
                if (rewardShadow != null) rewardShadow.sprite = hardRewardShadowSprite;
                return;
            }

            if (rewardShadow != null) rewardShadow.sprite = normalRewardShadowSprite;
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

        private void EnsureCheersSequence()
        {
            if (cheersSequence == null)
                cheersSequence = GetComponent<BartenderCheersSequence>();

            if (cheersSequence != null)
                cheersSequence.Configure(cardPivot);
        }
    }
}
