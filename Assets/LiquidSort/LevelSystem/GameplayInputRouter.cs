using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class GameplayInputRouter : MonoBehaviour
    {
        [Header("Authored gameplay targets")]
        [SerializeField] private BartenderPourInteraction pourInteraction;
        [SerializeField] private DeliveryBadgePresenter deliveryBadges;
        [SerializeField] private BoosterTrayInput boosterTray;

        internal enum PointerTarget { None, Ui, Booster, DeliveryBadge, Board }

        private void Update()
        {
            if (TryReadPointerDown(out Vector2 screenPoint, out int pointerId))
                RoutePointerDown(screenPoint, pointerId);
        }

        internal PointerTarget RoutePointerDown(Vector2 screenPoint, int pointerId)
        {
            if (!isActiveAndEnabled) return PointerTarget.None;

            // Query the current UI geometry; do not depend on EventSystem.Update running first.
            if (BartenderUiPointerGuard.IsPointerOverUi(screenPoint, pointerId))
                return PointerTarget.Ui;

            if (IsActiveTarget(boosterTray)
                && boosterTray.TryHandlePointerDown(Camera.main, screenPoint))
                return PointerTarget.Booster;

            if (IsActiveTarget(deliveryBadges)
                && deliveryBadges.TryHandlePointerDown(screenPoint))
                return PointerTarget.DeliveryBadge;

            if (!IsActiveTarget(pourInteraction)) return PointerTarget.None;
            pourInteraction.HandleRoutedPointerDown(screenPoint);
            return PointerTarget.Board;
        }

        private bool IsActiveTarget(Behaviour target) =>
            target != null && target.isActiveAndEnabled
            && target.gameObject.scene == gameObject.scene;

        public bool ValidateBindings(out string reason)
        {
            if (pourInteraction == null || deliveryBadges == null || boosterTray == null)
            {
                reason = "Gameplay input router requires pour, badge and booster targets.";
                return false;
            }
            if (pourInteraction.gameObject.scene != gameObject.scene
                || deliveryBadges.gameObject.scene != gameObject.scene
                || boosterTray.gameObject.scene != gameObject.scene)
            {
                reason = "Gameplay input targets must belong to the router's scene.";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool TryReadPointerDown(out Vector2 screenPoint, out int pointerId)
        {
            int touchCount = Input.touchCount;
            for (int i = 0; i < touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase != TouchPhase.Began) continue;
                screenPoint = touch.position;
                pointerId = touch.fingerId;
                return true;
            }
            if (touchCount == 0 && Input.GetMouseButtonDown(0))
            {
                screenPoint = Input.mousePosition;
                pointerId = -1;
                return true;
            }
            screenPoint = default;
            pointerId = -1;
            return false;
        }
    }
}
