using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows hidden-colour reveals and delivery unlocks from controller receipts after the shelf refreshes.
    /// Shader and lock-sprite effects leave bottle transforms to layout and pouring.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Gameplay/Mechanic Reveal Presenter")]
    public sealed class MechanicRevealPresenter : MonoBehaviour
    {
        private enum BeatKind
        {
            HiddenReveal,
            LockOpened
        }

        private struct PendingBeat
        {
            public int Revision;
            public int GlassId;
            public BeatKind Kind;
            public Color Tint;
            public int UnitIndex;
        }

        private sealed class ActiveFeedback
        {
            public LiquidBottle Bottle;
            public BottleShell Shell;
            public BeatKind Kind;
            public SpriteRenderer Renderer;
            public Coroutine Routine;
            public Tween Tween;
            public Vector3 BaseLocalPosition;
            public Quaternion BaseLocalRotation;
            public Vector3 BaseLocalScale;
            public int Revision;
            public int ModelVersion;
        }

        private const string FeedbackChildName = "MechanicRevealFeedback";

        [Header("Runtime binding")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;

        [Header("Hidden colour reveal")]
        [SerializeField, Min(0.05f)] private float revealDuration = 0.56f;
        [SerializeField, Range(0f, 0.35f)] private float revealHop = 0.13f;
        [SerializeField, Range(0f, 0.3f)] private float revealScale = 0.09f;
        [SerializeField, Range(0f, 1f)] private float revealAlpha = 0.68f;
        [SerializeField] private Color revealFallback = new Color(0.25f, 0.86f, 1f, 1f);

        [Header("Chain / layer lock opened")]
        [SerializeField, Min(0.05f)] private float unlockDuration = 0.72f;
        [SerializeField, Range(0f, 0.35f)] private float unlockHop = 0.10f;
        [SerializeField, Range(0f, 0.3f)] private float unlockScale = 0.12f;
        [SerializeField, Range(0f, 15f)] private float unlockWobbleDegrees = 6f;
        [SerializeField] private Color unlockGold = new Color(1f, 0.72f, 0.16f, 0.76f);

        [Header("Overlay")]
        [SerializeField] private Material revealOverlayMaterial;
        [SerializeField, Min(1)] private int sortingBoost = 14;

        private readonly List<PendingBeat> pending = new List<PendingBeat>(8);
        private readonly Dictionary<LiquidBottle, ActiveFeedback> active =
            new Dictionary<LiquidBottle, ActiveFeedback>();

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            StopAllFeedback();
            pending.Clear();
        }

        private void OnValidate()
        {
            revealDuration = Mathf.Max(0.05f, revealDuration);
            unlockDuration = Mathf.Max(0.05f, unlockDuration);
            revealHop = Mathf.Clamp(revealHop, 0f, 0.35f);
            unlockHop = Mathf.Clamp(unlockHop, 0f, 0.35f);
            revealScale = Mathf.Clamp(revealScale, 0f, 0.3f);
            unlockScale = Mathf.Clamp(unlockScale, 0f, 0.3f);
            unlockWobbleDegrees = Mathf.Clamp(unlockWobbleDegrees, 0f, 15f);
            revealAlpha = Mathf.Clamp01(revealAlpha);
            sortingBoost = Mathf.Max(1, sortingBoost);
        }

        private void LateUpdate()
        {
            RebindIfNeeded();
            TryFlushPending();
        }

        private void HandlePoured(BartenderPourReceipt receipt)
        {
            if (!IsCurrentPourReceipt(receipt)) return;

            StopFeedbackForGlass(receipt.SourceBefore != null
                ? receipt.SourceBefore.Id
                : -1);
            StopFeedbackForGlass(receipt.TargetBefore != null
                ? receipt.TargetBefore.Id
                : -1);

            if (!TryGetRevealedTop(receipt, out Layer revealed)
                || revealed.IsLocked(controller.Board.Delivered)) return;

            Color tint = controller != null && controller.Palette != null
                ? controller.Palette.ColorAt(revealed.Color)
                : revealFallback;
            // Dark drink colours still need to read as a light beat over dark glass art.
            tint = Color.Lerp(tint, Color.white, 0.22f);
            tint.a = revealAlpha;
            Queue(receipt.Revision, receipt.SourceAfter.Id,
                  BeatKind.HiddenReveal, tint, receipt.SourceAfter.Layers.Count - 1);
        }

        private bool IsCurrentPourReceipt(BartenderPourReceipt receipt)
        {
            return controller != null && receipt != null
                && receipt.AttemptId.IsValid
                && receipt.OperationId.IsValid
                && receipt.DomainRevision > 0L
                && receipt.BoardRevision >= 0
                && receipt.Cause == BsRoundTransitionCause.PlayerPour
                && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                    receipt.AttemptId,
                    receipt.Token,
                    receipt.DomainRevision,
                    receipt.BoardRevision);
        }

        private void HandleBoardCommitted(BartenderBoardChange change)
        {
            BartenderDeliveryReceipt receipt = change != null
                ? change.DeliveryReceipt
                : null;
            if (!IsCurrentDeliveryCommit(change, receipt)) return;
            if (receipt.DeliveredGlass != null)
                StopFeedbackForGlass(receipt.DeliveredGlass.Id);

            BsBoard snapshot = controller.Board;
            if (snapshot == null) return;
            int afterDelivered = snapshot.Delivered;
            int beforeDelivered = Mathf.Max(0, afterDelivered - 1);
            GlassLockPresenter lockPresenter = GetComponent<GlassLockPresenter>();

            for (int i = 0; i < snapshot.Glasses.Count; i++)
            {
                RtGlass glass = snapshot.Glasses[i];
                if (glass == null
                    || !CrossedUnlockThreshold(glass, beforeDelivered, afterDelivered))
                    continue;

                // Use generic lock feedback only when the glass has no dedicated presenter.
                if (lockPresenter != null && lockPresenter.WillAnimateUnlock(
                        glass, beforeDelivered, afterDelivered))
                    continue;

                Queue(receipt.Revision, glass.Id, BeatKind.LockOpened, unlockGold);
            }
        }

        private bool IsCurrentDeliveryCommit(
            BartenderBoardChange change,
            BartenderDeliveryReceipt receipt)
        {
            return controller != null && change != null && receipt != null
                && change.AttemptId.IsValid
                && change.OperationId.IsValid
                && change.DomainRevision > 0L
                && change.BoardRevision >= 0
                && change.Cause == BsRoundTransitionCause.PlayerDelivery
                && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                    change.AttemptId,
                    change.Token,
                    change.DomainRevision,
                    change.BoardRevision)
                && receipt.AttemptId == change.AttemptId
                && receipt.OperationId == change.OperationId
                && receipt.DomainRevision == change.DomainRevision
                && receipt.BoardRevision == change.BoardRevision
                && receipt.Cause == change.Cause
                && receipt.Token == change.Token
                && System.Nullable.Equals(
                    receipt.SettlementReceipt,
                    change.SettlementReceipt);
        }

        private static bool TryGetRevealedTop(BartenderPourReceipt receipt,
                                               out Layer revealed)
        {
            revealed = default;
            RtGlass before = receipt.SourceBefore;
            RtGlass after = receipt.SourceAfter;
            if (before == null || after == null || before.Id != after.Id
                || after.Layers.Count == 0)
                return false;

            int top = after.Layers.Count - 1;
            if (top >= before.Layers.Count) return false;
            Layer hidden = before.Layers[top];
            Layer shown = after.Layers[top];
            if (!hidden.Hidden || shown.Hidden || hidden.Color != shown.Color) return false;

            revealed = shown;
            return true;
        }

        private static bool CrossedUnlockThreshold(RtGlass glass, int beforeDelivered,
                                                   int afterDelivered)
        {
            if (glass.IsChained(beforeDelivered) && !glass.IsChained(afterDelivered))
                return true;
            if (glass.IsChained(beforeDelivered)) return false;

            for (int i = 0; i < glass.Layers.Count; i++)
            {
                Layer layer = glass.Layers[i];
                if (layer.IsLocked(beforeDelivered) && !layer.IsLocked(afterDelivered))
                    return true;
            }
            return false;
        }

        private void Queue(int revision, int glassId, BeatKind kind, Color tint,
                           int unitIndex = -1)
        {
            if (revision < 0 || glassId < 0) return;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingBeat existing = pending[i];
                if (existing.Revision == revision && existing.GlassId == glassId
                    && existing.Kind == kind)
                    return;
            }

            pending.Add(new PendingBeat
            {
                Revision = revision,
                GlassId = glassId,
                Kind = kind,
                Tint = tint,
                UnitIndex = unitIndex
            });
        }

        private void HandlePresentationChanged() => TryFlushPending();

        private void TryFlushPending()
        {
            if (pending.Count == 0 || controller == null || shelfView == null
                || !shelfView.Ready || shelfView.SynchronizationDeferred)
                return;

            int revision = controller.BoardRevision;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingBeat beat = pending[i];
                if (beat.Revision > revision) continue;

                // Wait for the shelf to settle before playing the unlock wobble.
                if (beat.Revision == revision && beat.Kind == BeatKind.LockOpened
                    && shelfView.SeatAnimationPlaying)
                    continue;

                pending.RemoveAt(i);
                // Discard superseded effects so reused bottles cannot flash an old reveal.
                if (beat.Revision != revision) continue;
                if (!shelfView.TryGetBottle(beat.GlassId, out LiquidBottle bottle)
                    || bottle == null || !bottle.gameObject.activeInHierarchy)
                    continue;

                Play(bottle, beat);
            }
        }

        // Play one unlock sound per frame, even when one delivery opens several locks.
        private int lockSoundFrame = -1;

        private void PlayLockOpenSound()
        {
            if (lockSoundFrame == Time.frameCount) return;
            lockSoundFrame = Time.frameCount;
            BsAudio.Instance?.Play(BsSfx.LockOpen, LockOpenVolume);
        }

        private const float LockOpenVolume = 0.8f;

        private void Play(LiquidBottle bottle, PendingBeat beat)
        {
            StopFeedback(bottle);
            BeatKind kind = beat.Kind;
            Color tint = beat.Tint;
            if (kind == BeatKind.HiddenReveal)
            {
                PlayRevealTurbulence(bottle, beat);
                return;
            }

            PlayLockOpenSound();

            SpriteRenderer source = FindVisualSource(bottle);
            if (source == null || source.sprite == null) return;

            SpriteRenderer overlay = GetAuthoredOverlay(bottle);
            if (overlay == null) return;
            ConfigureOverlay(overlay, source, bottle, tint);

            var feedback = new ActiveFeedback
            {
                Bottle = bottle,
                Kind = kind,
                Renderer = overlay,
                BaseLocalPosition = overlay.transform.localPosition,
                BaseLocalRotation = overlay.transform.localRotation,
                BaseLocalScale = overlay.transform.localScale
            };
            active[bottle] = feedback;
            feedback.Routine = StartCoroutine(Animate(feedback, kind, tint));
        }

        private void PlayRevealTurbulence(LiquidBottle bottle, PendingBeat beat)
        {
            var feedback = new ActiveFeedback
            {
                Bottle = bottle,
                Kind = BeatKind.HiddenReveal,
                Shell = bottle.GetComponent<BottleShell>(),
                Revision = beat.Revision,
                ModelVersion = bottle.ModelVersion,
                Renderer = PrepareRevealQuestion(bottle, beat.UnitIndex)
            };
            if (feedback.Renderer != null)
            {
                Transform marker = feedback.Renderer.transform;
                feedback.BaseLocalPosition = marker.localPosition;
                feedback.BaseLocalRotation = marker.localRotation;
                feedback.BaseLocalScale = marker.localScale;
            }
            active[bottle] = feedback;
            bottle.SetRevealTurbulence(0f, 0f);
            bottle.BeginHiddenColorReveal(beat.UnitIndex, shelfView.HiddenLayerColor);
            // PresentationChanged can run after this bottle's LateUpdate. Publish the initial grey frame
            // now so the committed colour never flashes ahead of the outgoing question mark.
            bottle.Refresh();
            feedback.Shell?.Refresh();
            BsAudio.Instance?.Play(BsSfx.HiddenReveal, 0.8f);

            Tween tween = DOVirtual.Float(0f, 1f, Mathf.Max(0.05f, revealDuration),
                    value => ApplyRevealTurbulence(feedback, value))
                .SetEase(Ease.Linear)
                .SetUpdate(true)
                .SetRecyclable(true)
                .SetTarget(bottle);
            feedback.Tween = tween;
            tween.OnComplete(() => FinishFeedback(feedback));
            tween.OnKill(() => HandleTweenKilled(feedback));
        }

        private static SpriteRenderer PrepareRevealQuestion(LiquidBottle bottle, int unitIndex)
        {
            SpriteRenderer overlay = GetAuthoredOverlay(bottle);
            Sprite question = MinimalLockSprites.Question;
            if (overlay == null || question == null
                || !bottle.TryGetUnitVisualBand(unitIndex, out Vector2 center, out _))
                return null;

            // A separate authored slot lets the lock presenter refresh all remaining markers normally.
            VesselPresentationSlots slots =
                bottle.GetComponentInChildren<VesselPresentationSlots>(true);
            SpriteRenderer source = slots != null && slots.LockQuestions != null
                && unitIndex >= 0 && unitIndex < slots.LockQuestions.Length
                ? slots.LockQuestions[unitIndex] : null;
            if (source == null) return null;

            overlay.sharedMaterial = source.sharedMaterial;
            overlay.SetPropertyBlock(null);
            overlay.sprite = question;
            overlay.color = Color.white;
            overlay.sortingLayerID = source.sortingLayerID;
            overlay.sortingOrder = source.sortingOrder + 1;
            overlay.maskInteraction = SpriteMaskInteraction.None;
            overlay.flipX = overlay.flipY = false;
            float height = bottle.profile != null
                ? VesselPresentationMath.RoyalPixelsToLocal(48f, bottle.profile)
                : bottle.InteriorBounds.height * 0.125f;
            float scale = height / Mathf.Max(0.0001f, question.bounds.size.y * 0.8f);
            Transform marker = overlay.transform;
            marker.position = bottle.transform.TransformPoint(new Vector3(center.x, center.y, 0f));
            marker.localRotation = Quaternion.identity;
            marker.localScale = new Vector3(scale, scale, 1f);
            overlay.enabled = true;
            return overlay;
        }

        private void ApplyRevealTurbulence(ActiveFeedback feedback, float progress)
        {
            if (!OwnsFeedback(feedback)) return;

            LiquidBottle bottle = feedback.Bottle;
            if (bottle == null || !bottle.gameObject.activeInHierarchy) return;
            if (controller == null || controller.BoardRevision != feedback.Revision
                || bottle.ModelVersion != feedback.ModelVersion || bottle.IsTransferReserved)
            {
                StopFeedback(bottle);
                return;
            }

            float life = Mathf.Clamp01(progress);
            float fadeIn = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0f, 0.08f, life));
            float fadeOut = 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.55f, 1f, life));
            bottle.SetRevealTurbulence(fadeIn * fadeOut, life);
            bottle.SetHiddenColorRevealProgress(Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.10f, 0.78f, life)));
            feedback.Shell?.Refresh();
            if (feedback.Renderer != null)
            {
                float pop = Mathf.Sin(Mathf.PI * Mathf.Clamp01(life / 0.65f));
                float fade = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(0.20f, 0.70f, life));
                Transform marker = feedback.Renderer.transform;
                marker.localPosition = feedback.BaseLocalPosition
                    + Vector3.up * (revealHop * Mathf.SmoothStep(0f, 1f, life));
                marker.localScale = feedback.BaseLocalScale * (1f + revealScale * 2f * pop);
                marker.localRotation = feedback.BaseLocalRotation
                    * Quaternion.AngleAxis(-10f * pop, Vector3.forward);
                feedback.Renderer.color = new Color(1f, 1f, 1f, fade);
            }
        }

        private IEnumerator Animate(ActiveFeedback feedback, BeatKind kind, Color tint)
        {
            float duration = kind == BeatKind.HiddenReveal
                ? revealDuration
                : unlockDuration;
            float hop = kind == BeatKind.HiddenReveal ? revealHop : unlockHop;
            float scaleAmount = kind == BeatKind.HiddenReveal
                ? revealScale
                : unlockScale;
            float wobble = kind == BeatKind.LockOpened ? unlockWobbleDegrees : 1.5f;
            float cycles = kind == BeatKind.LockOpened ? 4f : 1.25f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                if (feedback.Bottle == null || feedback.Renderer == null
                    || !feedback.Bottle.gameObject.activeInHierarchy)
                    break;

                float t = Mathf.Clamp01(elapsed / duration);
                float fadeIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.16f));
                float fadeOut = 1f - Mathf.SmoothStep(
                    0f, 1f, Mathf.InverseLerp(0.52f, 1f, t));
                float pulse = Mathf.Sin(Mathf.PI * t);
                float angle = Mathf.Sin(t * Mathf.PI * 2f * cycles)
                            * wobble * (1f - t);

                Transform tr = feedback.Renderer.transform;
                tr.localPosition = feedback.BaseLocalPosition
                                 + Vector3.up * (hop * pulse);
                tr.localRotation = feedback.BaseLocalRotation
                                 * Quaternion.AngleAxis(angle, Vector3.forward);
                tr.localScale = feedback.BaseLocalScale * (1f + scaleAmount * pulse);
                feedback.Renderer.color = new Color(
                    tint.r, tint.g, tint.b, tint.a * fadeIn * fadeOut);

                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }

            FinishFeedback(feedback);
        }

        private static SpriteRenderer GetAuthoredOverlay(LiquidBottle bottle)
        {
            VesselPresentationSlots slots =
                bottle.GetComponentInChildren<VesselPresentationSlots>(true);
            return slots != null ? slots.MechanicRevealFeedback : null;
        }

        private void ConfigureOverlay(SpriteRenderer overlay, SpriteRenderer source,
                                      LiquidBottle bottle, Color tint)
        {
            Transform tr = overlay.transform;
            Transform sourceTransform = source.transform;
            if (sourceTransform.parent == bottle.transform)
            {
                tr.localPosition = sourceTransform.localPosition;
                tr.localRotation = sourceTransform.localRotation;
                tr.localScale = sourceTransform.localScale;
            }
            else
            {
                tr.SetPositionAndRotation(sourceTransform.position, sourceTransform.rotation);
                Vector3 parentScale = bottle.transform.lossyScale;
                Vector3 sourceScale = sourceTransform.lossyScale;
                tr.localScale = new Vector3(
                    SafeRatio(sourceScale.x, parentScale.x),
                    SafeRatio(sourceScale.y, parentScale.y),
                    SafeRatio(sourceScale.z, parentScale.z));
            }
            overlay.gameObject.layer = bottle.gameObject.layer;
            overlay.sprite = source.sprite;
            overlay.drawMode = source.drawMode;
            if (source.drawMode != SpriteDrawMode.Simple) overlay.size = source.size;
            overlay.flipX = source.flipX;
            overlay.flipY = source.flipY;
            overlay.spriteSortPoint = source.spriteSortPoint;
            overlay.maskInteraction = source.maskInteraction;
            overlay.sortingLayerID = source.sortingLayerID;
            overlay.sortingOrder = source.sortingOrder + sortingBoost;
            overlay.SetPropertyBlock(null);

            overlay.sharedMaterial = revealOverlayMaterial != null
                ? revealOverlayMaterial
                : source.sharedMaterial;
            overlay.color = new Color(tint.r, tint.g, tint.b, 0f);
            overlay.enabled = true;
        }

        private static SpriteRenderer FindVisualSource(LiquidBottle bottle)
        {
            Transform front = bottle.transform.Find("FrontGlass");
            SpriteRenderer exact = front != null ? front.GetComponent<SpriteRenderer>() : null;
            if (exact != null && exact.sprite != null) return exact;

            SpriteRenderer[] renderers = bottle.GetComponentsInChildren<SpriteRenderer>(true);
            SpriteRenderer best = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                SpriteRenderer candidate = renderers[i];
                if (candidate == null || candidate.sprite == null
                    || candidate.name == FeedbackChildName)
                    continue;
                if (best == null || candidate.sortingOrder > best.sortingOrder)
                    best = candidate;
            }
            return best;
        }

        private static float SafeRatio(float numerator, float denominator) =>
            Mathf.Abs(denominator) > 0.0001f ? numerator / denominator : 1f;

        private void StopFeedbackForGlass(int glassId)
        {
            if (glassId < 0 || shelfView == null
                || !shelfView.TryGetBottle(glassId, out LiquidBottle bottle))
                return;
            StopFeedback(bottle);
        }

        private void StopFeedback(LiquidBottle bottle)
        {
            if (bottle == null || !active.TryGetValue(bottle, out ActiveFeedback feedback))
                return;
            active.Remove(bottle);
            StopOwnedFeedback(feedback);
        }

        private void FinishFeedback(ActiveFeedback feedback)
        {
            if (!OwnsFeedback(feedback)) return;

            active.Remove(feedback.Bottle);
            // Clear the tween reference on completion before DOTween recycles it.
            feedback.Tween = null;
            feedback.Routine = null;
            ResetFeedback(feedback);
        }

        private void HandleTweenKilled(ActiveFeedback feedback)
        {
            if (feedback == null) return;
            feedback.Tween = null;
            if (!OwnsFeedback(feedback)) return;

            active.Remove(feedback.Bottle);
            ResetFeedback(feedback);
        }

        private bool OwnsFeedback(ActiveFeedback feedback) =>
            feedback != null
            && feedback.Bottle != null
            && active.TryGetValue(feedback.Bottle, out ActiveFeedback current)
            && ReferenceEquals(current, feedback);

        private void StopOwnedFeedback(ActiveFeedback feedback)
        {
            if (feedback == null) return;

            Coroutine routine = feedback.Routine;
            feedback.Routine = null;
            Tween tween = feedback.Tween;
            feedback.Tween = null;

            if (routine != null) StopCoroutine(routine);
            if (tween != null && tween.IsActive()) tween.Kill(false);
            ResetFeedback(feedback);
        }

        private void StopAllFeedback()
        {
            if (active.Count == 0) return;

            var snapshot = new ActiveFeedback[active.Count];
            active.Values.CopyTo(snapshot, 0);
            active.Clear();
            for (int i = 0; i < snapshot.Length; i++)
                StopOwnedFeedback(snapshot[i]);
        }

        private static void ResetFeedback(ActiveFeedback feedback)
        {
            if (feedback == null) return;
            if (feedback.Kind == BeatKind.HiddenReveal)
            {
                if (feedback.Bottle != null)
                    feedback.Bottle.ClearRevealTurbulence();
                feedback.Shell?.Refresh();
                ResetOverlay(feedback.Renderer);
                return;
            }

            ResetOverlay(feedback.Renderer);
        }

        private static void ResetOverlay(SpriteRenderer overlay)
        {
            if (overlay == null) return;
            overlay.enabled = false;
            overlay.color = Color.clear;
            overlay.sprite = null;
            overlay.SetPropertyBlock(null);
            Transform tr = overlay.transform;
            tr.localPosition = Vector3.zero;
            tr.localRotation = Quaternion.identity;
            tr.localScale = Vector3.one;
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            pending.Clear();
            StopAllFeedback();
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Unloaded
                && state != BartenderLevelState.CampaignComplete)
                return;
            pending.Clear();
            StopAllFeedback();
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
        }

        private void RebindIfNeeded()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            BartenderLevelController wanted = shelfView != null
                ? shelfView.Controller
                : controller;
            if (ReferenceEquals(wanted, controller)
                && ReferenceEquals(subscribedView, shelfView))
                return;

            Unsubscribe();
            pending.Clear();
            StopAllFeedback();
            controller = wanted;
            Subscribe();
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.Poured -= HandlePoured;
                    subscribedController.BoardCommitted -= HandleBoardCommitted;
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.Poured += HandlePoured;
                    subscribedController.BoardCommitted += HandleBoardCommitted;
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                }
            }

            if (subscribedView == shelfView) return;
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedView = shelfView;
            if (subscribedView != null)
                subscribedView.PresentationChanged += HandlePresentationChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.Poured -= HandlePoured;
                subscribedController.BoardCommitted -= HandleBoardCommitted;
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedController = null;
            subscribedView = null;
        }
    }
}
