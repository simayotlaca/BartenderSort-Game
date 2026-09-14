using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Authored Today's Orders popup; the presenter only toggles, fills and animates these parts.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderDailyOrdersView : MonoBehaviour
    {
        [SerializeField] private CanvasGroup group;
        [SerializeField] private RectTransform panel;
        [SerializeField] private Button backdropButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button actionButton;
        [SerializeField] private Image actionImage;
        [SerializeField] private TextMeshProUGUI actionLabel;
        [Tooltip("CLAIM artwork with the 500 COINS lettering baked in.")]
        [SerializeField] private Sprite claimArtwork;
        [Tooltip("Text-free CLAIM frame, used when the reward total is not 500.")]
        [SerializeField] private Sprite claimFrame;
        [SerializeField] private TextMeshProUGUI countdownText;
        [SerializeField] private TextMeshProUGUI completedText;
        [SerializeField] private TextMeshProUGUI completionBonusText;
        [SerializeField] private TextMeshProUGUI feedbackText;
        [Tooltip("In order: delivered orders, won levels, served units.")]
        [SerializeField] private DailyOrderCardView[] cards = new DailyOrderCardView[3];

        internal CanvasGroup Group => group;
        internal RectTransform Panel => panel;
        internal Button BackdropButton => backdropButton;
        internal Button CloseButton => closeButton;
        internal Button ActionButton => actionButton;
        internal Image ActionImage => actionImage;
        internal TextMeshProUGUI ActionLabel => actionLabel;
        internal Sprite ClaimArtwork => claimArtwork;
        internal Sprite ClaimFrame => claimFrame;
        internal TextMeshProUGUI CountdownText => countdownText;
        internal TextMeshProUGUI CompletedText => completedText;
        internal TextMeshProUGUI CompletionBonusText => completionBonusText;
        internal TextMeshProUGUI FeedbackText => feedbackText;
        internal DailyOrderCardView[] Cards => cards;

        internal bool IsReady => group != null && panel != null && closeButton != null
            && actionButton != null && actionImage != null && actionLabel != null
            && cards != null && cards.Length == 3
            && Array.TrueForAll(cards, card => card != null && card.IsReady);
    }
}
