using System.Collections.Generic;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace LiquidSort.Levels
{
    public sealed partial class GlassLockPresenter
    {
        private struct LockSegment
        {
            public int Bottom;
            public int Top;
            public int Until;
            public bool HasGeometry;
            public Vector2 Center;
            public float Height;
            public float LockHeight;
        }

        private sealed class LockMotion
        {
            public bool Seen;
            public int Top = -1;
            public int Delivered = -1;
            public Vector3 From;
            public Vector3 Target;
            public float Elapsed = -1f;
            public int DisplayedTop = -1;
            public int DisplayedDelivered = -1;
            public Vector2 DisplayedCenter;
            public float DisplayedHeight;
        }

        private const float LockSettleDuration = 0.24f;

        private sealed class CountBadge
        {
            public Transform Root;
            public TextMeshPro Value;
            public int Shown;
            public int SegmentBottom = -1;
            public int SegmentTop = -1;
            public int SegmentUntil;
            public bool AppearNext;
            public bool HeldByUnlock;
            public ActiveUnlock ExitOwner;
            public float PopElapsed = -1f;
            public bool PopFromZero;
            public float PopStartScale = 1f;
        }

        private const int CountBadgeValueOrder = 15;
        private static readonly Vector2 CountBadgeLockOffset = new Vector2(0f, -0.153f);
        private static readonly Color CountDigitColor = new Color32(14, 28, 53, 255);
        private const float TextMeshProUnitsPerPoint = 0.1f;
        private const float FallbackCapHeightShare = 0.711f;

        private const float CountBadgePopDuration = 0.28f;
        private const float CountBadgePopScale = 1.12f;
        private const float CountBadgeExitEnd = LockUnlockFeedback.CountExitEnd;

        private readonly List<LockSegment> segmentScratch =
            new List<LockSegment>(LiquidBottle.MaxBands);
        private readonly List<LockSegment> unlockSegments =
            new List<LockSegment>(LiquidBottle.MaxBands);
        private readonly List<CountBadge> poppingBadges = new List<CountBadge>(4);
        private bool countBadgeReleased;

        internal static int PresentedLockUntil(RtGlass glass, int layerIndex, int delivered)
        {
            if (glass == null || layerIndex < 0 || layerIndex >= glass.Layers.Count) return 0;
            int until = glass.Layers[layerIndex].LockUntil;
            if (until <= 0) return 0;
            for (int i = layerIndex + 1; i < glass.Layers.Count; i++)
            {
                Layer above = glass.Layers[i];
                if (above.IsLocked(delivered)) until = Mathf.Max(until, above.LockUntil);
            }
            return delivered < until ? until : 0;
        }

        internal static bool IsLayerPresentedLocked(RtGlass glass, int layerIndex, int delivered) =>
            PresentedLockUntil(glass, layerIndex, delivered) > 0;

        internal static bool OpensPresentedLock(RtGlass glass, int beforeDelivered,
                                                int afterDelivered)
        {
            if (glass == null) return false;
            for (int i = 0; i < glass.Layers.Count; i++)
                if (IsLayerPresentedLocked(glass, i, beforeDelivered)
                    && !IsLayerPresentedLocked(glass, i, afterDelivered))
                    return true;
            return false;
        }

        private static void CollectLockSegments(RtGlass glass, int delivered,
                                                List<LockSegment> segments)
        {
            segments.Clear();
            for (int i = 0; glass != null && i < glass.Layers.Count; i++)
            {
                int until = PresentedLockUntil(glass, i, delivered);
                if (until <= 0) continue;

                int last = segments.Count - 1;
                if (last >= 0 && segments[last].Top == i - 1)
                {
                    LockSegment grown = segments[last];
                    grown.Top = i;
                    grown.Until = Mathf.Min(grown.Until, until);
                    segments[last] = grown;
                }
                else
                {
                    segments.Add(new LockSegment { Bottom = i, Top = i, Until = until });
                }
            }
        }

        private static bool SegmentFullyOpens(RtGlass glass, LockSegment segment, int delivered)
        {
            for (int i = segment.Bottom; i <= segment.Top; i++)
                if (IsLayerPresentedLocked(glass, i, delivered)) return false;
            return true;
        }

        private static void PlaceGroupedLock(BottleVisuals set, LockSegment segment, int delivered)
        {
            SpriteRenderer marker = GetAuthoredQuestion(set, segment.Bottom);
            LockMotion motion = set.LockMotions[segment.Bottom];
            Vector3 previous = marker.transform.localPosition;
            bool shrink = motion.Top > segment.Top && delivered > motion.Delivered;
            if (!PlaceSprite(marker, MinimalLockSprites.ClosedLock, segment.Center,
                             segment.LockHeight, WholeLockVisibleHeightShare)) return;
            motion.Seen = true;
            motion.Top = segment.Top;
            motion.Delivered = delivered;
            motion.Target = marker.transform.localPosition;
            if (shrink)
            {
                motion.From = previous;
                motion.Elapsed = 0f;
            }
            if (motion.Elapsed >= 0f)
                marker.transform.localPosition = LockMotionPosition(motion);
            motion.DisplayedTop = segment.Top;
            motion.DisplayedDelivered = delivered;
            motion.DisplayedCenter = marker.transform.localPosition;
            motion.DisplayedHeight = segment.LockHeight;
        }

        private static LockSegment PresentedUnlockSegment(BottleVisuals set, LockSegment segment,
                                                           int beforeDelivered)
        {
            if (segment.Bottom < 0 || segment.Bottom >= set.LockMotions.Count) return segment;
            LockMotion motion = set.LockMotions[segment.Bottom];
            if (motion.DisplayedTop != segment.Top || motion.DisplayedDelivered != beforeDelivered
                || motion.DisplayedHeight <= 0f) return segment;
            segment.Center = motion.DisplayedCenter;
            segment.LockHeight = motion.DisplayedHeight;
            return segment;
        }

        private static Vector3 LockMotionPosition(LockMotion motion)
        {
            float t = Mathf.Clamp01(motion.Elapsed / LockSettleDuration);
            return Vector3.LerpUnclamped(motion.From, motion.Target, 1f - Mathf.Pow(1f - t, 3f));
        }

        private static void ResetLockMotion(LockMotion motion)
        {
            motion.Seen = false;
            motion.Top = -1;
            motion.Delivered = -1;
            motion.Elapsed = -1f;
        }

        private static void ResetLockMotions(BottleVisuals set)
        {
            foreach (LockMotion motion in set.LockMotions)
            {
                ResetLockMotion(motion);
                // Hiding a removed slot keeps its pose for that delivery's cue; hiding/rebinding the
                // vessel forgets it so a replay or another glass cannot inherit the old location.
                motion.DisplayedTop = -1;
                motion.DisplayedDelivered = -1;
                motion.DisplayedHeight = 0f;
            }
        }

        private void TickLockMotions()
        {
            foreach (BottleVisuals set in visuals.Values)
            {
                for (int i = 0; i < set.LockMotions.Count; i++)
                {
                    LockMotion motion = set.LockMotions[i];
                    if (motion.Elapsed < 0f) continue;
                    SpriteRenderer marker = GetAuthoredQuestion(set, i);
                    if (marker == null || !marker.enabled || set.Bottle == null
                        || !set.Bottle.gameObject.activeInHierarchy)
                    {
                        ResetLockMotion(motion);
                        continue;
                    }
                    motion.Elapsed = set.Bottle.IsTransferReserved ? LockSettleDuration
                        : motion.Elapsed + Mathf.Max(0f, Time.unscaledDeltaTime);
                    Vector3 previousWorld = marker.transform.position;
                    marker.transform.localPosition = LockMotionPosition(motion);
                    motion.DisplayedCenter = marker.transform.localPosition;
                    CountBadge badge = set.Badge;
                    if (badge != null && badge.Root != null && badge.Shown > 0
                        && !badge.HeldByUnlock && badge.SegmentBottom == i)
                        badge.Root.position += marker.transform.position - previousWorld;
                    if (motion.Elapsed >= LockSettleDuration) motion.Elapsed = -1f;
                }
            }
        }

        private static void MeasureLockSegments(LiquidBottle bottle, RtGlass glass, int delivered,
                                                List<LockSegment> segments)
        {
            for (int s = 0; s < segments.Count; s++)
            {
                LockSegment segment = segments[s];
                float lower = float.MaxValue;
                float upper = float.MinValue;
                for (int i = segment.Bottom; i <= segment.Top; i++)
                {
                    if (!bottle.TryGetUnitVisualBand(i, out Vector2 center, out float bandHeight))
                        continue;
                    lower = Mathf.Min(lower, center.y - bandHeight * 0.5f);
                    upper = Mathf.Max(upper, center.y + bandHeight * 0.5f);
                }
                segment.HasGeometry = upper > lower;
                if (segment.HasGeometry)
                {
                    float middle = (lower + upper) * 0.5f;
                    segment.Height = upper - lower;
                    segment.Center = new Vector2(SegmentCenterX(bottle, segment, middle), middle);
                }
                segments[s] = segment;
            }

            float wantedHeight = bottle.profile != null
                ? VesselPresentationMath.RoyalPixelsToLocal(LayerLockRoyalHeightPixels, bottle.profile)
                : ArtHeight(bottle) * UnprofiledQuestionHeightShare;
            for (int s = 0; s < segments.Count; s++)
            {
                if (!segments[s].HasGeometry) continue;
                LockSegment segment = segments[s];
                segment.LockHeight = FitLayerLockBesideMarkers(
                    bottle, glass, delivered, segments, s, wantedHeight);
                segments[s] = segment;
            }
        }

        private static float SegmentCenterX(LiquidBottle bottle, LockSegment segment, float y)
        {
            float x = 0f;
            float nearest = float.MaxValue;
            bool hasPrevious = false;
            Vector2 previous = default;
            for (int i = segment.Bottom; i <= segment.Top; i++)
            {
                if (!bottle.TryGetUnitVisualBand(i, out Vector2 center, out _)) continue;
                if (hasPrevious && (y - previous.y) * (y - center.y) <= 0f)
                    return Mathf.Lerp(previous.x, center.x,
                                      Mathf.InverseLerp(previous.y, center.y, y));
                float distance = Mathf.Abs(center.y - y);
                if (distance < nearest)
                {
                    nearest = distance;
                    x = center.x;
                }
                previous = center;
                hasPrevious = true;
            }
            return x;
        }

        private void RefreshCountBadge(BottleVisuals set, RtGlass glass, int delivered,
                                       List<LockSegment> segments)
        {
            CountBadge badge = set.Badge;
            if (badge == null || badge.HeldByUnlock || HasPendingUnlock(glass.Id)) return;

            int top = segments.Count - 1;
            int remaining = top >= 0 ? segments[top].Until - delivered : 0;
            if (remaining <= 0 || !segments[top].HasGeometry)
            {
                HideCountBadge(badge);
                return;
            }

            LockSegment segment = segments[top];
            bool appear = badge.Shown <= 0
                ? badge.AppearNext
                : badge.SegmentBottom != segment.Bottom;
            bool countdown = !appear && badge.Shown > 0 && remaining < badge.Shown;
            bool nextStage = !appear && badge.Shown > 0
                && (badge.SegmentTop != segment.Top || badge.SegmentUntil != segment.Until);
            if (!PlaceCountBadge(set, segment, remaining))
            {
                HideCountBadge(badge);
                return;
            }
            badge.AppearNext = false;
            if (appear || countdown || nextStage) StartCountBadgePop(badge, appear);
        }

        private bool HasPendingUnlock(int glassId)
        {
            for (int i = 0; i < pendingUnlocks.Count; i++)
            {
                PendingUnlock pending = pendingUnlocks[i];
                if (pending.Glass.Id == glassId)
                    return true;
            }
            return false;
        }

        private void RefreshWholeCount(BottleVisuals set, RtGlass glass, int delivered)
        {
            CountBadge badge = set.Badge;
            if (badge == null || badge.HeldByUnlock || HasPendingUnlock(glass.Id)) return;
            GetWholeLockLayout(set.Bottle, out Vector2 center, out float height);
            bool countdown = badge.Shown > glass.ChainRemaining(delivered);
            var segment = new LockSegment { Bottom = -1, Top = -1, Until = glass.UnlockAfter,
                Center = center, LockHeight = height, HasGeometry = true };
            if (PlaceCountBadge(set, segment, glass.ChainRemaining(delivered), false) && countdown)
                StartCountBadgePop(badge, false);
        }

        private static bool PlaceCountBadge(BottleVisuals set, LockSegment segment, int remaining,
                                            bool followSettlingLock = true)
        {
            CountBadge badge = set.Badge;
            if (badge == null || badge.Root == null || badge.Value == null
                || remaining <= 0 || segment.LockHeight <= 0.0001f) return false;
            Vector2 center = segment.Center + CountBadgeLockOffset * segment.LockHeight;
            Transform root = badge.Root;
            root.localPosition = new Vector3(center.x, center.y, 0f);
            if (followSettlingLock && segment.Bottom >= 0 && segment.Bottom < set.LockMotions.Count)
            {
                LockMotion motion = set.LockMotions[segment.Bottom];
                SpriteRenderer marker = GetAuthoredQuestion(set, segment.Bottom);
                if (motion.Top == segment.Top && motion.Elapsed >= 0f && marker != null)
                    root.position += marker.transform.parent.TransformVector(marker.transform.localPosition-motion.Target);
            }
            root.localRotation = Quaternion.identity;
            if (badge.PopElapsed < 0f) root.localScale = Vector3.one;
            TextMeshPro value = badge.Value;
            float capShare = FallbackCapHeightShare;
            if (value.font != null)
            {
                FaceInfo face = value.font.faceInfo;
                if (face.pointSize > 0f && face.capLine > 0f)
                    capShare = face.capLine / face.pointSize * face.scale;
            }
            float digitHeight = segment.LockHeight * 0.34f;
            value.fontSize = digitHeight / Mathf.Max(0.0001f, capShare * TextMeshProUnitsPerPoint);
            value.rectTransform.sizeDelta = new Vector2(segment.LockHeight * 0.55f, segment.LockHeight * 0.43f);
            ScaleCountBadgePart(value.transform, 1f);
            if (badge.Shown != remaining) value.text = remaining.ToString();
            value.ForceMeshUpdate();
            Bounds bounds = value.textBounds;
            float fit = Mathf.Min(1f, (segment.LockHeight * 0.51f / CountBadgePopScale)
                                      / Mathf.Max(0.0001f,bounds.size.x));
            value.fontSize *= fit;
            badge.Shown = remaining;
            badge.SegmentBottom = segment.Bottom;
            badge.SegmentTop = segment.Top;
            badge.SegmentUntil = segment.Until;
            SetCountBadgeAlpha(badge,1f);
            SetCountBadgeVisible(badge,true);
            return true;
        }

        private static CountBadge CreateCountBadge(LiquidBottle bottle, Transform parent,
                                                   int sortingLayerId)
        {
            Material valueMaterial = LockCountBadgeAssets.ValueMaterial;
            TMP_FontAsset valueFont = LockCountBadgeAssets.ValueFont;
            if (bottle == null || parent == null || valueMaterial == null || valueFont == null)
                return null;

            int layer = bottle.gameObject.layer;
            var root = new GameObject(MarkerPrefix + "CountBadge") { layer = layer };
            root.transform.SetParent(parent, false);
            var badge = new CountBadge { Root = root.transform };

            var valueObject = new GameObject(MarkerPrefix + "CountBadge_Value",
                                             typeof(RectTransform)) { layer = layer };
            valueObject.transform.SetParent(root.transform, false);
            TextMeshPro value = valueObject.AddComponent<TextMeshPro>();
            value.font = valueFont;
            value.fontSharedMaterial = valueMaterial;
            value.fontStyle = FontStyles.Normal;
            value.alignment = TextAlignmentOptions.MidlineGeoAligned;
            value.textWrappingMode = TextWrappingModes.NoWrap;
            value.overflowMode = TextOverflowModes.Overflow;
            value.enableAutoSizing = false;
            value.richText = false;
            value.color = CountDigitColor;
            value.text = string.Empty;
            value.sortingLayerID = sortingLayerId;
            value.sortingOrder = CountBadgeValueOrder;
            badge.Value = value;
            HideCountBadge(badge);
            return badge;
        }

        private static void ScaleCountBadgePart(Transform part, float scale)
        {
            part.localPosition = Vector3.zero;
            part.localRotation = Quaternion.identity;
            part.localScale = new Vector3(scale, scale, 1f);
        }

        private static void HideCountBadge(CountBadge badge)
        {
            if (badge == null) return;
            badge.Shown = 0;
            badge.SegmentBottom = -1;
            badge.SegmentTop = -1;
            badge.SegmentUntil = 0;
            badge.AppearNext = false;
            badge.HeldByUnlock = false;
            if (badge.ExitOwner != null) badge.ExitOwner.BadgeVisuals = null;
            badge.ExitOwner = null;
            badge.PopElapsed = -1f;
            SetCountBadgeVisible(badge, false);
            if (badge.Root == null) return;
            badge.Root.localRotation = Quaternion.identity;
            badge.Root.localScale = Vector3.one;
        }

        private static void SetCountBadgeVisible(CountBadge badge, bool visible)
        {
            if (badge.Value != null && badge.Value.renderer != null)
                badge.Value.renderer.enabled = visible;
        }

        private static void SetCountBadgeAlpha(CountBadge badge, float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            if (badge.Value != null) badge.Value.alpha = alpha;
        }

        private void StartCountBadgePop(CountBadge badge, bool appear)
        {
            if (badge.Root == null) return;
            badge.PopStartScale = Mathf.Clamp(badge.Root.localScale.x, 0f, CountBadgePopScale);
            badge.PopElapsed = 0f;
            badge.PopFromZero = appear;
            badge.Root.localScale = Vector3.one * CountBadgePopScaleAt(0f, appear, badge.PopStartScale);
            if (!poppingBadges.Contains(badge)) poppingBadges.Add(badge);
        }

        private void TickCountBadges()
        {
            for (int i = poppingBadges.Count - 1; i >= 0; i--)
            {
                CountBadge badge = poppingBadges[i];
                if (badge == null || badge.Root == null || badge.HeldByUnlock
                    || badge.PopElapsed < 0f)
                {
                    poppingBadges.RemoveAt(i);
                    continue;
                }

                badge.PopElapsed += Mathf.Max(0f, Time.unscaledDeltaTime);
                float progress = Mathf.Clamp01(badge.PopElapsed / CountBadgePopDuration);
                badge.Root.localScale = Vector3.one * CountBadgePopScaleAt(progress, badge.PopFromZero, badge.PopStartScale);
                if (progress < 1f) continue;
                badge.PopElapsed = -1f;
                poppingBadges.RemoveAt(i);
            }
        }

        private static float CountBadgePopScaleAt(float progress, bool appear, float startScale = 1f)
        {
            if (appear) return EaseOutBack(progress);
            const float swellShare = 0.24f;
            if (progress < swellShare)
            {
                float rise = 1f - progress / swellShare;
                return Mathf.Lerp(startScale, CountBadgePopScale, 1f - rise * rise);
            }
            return Mathf.LerpUnclamped(CountBadgePopScale, 1f,
                EaseOutBack((progress - swellShare) / (1f - swellShare)));
        }

        private static float CountBadgeExitScaleAt(float leave)
        {
            const float swellShare = 0.28f;
            if (leave < swellShare)
            {
                float rise = 1f - leave / swellShare;
                return Mathf.Lerp(1f, 1.07f, 1f - rise * rise);
            }
            float fall = (leave - swellShare) / (1f - swellShare);
            return Mathf.Lerp(1.07f, 1f, Mathf.SmoothStep(0f, 1f, fall));
        }

        private static float EaseOutBack(float t)
        {
            const float overshoot = 1.70158f;
            float x = Mathf.Clamp01(t) - 1f;
            return 1f + (overshoot + 1f) * x * x * x + overshoot * x * x;
        }

        private void AttachCountBadgeExit(ActiveUnlock active, BottleVisuals set,
                                          LockSegment segment, int beforeDelivered)
        {
            CountBadge badge = set.Badge;
            Transform lockRoot = active.Effect != null ? active.Effect.Root : null;
            if (badge == null || lockRoot == null) return;

            // Deferred deliveries can reach this point newest first. Never let an older cue take the
            // current number back, or let its cleanup hide a number already owned by a later release.
            if (badge.ExitOwner != null
                && badge.ExitOwner.Pending.AfterDelivered >= active.Pending.AfterDelivered) return;
            if (badge.ExitOwner != null) badge.ExitOwner.BadgeVisuals = null;
            badge.ExitOwner = null;

            badge.PopElapsed = -1f;
            if (!PlaceCountBadge(set, segment, segment.Until - beforeDelivered,
                                 followSettlingLock: false))
            {
                HideCountBadge(badge);
                return;
            }
            badge.HeldByUnlock = true;
            badge.ExitOwner = active;
            active.BadgeVisuals = set;
            active.BadgeLocalOffset = lockRoot.InverseTransformPoint(badge.Root.position);
            active.BadgeLockStartScale = Mathf.Max(0.0001f, Mathf.Abs(lockRoot.lossyScale.y));
        }

        private void AdvanceCountBadgeExit(ActiveUnlock active)
        {
            CountBadge badge = active.BadgeVisuals.Badge;
            Transform lockRoot = active.Effect.Root;
            if (badge == null || badge.Root == null || !badge.HeldByUnlock || lockRoot == null
                || !ReferenceEquals(badge.ExitOwner, active))
            {
                active.BadgeVisuals = null;
                return;
            }

            float leave = Mathf.InverseLerp(LockUnlockFeedback.GrowDuration, CountBadgeExitEnd,
                                            active.Elapsed);
            if (leave >= 1f)
            {
                ReleaseCountBadge(active);
                return;
            }
            badge.Root.SetPositionAndRotation(
                lockRoot.TransformPoint(active.BadgeLocalOffset), lockRoot.rotation);
            badge.Root.localScale = Vector3.one * CountBadgeExitScaleAt(leave)
                * (Mathf.Abs(lockRoot.lossyScale.y) / active.BadgeLockStartScale);
            // Never detach the number from a still-visible silver face. The feedback owns the
            // complete fade, while this root continues to follow its growth and recoil.
            SetCountBadgeAlpha(badge, active.Effect.Opacity);
        }

        private void ReleaseCountBadge(ActiveUnlock active)
        {
            CountBadge badge = active.BadgeVisuals != null ? active.BadgeVisuals.Badge : null;
            active.BadgeVisuals = null;
            if (badge == null || !badge.HeldByUnlock || !ReferenceEquals(badge.ExitOwner, active)) return;
            HideCountBadge(badge);
            badge.AppearNext = true;
            countBadgeReleased = true;
        }
    }

    internal static class LockCountBadgeAssets
    {
        private const string MaterialPath = "Ui/Locks/LockCountBadge_SDF";
        private const string FontCardPath = "Ui/DailyOrders/DailyOrderCard";
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private static Material valueMaterial;
        private static TMP_FontAsset valueFont;

        public static Material ValueMaterial =>
            valueMaterial != null
                ? valueMaterial
                : valueMaterial = Resources.Load<Material>(MaterialPath);

        public static TMP_FontAsset ValueFont
        {
            get
            {
                if (valueFont != null) return valueFont;
                Material material = ValueMaterial;
                Texture atlas = material != null && material.HasProperty(MainTexId)
                    ? material.GetTexture(MainTexId)
                    : null;
                if (atlas == null) return null;

                TMP_FontAsset[] loaded = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                for (int i = 0; i < loaded.Length; i++)
                    if (OwnsAtlas(loaded[i], atlas)) return valueFont = loaded[i];

                // Gameplay UI normally has the font loaded; the daily-order card always references it.
                DailyOrderCardView card = Resources.Load<DailyOrderCardView>(FontCardPath);
                TMP_Text label = card != null ? card.GetComponentInChildren<TMP_Text>(true) : null;
                if (label != null && OwnsAtlas(label.font, atlas)) valueFont = label.font;
                return valueFont;
            }
        }

        private static bool OwnsAtlas(TMP_FontAsset candidate, Texture atlas)
        {
            Texture2D[] atlases = candidate != null ? candidate.atlasTextures : null;
            return atlases != null && atlases.Length > 0 && atlases[0] == atlas;
        }
    }
}
