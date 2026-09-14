using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    public sealed partial class GlassLockPresenter
    {
        private sealed class PendingUnlock
        {
            public BsAttemptId AttemptId;
            public BsRoundToken Token;
            public RtGlass Glass;
            public int BeforeDelivered;
            public int AfterDelivered;
        }

        private sealed class ActiveUnlock
        {
            public PendingUnlock Pending;
            public LiquidBottle Bottle;
            public LockUnlockFeedback Effect;
            public BottleVisuals DimVisuals;
            public BottleVisuals BadgeVisuals;
            public Vector3 BadgeLocalOffset;
            public float BadgeLockStartScale;
            public bool Started;
            public bool SoundPlayed;
            public float HoldElapsed;
            public float Elapsed;
        }

        private readonly List<PendingUnlock> pendingUnlocks = new List<PendingUnlock>(8);
        private readonly List<ActiveUnlock> activeUnlocks = new List<ActiveUnlock>(8);
        private long lastUnlockRevision = -1;
        private int unlockSoundFrame = -1;

        private void QueueUnlockFeedback(BartenderBoardChange change)
        {
            BartenderDeliveryReceipt receipt = change?.DeliveryReceipt;
            if (controller == null || change == null || receipt == null
                || !change.AttemptId.IsValid || !change.OperationId.IsValid
                || change.DomainRevision <= 0L || change.BoardRevision < 0
                || change.Cause != BsRoundTransitionCause.PlayerDelivery
                || controller.CurrentRoundStamp != new BsRoundCommandStamp(
                    change.AttemptId, change.Token, change.DomainRevision, change.BoardRevision)
                || receipt.AttemptId != change.AttemptId
                || receipt.OperationId != change.OperationId
                || receipt.Token != change.Token
                || receipt.DomainRevision != change.DomainRevision
                || receipt.BoardRevision != change.BoardRevision
                || receipt.Cause != change.Cause
                || !System.Nullable.Equals(receipt.SettlementReceipt, change.SettlementReceipt)
                || lastUnlockRevision == change.DomainRevision)
                return;

            BsBoard board = controller.Board;
            if (board == null) return;
            lastUnlockRevision = change.DomainRevision;
            int after = board.Delivered;
            int before = Mathf.Max(0, after - 1);
            for (int i = 0; i < board.Glasses.Count; i++)
            {
                RtGlass glass = board.Glasses[i];
                if (!WillAnimateUnlock(glass, before, after)) continue;
                pendingUnlocks.Add(new PendingUnlock
                {
                    AttemptId = change.AttemptId,
                    Token = change.Token,
                    Glass = glass.Clone(),
                    BeforeDelivered = before,
                    AfterDelivered = after
                });
            }
        }

        // Cosmetic only: no presentation lock, synchronization deferral, or completion callback is acquired.
        // Run after the shelf's update so every copy starts at the final seat, independent of event order.
        private void TickUnlockFeedback()
        {
            if (activeUnlocks.Count == 0 && pendingUnlocks.Count == 0) return;
            if (controller == null || shelfView == null || !shelfView.Ready)
            {
                ClearUnlockFeedback();
                return;
            }

            BsBoard board = controller.Board;
            BsRoundCommandStamp stamp = controller.CurrentRoundStamp;
            bool releasedDim = false;
            for (int i = activeUnlocks.Count - 1; i >= 0; i--)
            {
                ActiveUnlock active = activeUnlocks[i];
                if (!UnlockStillCurrent(active.Pending, board, stamp)
                    || active.Bottle == null || !active.Bottle.gameObject.activeInHierarchy
                    || active.Bottle.IsTransferReserved || shelfView.BlockingSeatRunPlaying
                    || shelfView.IsSeatMoving(active.Pending.Glass.Id)
                    || shelfView.GlobalSynchronizationDeferred
                    || shelfView.IsGlassSynchronizationDeferred(active.Pending.Glass.Id)
                    || !shelfView.TryGetBottle(active.Pending.Glass.Id, out LiquidBottle current)
                    || current != active.Bottle
                    || !AdvanceUnlock(active))
                {
                    releasedDim |= active.DimVisuals != null;
                    DisposeUnlock(active);
                    activeUnlocks.RemoveAt(i);
                }
            }

            for (int i = pendingUnlocks.Count - 1; i >= 0; i--)
            {
                PendingUnlock pending = pendingUnlocks[i];
                if (!UnlockStillCurrent(pending, board, stamp))
                {
                    pendingUnlocks.RemoveAt(i);
                    countBadgeReleased = true;
                    continue;
                }
                if (shelfView.BlockingSeatRunPlaying || shelfView.IsSeatMoving(pending.Glass.Id)
                    || shelfView.GlobalSynchronizationDeferred
                    || shelfView.IsGlassSynchronizationDeferred(pending.Glass.Id))
                    continue;
                if (!shelfView.TryGetBottle(pending.Glass.Id, out LiquidBottle bottle)
                    || bottle == null || !bottle.gameObject.activeInHierarchy)
                {
                    pendingUnlocks.RemoveAt(i);
                    countBadgeReleased = true;
                    continue;
                }
                if (bottle.IsTransferReserved) continue;

                pendingUnlocks.RemoveAt(i);
                countBadgeReleased = true;
                BottleVisuals set = GetAuthoredVisuals(bottle);
                if (set == null) continue;
                if (pending.Glass.IsChained(pending.BeforeDelivered))
                {
                    PlayUnlock(set, pending, -1);
                    continue;
                }
                CollectLockSegments(pending.Glass, pending.BeforeDelivered, unlockSegments);
                MeasureLockSegments(bottle, pending.Glass, pending.BeforeDelivered, unlockSegments);
                for (int segmentIndex = 0; segmentIndex < unlockSegments.Count; segmentIndex++)
                    if (SegmentFullyOpens(pending.Glass, unlockSegments[segmentIndex],
                                          pending.AfterDelivered))
                        PlayUnlock(set, pending, segmentIndex);
            }
            if (releasedDim || countBadgeReleased)
            {
                countBadgeReleased = false;
                RequestRefresh();
            }
        }

        private bool AdvanceUnlock(ActiveUnlock active)
        {
            if (!active.Started)
            {
                // Keep a closed copy in place while a modal state holds the round. The game never waits for this
                // effect, and a save in flight neither delays nor drops it.
                if (controller.ModalInputBlocked || controller.State != BartenderLevelState.Playing)
                {
                    active.HoldElapsed += Time.unscaledDeltaTime;
                    return active.HoldElapsed < 2f;
                }
                StartUnlock(active);
            }
            if (!active.Effect.Tick(Time.unscaledDeltaTime)) return false;
            active.Elapsed += Mathf.Max(0f, Time.unscaledDeltaTime);
            if (active.BadgeVisuals != null) AdvanceCountBadgeExit(active);
            if (active.DimVisuals != null)
                ApplyUnlockDim(active.DimVisuals, active.Effect.DimOpacity);
            if (!active.SoundPlayed && active.Effect.OpeningStarted)
            {
                active.SoundPlayed = true;
                PlayUnlockSound();
            }
            return true;
        }

        private static bool UnlockStillCurrent(PendingUnlock pending, BsBoard board,
                                                BsRoundCommandStamp stamp)
        {
            if (board == null || pending.AttemptId != stamp.AttemptId
                || pending.Token != stamp.Token || board.Delivered < pending.AfterDelivered)
                return false;
            RtGlass expected = pending.Glass;
            RtGlass current = null;
            for (int i = 0; i < board.Glasses.Count; i++)
                if (board.Glasses[i] != null && board.Glasses[i].Id == expected.Id)
                {
                    current = board.Glasses[i];
                    break;
                }
            if (current == null || current.Type != expected.Type
                || current.UnlockAfter != expected.UnlockAfter
                || current.Layers.Count != expected.Layers.Count)
                return false;
            for (int i = 0; i < current.Layers.Count; i++)
                if (!current.Layers[i].Equals(expected.Layers[i])) return false;
            return true;
        }

        private void PlayUnlock(BottleVisuals set, PendingUnlock pending, int segmentIndex)
        {
            Sprite sprite = MinimalLockSprites.ClosedLock;
            if (sprite == null || sprite.bounds.size.y <= 0.0001f) return;
            bool wholeGlass = segmentIndex < 0;
            int topLayer = -1;
            SpriteRenderer source;
            Material material;
            Vector2 center;
            float height;
            LockSegment openingSegment = default;
            if (wholeGlass)
            {
                source = set.WholeLock;
                material = source != null ? source.sharedMaterial : null;
                GetWholeLockLayout(set.Bottle, out center, out height);
                openingSegment = new LockSegment { Bottom=-1, Top=-1, Until=pending.Glass.UnlockAfter,
                    Center=center, LockHeight=height, HasGeometry=true };
            }
            else
            {
                LockSegment segment = PresentedUnlockSegment(
                    set, unlockSegments[segmentIndex], pending.BeforeDelivered);
                openingSegment = segment;
                source = GetAuthoredQuestion(set, segment.Bottom);
                if (source == null || !segment.HasGeometry) return;
                center = segment.Center;
                height = segment.LockHeight;
                topLayer = segment.Top;
                material = segmentIndex == unlockSegments.Count - 1
                    ? MinimalLockSprites.NumberedLockMaterial : MinimalLockSprites.LayerLockMaterial;
            }
            if (source == null || height <= 0.0001f) return;

            float scale = height / (sprite.bounds.size.y * WholeLockVisibleHeightShare);
            Camera viewCamera = Camera.main;
            // A thin-layer marker gets a little emphasis, but the feedback must still belong to the
            // liquid band rather than turn into a large floating UI element.
            float minimumVisibleWorldHeight = viewCamera != null && viewCamera.orthographic
                ? viewCamera.orthographicSize * 2f * 0.02f : 0f;
            LockUnlockFeedback effect = LockUnlockFeedback.Create(
                source, transform, sprite, center, scale, material, minimumVisibleWorldHeight,
                wholeGlass: wholeGlass);
            if (effect == null) return;
            if (ShouldHighlightUnlockedGlass(pending.Glass, pending.BeforeDelivered,
                                             pending.AfterDelivered, topLayer))
            {
                Transform front = set.Bottle.transform.Find("FrontGlass");
                effect.AttachGlassSheen(front != null ? front.GetComponent<SpriteRenderer>() : null);
            }
            var active = new ActiveUnlock
            {
                Pending = pending, Bottle = set.Bottle, Effect = effect,
                DimVisuals = wholeGlass ? set : null
            };
            activeUnlocks.Add(active);
            shelfView.TryAcquireSeatLease(active, pending.Glass.Id, false);
            if (wholeGlass || segmentIndex == unlockSegments.Count - 1)
                AttachCountBadgeExit(active, set, openingSegment, pending.BeforeDelivered);
            if (active.DimVisuals != null) ApplyUnlockDim(set, 1f);
            if (!controller.ModalInputBlocked && controller.State == BartenderLevelState.Playing)
                StartUnlock(active);
        }

        // The full outline means this vessel has just become usable. A buried layer still gets its
        // own opening lock, but must not imply a blocked top can now pour. Several unlocked layers
        // therefore create at most one glass highlight, owned by the top layer's existing cue.
        internal static bool ShouldHighlightUnlockedGlass(RtGlass glass, int beforeDelivered,
                                                          int afterDelivered, int layerIndex)
        {
            if (glass == null || afterDelivered <= beforeDelivered
                || glass.IsChained(afterDelivered))
                return false;

            if (layerIndex < 0)
                return glass.IsChained(beforeDelivered)
                    && (glass.Free > 0 || glass.TopChainLength(afterDelivered) > 0);

            return !glass.IsChained(beforeDelivered)
                && layerIndex == glass.Layers.Count - 1
                && glass.TopChainLength(beforeDelivered) == 0
                && glass.TopChainLength(afterDelivered) > 0;
        }

        private void StartUnlock(ActiveUnlock active)
        {
            active.Started = true;
        }

        private void PlayUnlockSound()
        {
            if (unlockSoundFrame == Time.frameCount) return;
            unlockSoundFrame = Time.frameCount;
            BsAudio.Instance?.Play(BsSfx.LockOpen, 0.8f);
        }

        private void ClearUnlockFeedback()
        {
            for (int i = 0; i < activeUnlocks.Count; i++) DisposeUnlock(activeUnlocks[i]);
            activeUnlocks.Clear();
            pendingUnlocks.Clear();
            lastUnlockRevision = -1;
            unlockSoundFrame = -1;
            countBadgeReleased = false;
        }

        private bool RefreshUnlockDim(BottleVisuals set, int glassId)
        {
            for (int i = 0; i < activeUnlocks.Count; i++)
            {
                ActiveUnlock active = activeUnlocks[i];
                if (active.DimVisuals != set || active.Pending.Glass.Id != glassId) continue;
                ApplyUnlockDim(set, active.Effect.DimOpacity);
                return true;
            }
            return false;
        }

        private static void ApplyUnlockDim(BottleVisuals set, float opacity)
        {
            if (opacity <= 0f)
            {
                HideWholeGlassDim(set);
                return;
            }
            RefreshWholeGlassDim(set);
            if (set.WholeGlassDim != null)
            {
                Color color = WholeGlassDimColor;
                color.a *= opacity;
                set.WholeGlassDim.color = color;
            }
            if (set.WholeInteriorDim != null)
            {
                Color color = WholeInteriorDimColor;
                color.a *= opacity;
                set.WholeInteriorDim.color = color;
            }
        }

        private void DisposeUnlock(ActiveUnlock active)
        {
            try
            {
                active.Effect.Dispose();
                if (active.DimVisuals != null) HideWholeGlassDim(active.DimVisuals);
                if (active.BadgeVisuals != null) ReleaseCountBadge(active);
            }
            finally
            {
                if (shelfView != null) shelfView.ReleaseSeatLease(active);
            }
        }
    }
}
