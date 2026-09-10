using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Adds a small DOTween response to settings button taps.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Selectable))]
    public sealed class BartenderSettingsButtonFeedback : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] private Vector3 defaultScale = Vector3.one;
        [SerializeField] private Vector3 hoverScale = Vector3.one * 1.05f;
        [SerializeField] private Vector3 pressedScale = Vector3.one * 0.90f;
        [SerializeField, Min(0f)] private float duration = 0.15f;

        private Selectable selectable;
        private RectTransform rectTransform;
        private bool hovered;
        private bool pressed;

        private void Awake()
        {
            selectable = GetComponent<Selectable>();
            rectTransform = GetComponent<RectTransform>();
            CenterPivotWithoutMoving();
        }

        private void OnDisable()
        {
            transform.DOKill(false);
            transform.localScale = defaultScale;
            hovered = false;
            pressed = false;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!CanAnimate()) return;
            hovered = true;
            if (!pressed) AnimateTo(hoverScale, Ease.OutBack);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!CanAnimate()) return;
            hovered = false;
            if (!pressed) AnimateTo(defaultScale, Ease.OutQuad);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!CanAnimate()) return;
            pressed = true;
            AnimateTo(pressedScale, Ease.OutQuad);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!CanAnimate()) return;
            pressed = false;
            AnimateTo(hovered ? hoverScale : defaultScale,
                hovered ? Ease.OutBack : Ease.OutQuad);
        }

        private bool CanAnimate()
        {
            if (selectable == null) selectable = GetComponent<Selectable>();
            return selectable != null && selectable.IsInteractable();
        }

        private void AnimateTo(Vector3 scale, Ease ease)
        {
            transform.DOKill(false);
            transform.DOScale(scale, duration).SetEase(ease).SetUpdate(true);
        }

        private void CenterPivotWithoutMoving()
        {
            if (rectTransform == null) return;
            Vector2 oldPivot = rectTransform.pivot;
            Vector2 newPivot = new Vector2(0.5f, 0.5f);
            if (oldPivot == newPivot) return;

            Vector2 delta = newPivot - oldPivot;
            delta.x *= rectTransform.rect.width;
            delta.y *= rectTransform.rect.height;
            rectTransform.pivot = newPivot;
            rectTransform.anchoredPosition += delta;
        }
    }
}
