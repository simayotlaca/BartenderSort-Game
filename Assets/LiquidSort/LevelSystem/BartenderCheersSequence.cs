using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Drives result-card entry and shares timing with round feedback. Keep these constants aligned with the
    /// Animator in <c>CheersToast_Manual</c> and its animation clip.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BartenderCheersSequence : MonoBehaviour
    {
        // Keep the clink at 1.65s aligned with audio and liquid contact. Words enter at 0.10/0.72s; the
        // card is usable at 3.30s while music finishes.
        public const float TitleTime = 0.10f;
        // Keep the original glass approach; the logo reveal fills the lead-in.
        public const float GlassEntryTime = 1.27f;
        public const float ContactTime = 1.65f;
        public const float GlassExitTime = 2.75f;
        public const float CardRevealTime = 2.90f;
        public const float Duration = 3.30f;

        private static readonly int ToastState = Animator.StringToHash("Base Layer.Toast One Shot");

        private RectTransform cardPivot;
        private RectTransform toastViewport;
        private RectTransform toastComposition;
        private RectTransform toastDimmer;
        private Animator toastAnimator;
        private Image originalCardTitle;
        private BartenderCoinRewardEffect rewardEffect;
        private bool configured;
        private int presentationRevision;

        public bool IsReady => configured && cardPivot != null
            && toastAnimator != null && toastAnimator.enabled
            && toastAnimator.runtimeAnimatorController != null;

        public void Configure(RectTransform authoredCardPivot)
        {
            if (configured) return;
            if (authoredCardPivot == null) return;

            cardPivot = authoredCardPivot;
            Transform toastRoot = transform.Find("CheersToast_Manual");
            toastViewport = transform as RectTransform;
            if (toastRoot != null)
            {
                toastAnimator = toastRoot.GetComponent<Animator>();
                toastComposition = toastRoot as RectTransform;
                toastDimmer = toastRoot.Find("Dimmer") as RectTransform;
            }
            RefreshToastLayout();
            // Keep the card's own CHEERS title.
            originalCardTitle = FindDirectImage(cardPivot, "CheersTitle");
            if (originalCardTitle != null) originalCardTitle.enabled = true;
            rewardEffect = cardPivot.GetComponentInChildren<BartenderCoinRewardEffect>(true);

            configured = true;
            ResetPresentation();
        }

        public void Prepare(float hiddenCardOffset, float cardStartScale)
        {
            if (!IsReady) return;
            presentationRevision++;

            float safeOffset = Mathf.Max(760f, hiddenCardOffset);
            cardPivot.anchoredPosition = new Vector2(0f, -safeOffset);
            cardPivot.localScale = Vector3.one * Mathf.Clamp(cardStartScale, 0.5f, 1f);
            if (rewardEffect != null) rewardEffect.enabled = false;

            // Rewind on every win so TerminalReady starts audio and animation together, even on reused
            // popups.
            if (toastAnimator != null) toastAnimator.gameObject.SetActive(true);
            RefreshToastLayout();
            if (toastAnimator != null && toastAnimator.isActiveAndEnabled
                && toastAnimator.runtimeAnimatorController != null)
            {
                toastAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
                toastAnimator.speed = 1f;
                toastAnimator.Play(ToastState, 0, 0f);
                toastAnimator.Update(0f);
            }
        }

        public void InsertInto(Sequence sequence)
        {
            if (!IsReady || sequence == null) return;
            // Clear the toast as the card enters. Music finishes over it using this cue's timing.
            float safeCardDuration = Duration - CardRevealTime;
            int revision = presentationRevision;
            sequence.InsertCallback(CardRevealTime, () =>
            {
                if (revision != presentationRevision) return;
                if (rewardEffect != null) rewardEffect.enabled = true;
            });
            Insert(sequence, CardRevealTime,
                cardPivot.DOAnchorPos(Vector2.zero, safeCardDuration)
                    .SetEase(Ease.OutCubic));
            Insert(sequence, CardRevealTime,
                cardPivot.DOScale(Vector3.one, safeCardDuration)
                    .SetEase(Ease.OutBack));
        }

        public void ResetPresentation()
        {
            presentationRevision++;
            // The presenter owns this sequence. Do not retain it after completion because DOTween may
            // recycle it for another view.
            if (cardPivot != null) cardPivot.DOKill(false);
            if (rewardEffect != null) rewardEffect.enabled = true;
            if (originalCardTitle != null) originalCardTitle.enabled = true;
            if (toastAnimator != null) toastAnimator.gameObject.SetActive(false);
        }

        private void OnRectTransformDimensionsChange()
        {
            RefreshToastLayout();
        }

        private void RefreshToastLayout()
        {
            LayoutToastComposition(toastComposition, toastViewport, toastDimmer);
        }

        // Both the opening and the win toast use the approved 1100 x 670 authoring space.
        // Fit inside the reference viewport independently of their different CanvasScaler modes.
        internal static void LayoutToastComposition(RectTransform composition, RectTransform viewport,
            RectTransform fullscreenDimmer = null)
        {
            if (composition == null || viewport == null) return;
            Vector2 viewportSize = viewport.rect.size;
            float scale = Mathf.Min(viewportSize.x / 720f, viewportSize.y / 1280f);
            if (scale <= 0f) return;

            Vector2 center = new Vector2(0.5f, 0.5f);
            Vector2 offset = new Vector2(0f, -340f);
            composition.anchorMin = composition.anchorMax = center;
            composition.pivot = center;
            composition.sizeDelta = new Vector2(1100f, 670f);
            composition.localScale = Vector3.one * scale;
            composition.anchoredPosition = offset * scale;

            // Only the artwork moves. Keep the animated dimmer covering the whole screen.
            if (fullscreenDimmer == null) return;
            fullscreenDimmer.anchorMin = fullscreenDimmer.anchorMax = center;
            fullscreenDimmer.pivot = center;
            fullscreenDimmer.localScale = Vector3.one;
            fullscreenDimmer.anchoredPosition = -offset;
            fullscreenDimmer.sizeDelta = viewportSize / scale;
        }

        private static Image FindDirectImage(RectTransform parent, string childName)
        {
            if (parent == null) return null;
            Transform child = parent.Find(childName);
            return child != null ? child.GetComponent<Image>() : null;
        }

        private static void Insert(Sequence sequence, float time, Tween tween)
        {
            if (sequence == null || tween == null) return;
            sequence.Insert(time, tween.SetRecyclable(true));
        }
    }
}
