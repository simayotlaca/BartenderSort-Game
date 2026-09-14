using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class DeliveryBadgePresenter : MonoBehaviour
    {
        [Serializable]
        public sealed class BadgeBinding
        {
            [Tooltip("Royal glass from the pool.")]
            public LiquidBottle bottle;
            [Tooltip("The glass check badge. Keep it under the glass.")]
            public Transform badge;
            [Tooltip("Badge sprite used to measure the tap area.")]
            public SpriteRenderer badgeRenderer;
            [Tooltip("Rest scale used after the badge bounce.")]
            public Vector3 authoredLocalScale = Vector3.one;
        }

        [Header("Rig references")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [Tooltip("Sends badge and glass taps to the delivery command.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;
        [Tooltip("Scene camera used to map badge taps to world space.")]
        [SerializeField] private Camera inputCamera;

        [Header("Hand-authored badges")]
        [Tooltip("One entry per pooled glass. Glasses without badges are skipped.")]
        [SerializeField] private List<BadgeBinding> badges = new List<BadgeBinding>();

        [Header("Tap")]
        [Tooltip("Tap the badge to deliver. Disable for display only.")]
        [SerializeField] private bool tapDelivers = true;
        [Tooltip("Extra tap padding in layout units.")]
        [SerializeField, Min(0f)] private float tapPadding = 0.12f;

        [Header("Entrance")]
        [Tooltip("Bounce only when the badge first appears.")]
        [SerializeField, Min(0f)] private float popDuration = 0.22f;
        [SerializeField, Range(1f, 1.25f)] private float popScale = 1.08f;

        private readonly Dictionary<LiquidBottle, BadgeBinding> badgeByBottle =
            new Dictionary<LiquidBottle, BadgeBinding>();
        private readonly HashSet<LiquidBottle> uniqueBadgeBottleScratch =
            new HashSet<LiquidBottle>();
        private readonly HashSet<Transform> uniqueBadgeTransformScratch =
            new HashSet<Transform>();
        private readonly HashSet<SpriteRenderer> uniqueBadgeRendererScratch =
            new HashSet<SpriteRenderer>();
        private readonly HashSet<BadgeBinding> shownBadges = new HashSet<BadgeBinding>();
        private readonly HashSet<BadgeBinding> heldBadges = new HashSet<BadgeBinding>();
        private readonly HashSet<BadgeBinding> readyBadges = new HashSet<BadgeBinding>();

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedShelfView;
        private bool cacheBuilt;

        private void Awake()
        {
            BuildCache();
            HideAll();
        }

        private void OnEnable()
        {
            Subscribe();
            RefreshMatches();
        }

        private void OnDisable()
        {
            Unsubscribe();
            HideAll();
        }

        private void OnValidate()
        {
            tapPadding = Mathf.Max(0f, tapPadding);
            popDuration = Mathf.Max(0f, popDuration);
            popScale = Mathf.Clamp(popScale, 1f, 1.25f);
        }

        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "Level controller missing.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "Shelf view missing.";
                return false;
            }
            if (shelfView.Controller != controller)
            {
                reason = "Badge and shelf use different controllers.";
                return false;
            }
            if (tapDelivers && pourInteraction == null)
            {
                reason = "Badge delivery needs pour interaction.";
                return false;
            }
            if (tapDelivers && inputCamera == null)
            {
                reason = "Badge delivery needs a scene camera.";
                return false;
            }
            if (pourInteraction != null
                && (pourInteraction.Controller != controller
                    || pourInteraction.ShelfView != shelfView))
            {
                reason = "Badge and pour use different rigs.";
                return false;
            }
            if (badges == null)
            {
                reason = "Badge list missing.";
                return false;
            }
            uniqueBadgeBottleScratch.Clear();
            uniqueBadgeTransformScratch.Clear();
            uniqueBadgeRendererScratch.Clear();
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.bottle == null)
                {
                    reason = $"Badges[{i}] bottle missing.";
                    return false;
                }
                if (!uniqueBadgeBottleScratch.Add(binding.bottle))
                {
                    reason = $"Badges[{i}] duplicate bottle.";
                    return false;
                }
                if (binding.badge == null)
                {
                    reason = $"Badges[{i}] badge missing.";
                    return false;
                }
                if (!uniqueBadgeTransformScratch.Add(binding.badge))
                {
                    reason = $"Badges[{i}] duplicate badge.";
                    return false;
                }
                if (binding.badge == binding.bottle.transform
                    || !binding.badge.IsChildOf(binding.bottle.transform))
                {
                    reason = $"Badges[{i}] badge is outside its bottle.";
                    return false;
                }
                if (binding.badgeRenderer == null)
                {
                    reason = $"Badges[{i}] renderer missing.";
                    return false;
                }
                if (!uniqueBadgeRendererScratch.Add(binding.badgeRenderer))
                {
                    reason = $"Badges[{i}] duplicate renderer.";
                    return false;
                }
                if (binding.badgeRenderer.transform != binding.badge
                    && !binding.badgeRenderer.transform.IsChildOf(binding.badge))
                {
                    reason = $"Badges[{i}] renderer is outside its badge.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Badge Bindings")]
        private void ValidateFromContextMenu()
        {
            if (!ValidateBindings(out string reason))
                Debug.LogError("Delivery badge binding error: " + reason, this);
        }

        public bool IsReadyForDelivery(LiquidBottle glass)
        {
            BuildCache();
            if (glass == null) return false;
            if (!badgeByBottle.TryGetValue(glass, out BadgeBinding binding)
                || binding == null || binding.badge == null) return true;
            return shownBadges.Contains(binding)
                && readyBadges.Contains(binding)
                && binding.badge.gameObject.activeInHierarchy;
        }

        public void HoldForCompletionCue(LiquidBottle glass)
        {
            BuildCache();
            if (glass == null
                || !badgeByBottle.TryGetValue(glass, out BadgeBinding binding)
                || binding == null || binding.badge == null) return;

            heldBadges.Add(binding);
            readyBadges.Remove(binding);
            shownBadges.Remove(binding);
            KillBadgeTween(binding.badge);
            binding.badge.localScale = binding.authoredLocalScale;
            binding.badge.gameObject.SetActive(false);
        }

        public void ReconcileCancelledCompletion(LiquidBottle glass)
        {
            BuildCache();
            if (glass == null
                || !badgeByBottle.TryGetValue(glass, out BadgeBinding binding)
                || binding == null || binding.badge == null) return;

            bool wasHeld = heldBadges.Remove(binding);
            bool wasShownButPending = shownBadges.Contains(binding)
                                      && !readyBadges.Contains(binding);
            if (!wasHeld && !wasShownButPending) return;

            if (!wasHeld && binding.badge.gameObject.activeSelf
                && TryResolveCurrentMatch(glass, out _))
            {
                readyBadges.Add(binding);
                return;
            }

            readyBadges.Remove(binding);
            shownBadges.Remove(binding);
            KillBadgeTween(binding.badge);
            binding.badge.localScale = binding.authoredLocalScale;
            if (!TryResolveCurrentMatch(glass, out _))
            {
                binding.badge.gameObject.SetActive(false);
                return;
            }

            // The motion owner already returned the glass home, so this cancelled path cannot deliver it
            // mid-air.
            SetMatched(binding, true, true);
        }

        public void RevealFromCompletionCue(LiquidBottle glass)
        {
            BuildCache();
            if (!TryResolveCurrentMatch(glass, out BadgeBinding binding)) return;
            heldBadges.Remove(binding);
            SetMatched(binding, true, false);
        }

        public void MarkCompletionBadgeReady(LiquidBottle glass)
        {
            BuildCache();
            if (!TryResolveCurrentMatch(glass, out BadgeBinding binding)
                || !shownBadges.Contains(binding)) return;
            heldBadges.Remove(binding);
            readyBadges.Add(binding);
        }

        private void HandleOrdersChanged() => RefreshMatches();
        private void HandleBoardCommitted(BartenderBoardChange _) => RefreshMatches();

        private void HandleLevelLoaded(BsLevel level)
        {
            HideAll();
            RefreshMatches();
        }

        private void HandleShelfPresentationChanged() => RefreshMatches();

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state == BartenderLevelState.Unloaded
                || state == BartenderLevelState.CampaignComplete)
                HideAll();
            else
                RefreshMatches();
        }

        private void RefreshMatches()
        {
            BuildCache();
            if (controller == null || shelfView == null || !shelfView.Ready)
            {
                HideAll();
                return;
            }

            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null) continue;

                int glassId = -1;
                bool mapped = binding.bottle != null
                    && binding.bottle.gameObject.activeInHierarchy
                    && shelfView.TryGetGlassId(binding.bottle, out glassId);
                if (mapped && shelfView.IsGlassSynchronizationDeferred(glassId)) continue;
                bool matched = mapped && controller.MatchedOrderSlot(glassId) >= 0;
                SetMatched(binding, matched);
            }
        }

        private void LateUpdate()
        {
            if (shownBadges.Count == 0) return;
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null
                    || !shownBadges.Contains(binding)) continue;
                if (binding.bottle == null || !binding.bottle.gameObject.activeInHierarchy
                    || shelfView == null || !shelfView.Ready
                    || !shelfView.TryGetGlassId(binding.bottle, out _))
                    SetMatched(binding, false);
            }
        }

        private void SetMatched(BadgeBinding binding, bool matched,
                                bool readyWhenShown = true)
        {
            if (!matched)
            {
                heldBadges.Remove(binding);
                readyBadges.Remove(binding);
            }
            if (matched && heldBadges.Contains(binding))
            {
                shownBadges.Remove(binding);
                readyBadges.Remove(binding);
                KillBadgeTween(binding.badge);
                binding.badge.localScale = binding.authoredLocalScale;
                binding.badge.gameObject.SetActive(false);
                return;
            }

            bool shown = shownBadges.Contains(binding);
            if (shown == matched) return;

            GameObject badgeObject = binding.badge.gameObject;
            KillBadgeTween(binding.badge);

            if (!matched)
            {
                shownBadges.Remove(binding);
                binding.badge.localScale = binding.authoredLocalScale;
                badgeObject.SetActive(false);
                return;
            }

            shownBadges.Add(binding);
            if (readyWhenShown) readyBadges.Add(binding);
            else readyBadges.Remove(binding);
            badgeObject.SetActive(true);
            if (popDuration <= 0f)
            {
                binding.badge.localScale = binding.authoredLocalScale;
                return;
            }

            float up = popDuration * 0.4f;
            binding.badge.localScale = Vector3.zero;
            Sequence pop = DOTween.Sequence().SetRecyclable(true)
                .SetTarget(binding.badge).SetUpdate(true);
            pop.Append(binding.badge
                .DOScale(binding.authoredLocalScale * popScale, up)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            pop.Append(binding.badge
                .DOScale(binding.authoredLocalScale, popDuration - up)
                .SetEase(Ease.OutSine).SetRecyclable(true));
        }

        private bool TryResolveCurrentMatch(LiquidBottle glass,
                                            out BadgeBinding binding)
        {
            binding = null;
            return isActiveAndEnabled && glass != null
                && badgeByBottle.TryGetValue(glass, out binding)
                && binding != null && binding.badge != null
                && controller != null && shelfView != null && shelfView.Ready
                && glass.gameObject.activeInHierarchy
                && shelfView.TryGetGlassId(glass, out int glassId)
                && controller.MatchedOrderSlot(glassId) >= 0;
        }

        private void HideAll()
        {
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null) continue;
                KillBadgeTween(binding.badge);
                binding.badge.localScale = binding.authoredLocalScale;
                binding.badge.gameObject.SetActive(false);
            }
            shownBadges.Clear();
            heldBadges.Clear();
            readyBadges.Clear();
        }

        private static void KillBadgeTween(Transform badge)
        {
            if (DOTween.IsTweening(badge)) badge.DOKill();
        }

        internal bool TryHandlePointerDown(Vector2 screenPoint)
        {
            if (!isActiveAndEnabled || !tapDelivers
                || controller == null || controller.State != BartenderLevelState.Playing
                || !TryPickBadge(screenPoint, out int glassId)) return false;

            // Playing badges own taps even if a save, animation or policy rejects delivery.
            // After the round ends, world taps belong to the board's retry/continue fallback.
            if (CanAcceptTap()) pourInteraction.RequestDelivery(glassId);
            return true;
        }

        private bool CanAcceptTap()
        {
            // Pours elsewhere, saves, leaving cards and a closing gap do not hold a badge tap. The delivery request
            // checks the glass itself and keeps tap order through the input lane. Modal states (tutorial, shop,
            // shuffle selection, TIME'S UP) still ignore it silently, as before.
            return controller != null && shelfView != null
                && shelfView.Ready
                && !shelfView.BlockingSeatRunPlaying
                && !controller.ModalInputBlocked
                && pourInteraction != null && pourInteraction.isActiveAndEnabled;
        }

        private bool TryPickBadge(Vector2 screenPoint, out int glassId)
        {
            glassId = -1;
            Camera camera = inputCamera;
            if (camera == null || shownBadges.Count == 0) return false;

            float bestDistance = float.MaxValue;
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || !shownBadges.Contains(binding)
                    || binding.badgeRenderer == null
                    || !binding.badgeRenderer.enabled
                    || !binding.badgeRenderer.gameObject.activeInHierarchy) continue;
                if (binding.bottle == null
                    || !shelfView.TryGetGlassId(binding.bottle, out int candidateId))
                    continue;

                Bounds bounds = binding.badgeRenderer.bounds;
                float depth = Vector3.Dot(bounds.center - camera.transform.position,
                                          camera.transform.forward);
                Vector3 world = camera.ScreenToWorldPoint(
                    new Vector3(screenPoint.x, screenPoint.y, depth));
                if (Mathf.Abs(world.x - bounds.center.x) > bounds.extents.x + tapPadding
                    || Mathf.Abs(world.y - bounds.center.y) > bounds.extents.y + tapPadding)
                    continue;

                float distance = (world - bounds.center).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                glassId = candidateId;
            }
            return glassId >= 0;
        }

        private void BuildCache()
        {
            if (cacheBuilt) return;
            cacheBuilt = true;
            badgeByBottle.Clear();
            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.bottle == null) continue;
                badgeByBottle[binding.bottle] = binding;
            }
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.BoardCommitted -= HandleBoardCommitted;
                    subscribedController.OrdersChanged -= HandleOrdersChanged;
                    subscribedController.StateChanged -= HandleStateChanged;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.BoardCommitted += HandleBoardCommitted;
                    subscribedController.OrdersChanged += HandleOrdersChanged;
                    subscribedController.StateChanged += HandleStateChanged;
                }
            }

            if (subscribedShelfView == shelfView) return;
            if (subscribedShelfView != null)
                subscribedShelfView.PresentationChanged -=
                    HandleShelfPresentationChanged;
            subscribedShelfView = shelfView;
            if (subscribedShelfView != null)
                subscribedShelfView.PresentationChanged +=
                    HandleShelfPresentationChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.BoardCommitted -= HandleBoardCommitted;
                subscribedController.OrdersChanged -= HandleOrdersChanged;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            if (subscribedShelfView != null)
                subscribedShelfView.PresentationChanged -=
                    HandleShelfPresentationChanged;
            subscribedController = null;
            subscribedShelfView = null;
        }
    }
}
