using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows a ready-order badge and delivers through BartenderPourInteraction. Existing badges must be glass
    /// children so pooling hides both together; tapping the glass also delivers.
    /// </summary>
    [DisallowMultipleComponent]
    // Read input before BartenderPourInteraction so badge delivery gets the first try; rejected taps can still
    // select or pour.
    [DefaultExecutionOrder(-50)]
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

        /// <summary>
        /// Checks bindings and requires the badge to be a glass child so both hide together when pooled.
        /// </summary>
        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "BartenderShelfLevelView Inspector referansı eksik.";
                return false;
            }
            if (shelfView.Controller != controller)
            {
                reason = "Rozet ve raf görünümü aynı controller rig'ine bağlı değil.";
                return false;
            }
            if (tapDelivers && pourInteraction == null)
            {
                reason = "Rozet teslimi açık ama BartenderPourInteraction bağlantısı eksik.";
                return false;
            }
            if (tapDelivers && inputCamera == null)
            {
                reason = "Rozet teslimi açık ama authored sahne kamerası bağlı değil.";
                return false;
            }
            if (pourInteraction != null
                && (pourInteraction.Controller != controller
                    || pourInteraction.ShelfView != shelfView))
            {
                reason = "Rozet ve pour interaction aynı controller/view rig'ine bağlı değil.";
                return false;
            }
            if (badges == null)
            {
                reason = "Authored rozet listesi eksik.";
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
                    reason = $"Badges[{i}] bardak referansı eksik.";
                    return false;
                }
                if (!uniqueBadgeBottleScratch.Add(binding.bottle))
                {
                    reason = $"Badges[{i}] ({binding.bottle.name}) aynı bardak için ikinci "
                           + "kez bağlanmış.";
                    return false;
                }
                if (binding.badge == null)
                {
                    reason = $"Badges[{i}] ({binding.bottle.name}) rozet referansı eksik.";
                    return false;
                }
                if (!uniqueBadgeTransformScratch.Add(binding.badge))
                {
                    reason = $"Badges[{i}] ({binding.bottle.name}) başka bir satırla aynı "
                           + "rozet transformunu kullanıyor.";
                    return false;
                }
                if (binding.badge == binding.bottle.transform
                    || !binding.badge.IsChildOf(binding.bottle.transform))
                {
                    reason = $"Badges[{i}] rozeti {binding.bottle.name} bardağının çocuğu "
                           + "değil; bardak kapanınca onunla birlikte kapanmaz.";
                    return false;
                }
                if (binding.badgeRenderer == null)
                {
                    reason = $"Badges[{i}] rozet SpriteRenderer'ı eksik; dokunma alanı "
                           + "onun sınırlarından okunuyor.";
                    return false;
                }
                if (!uniqueBadgeRendererScratch.Add(binding.badgeRenderer))
                {
                    reason = $"Badges[{i}] ({binding.bottle.name}) başka bir satırla aynı "
                           + "rozet SpriteRenderer'ını kullanıyor.";
                    return false;
                }
                if (binding.badgeRenderer.transform != binding.badge
                    && !binding.badgeRenderer.transform.IsChildOf(binding.badge))
                {
                    reason = $"Badges[{i}] ({binding.bottle.name}) SpriteRenderer'ı bağlı "
                           + "rozet transformunun parçası değil.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        [ContextMenu("Validate Badge Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log($"Delivery badges: {badges.Count} bağlantı geçerli.", this);
            else
                Debug.LogError("Delivery badge binding error: " + reason, this);
        }

        /// <summary>
        /// Glasses without badges still allow body-tap delivery. Bound badges unlock after the completion
        /// animation settles.
        /// </summary>
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

        /// <summary>
        /// Hides a new match until its completion cue. Call before releasing the deferred shelf refresh.
        /// </summary>
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

        /// <summary>
        /// After cancellation, show and unlock a still-valid match at its authored scale. Stale levels or
        /// bottles stay hidden.
        /// </summary>
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

        /// <summary>Starts the 220 ms badge pop as the completed glass descends.</summary>
        public void RevealFromCompletionCue(LiquidBottle glass)
        {
            BuildCache();
            if (!TryResolveCurrentMatch(glass, out BadgeBinding binding)) return;
            heldBadges.Remove(binding);
            SetMatched(binding, true, false);
        }

        /// <summary>
        /// Opens the hit area when the glass reaches its exact shelf pose, together with Check SFX.
        /// </summary>
        public void MarkCompletionBadgeReady(LiquidBottle glass)
        {
            BuildCache();
            if (!TryResolveCurrentMatch(glass, out BadgeBinding binding)
                || !shownBadges.Contains(binding)) return;
            heldBadges.Remove(binding);
            readyBadges.Add(binding);
        }

        // ---- Match state ----------------------------------------------------------

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

        /// <summary>
        /// Tracks glasses matching an open order. A later pour can remove the match and hide the badge.
        /// </summary>
        private void RefreshMatches()
        {
            BuildCache();
            if (controller == null || shelfView == null || !shelfView.Ready)
            {
                HideAll();
                return;
            }

            // Wait for the deferred shelf refresh so the badge appears with completion, not during the old
            // pour visuals.
            if (shelfView.SynchronizationDeferred) return;

            for (int i = 0; i < badges.Count; i++)
            {
                BadgeBinding binding = badges[i];
                if (binding == null || binding.badge == null) continue;

                bool matched = binding.bottle != null
                    && binding.bottle.gameObject.activeInHierarchy
                    && shelfView.TryGetGlassId(binding.bottle, out int glassId)
                    && controller.MatchedOrderSlot(glassId) >= 0;
                SetMatched(binding, matched);
            }
        }

        /// <summary>Checks each frame and hides badges on pooled glasses so reuse cannot show an old match.</summary>
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

            // Make this success cue noticeable so the player sees the match.
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

        // ---- Tap ------------------------------------------------------------------

        private void Update()
        {
            if (!tapDelivers || !CanAcceptTap()) return;

            if (Input.touchCount > 0)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    Touch touch = Input.GetTouch(i);
                    if (touch.phase != TouchPhase.Began
                        || BartenderUiPointerGuard.IsPointerOverUi(
                            touch.position, touch.fingerId)
                        || !TryPickBadge(touch.position, out int touchGlassId))
                        continue;

                    pourInteraction.TryCommitDelivery(touchGlassId, out _);
                    return;
                }
                // A touch can also create a mouse click; handle the physical tap only once.
                return;
            }

            if (!Input.GetMouseButtonDown(0)) return;
            Vector2 mousePosition = Input.mousePosition;
            if (BartenderUiPointerGuard.IsPointerOverUi(mousePosition, -1)
                || !TryPickBadge(mousePosition, out int mouseGlassId)) return;
            pourInteraction.TryCommitDelivery(mouseGlassId, out _);
        }

        private bool CanAcceptTap()
        {
            return controller != null && shelfView != null
                && shelfView.Ready
                && !shelfView.SeatAnimationPlaying
                && !shelfView.SynchronizationDeferred
                && !controller.PresentationLocked
                && pourInteraction != null && !pourInteraction.Busy;
        }

        /// <summary>
        /// Hit-tests visible badges using sprite bounds plus padding. Glass body taps use the same delivery
        /// command through BartenderPourInteraction.
        /// </summary>
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
                    || !readyBadges.Contains(binding)
                    || binding.badgeRenderer == null
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

                // For overlapping badges, pick the closest centre.
                float distance = (world - bounds.center).sqrMagnitude;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                glassId = candidateId;
            }
            return glassId >= 0;
        }

        // ---- Wiring ---------------------------------------------------------------

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
