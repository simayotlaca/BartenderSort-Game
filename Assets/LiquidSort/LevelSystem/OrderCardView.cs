using System;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class OrderCardView : MonoBehaviour
    {
        public const int MaxUnits = 5;

        private static readonly Color TimeBoostWarmColor =
            new Color32(0xFF, 0xDE, 0x80, 0xFF);

        private const float TimeBoostImpactSeconds = 0.66f;
        private const float TimeBoostCountSeconds = 0.36f;
        private const float TimeBoostBumpX = 0.30f;
        private const float TimeBoostBumpY = 0.216f;

        [Header("Card appearance")]
        [SerializeField] private RectTransform rt = null;
        [SerializeField] private CanvasGroup canvasGroup = null;
        [Tooltip("Optional. Tilts the card as it lands, the way a ticket clipped "
               + "to a rail would. Absent on a card that has not been rewired, "
               + "which simply skips the swing.")]
        [SerializeField] private BartenderOrderCardSwing hangSwing = null;
        [SerializeField] private Image background = null;
        [Tooltip("Controls the card shadow and lighting.")]
        [SerializeField] private OrderCardPolish cardPolish = null;
        [Tooltip("Border flashed on a match. Keeps the card colours unchanged.")]
        [SerializeField] private Image edge = null;
        [SerializeField] private Image icon = null;
        [SerializeField] private Image kindBadge = null;
        [Tooltip("Delivered stamp.")]
        [SerializeField] private Image tickBadge = null;
        [SerializeField] private TextMeshProUGUI kindLabel = null;
        [SerializeField] private TextMeshProUGUI description = null;
        [Tooltip("Authored Timer Seconds etiketi.")]
        [FormerlySerializedAs("timerLegacyLabel")]
        [SerializeField] private Text timerText = null;
        [Tooltip("Text shown in an empty slot.")]
        [SerializeField] private TextMeshProUGUI emptyLabel = null;
        [SerializeField] private Image timerFill = null;
        [SerializeField] private RectTransform timerRoot = null;
        [SerializeField] private Image timerClock = null;
        [Header("+Time feedback")]
        [SerializeField] private RectTransform timeBoostFeedbackRoot = null;
        [SerializeField] private CanvasGroup timeBoostFeedbackCanvasGroup = null;
        [SerializeField] private TextMeshProUGUI timeBoostFeedbackLabel = null;
        [SerializeField] private Text timeBoostFeedbackLegacyLabel = null;

        [Header("Order artwork")]
        [Tooltip("Fits the glass here without changing its aspect ratio or this box.")]
        [SerializeField] private RectTransform iconFitBox = null;
        [Tooltip("Original sketch fronts with the existing gameplay liquid renderer and mask.")]
        [SerializeField] private OrderCardVesselView vesselPreview = null;
        [SerializeField, Range(0.5f, 1.6f)] private float glassVisualScale = 1.3f;
        [SerializeField, Range(0.5f, 1.6f)] private float glassVisualScaleWithChips = 1f;
        [Tooltip("Recipe row below the glass. Shown only for SET orders.")]
        [SerializeField] private RectTransform chipColumn = null;
        [Tooltip("Up to five SET colour chips. Repeated colours show a count above the chip.")]
        [SerializeField] private Image[] chips = new Image[0];
        [Tooltip("Size chips from the parent rect. Disable to use their saved sizes.")]
        [SerializeField] private bool chipsFillColumn = true;
        [Tooltip("Show a tray behind the chips using a stretched chip sprite. Off by default.")]
        [SerializeField] private bool showRecipeTray = false;
        [Tooltip("The saved outer tray, even when tray display is disabled.")]
        [SerializeField] private Image recipeTrayBorder = null;
        [Tooltip("Required fill under Recipe Tray Border.")]
        [SerializeField] private Image recipeTrayFill = null;
        [Header("SET counts")]
        [Tooltip("Count Label images in the same order as Chips.")]
        [SerializeField] private Image[] chipFormulaTokens = new Image[0];
        [Tooltip("Count Value TMP labels in the same order as Chips.")]
        [SerializeField] private TextMeshProUGUI[] chipFormulaCountTexts =
            new TextMeshProUGUI[0];
        [Tooltip("Maximum SET chip diameter. Shrinks to fit more colours in the Recipe Chip Row width.")]
        [SerializeField, Min(8f)] private float horizontalChipHeight = 68f;
        [Tooltip("Gap between SET colour circles.")]
        [SerializeField, Min(0f)] private float horizontalFormulaGap = 8f;
        [Tooltip("Glass X position without chips. 0 centres it.")]
        [SerializeField] private float iconSoloOffsetX = 0f;
        [Tooltip("Fallback glass Y offset for SET recipe rows, used only when the centred recipe layout "
               + "cannot be measured. Does not affect LAYER cards.")]
        [SerializeField] private float horizontalChipsIconOffsetY = 0f;

        [Header("SET recipe layout")]
        [Tooltip("Top of the card's cream face, in card units above the card centre. The visible glass and "
               + "recipe row are centred as one group below it.")]
        [SerializeField] private float recipeLayoutAreaTop = 126.2f;
        [Tooltip("Bottom of the card's cream face, in card units from the card centre. A visible timer plate "
               + "raises this limit to the plate's top edge.")]
        [SerializeField] private float recipeLayoutAreaBottom = -122.3f;
        [Tooltip("Space from the visible bottom of the glass drawing to the top of the recipe row, in card units.")]
        [SerializeField, Min(0f)] private float recipeRowGapBelowGlass = 23f;
        [Tooltip("Moves the centred glass and recipe row together, in card units. Negative moves them down.")]
        [SerializeField] private float recipeGroupOffsetY = -10.8f;

        [Header("Completion glow")]
        [Tooltip("Brief warm highlight on a completed order.")]
        [SerializeField] private Color completionLineColor =
            new Color32(0xFF, 0xF4, 0xD0, 0xFF);
        [Tooltip("Gold colour used as the highlight fades.")]
        [SerializeField] private Color completionGlowColor =
            new Color32(0xFF, 0xBD, 0x3E, 0xFF);

        [Header("State colours")]
        [SerializeField] private Color accentColor = new Color(0.95f, 0.68f, 0.20f, 1f);
        [SerializeField] private Color layerBadgeColor = new Color32(0xE8, 0x8B, 0x3C, 0xFF);
        [Tooltip("Faint empty slot appearance.")]
        [SerializeField] private Color emptySlotColor = new Color(1f, 1f, 1f, 0.14f);
        [Header("Timer plate")]
        [Tooltip("Resting timer text colour. Off-white stays readable on the cream plate.")]
        [SerializeField] private Color timerNormalColor = new Color32(0xFF, 0xF6, 0xE0, 0xFF);
        [SerializeField] private Color timerWarningColor = new Color32(0xFF, 0x9F, 0x3D, 0xFF);
        [SerializeField] private Color timerCriticalColor = new Color32(0xFF, 0x59, 0x64, 0xFF);
        [SerializeField, Range(0.05f, 0.95f)] private float timerWarningRatio = 0.33f;
        [SerializeField, Min(1f)] private float timerCriticalSeconds = 5f;

        private bool highlighted;
        private bool initialized;
        private bool restPositionInitialized;
        private bool desiredVisible;
        private readonly BsOrderCardStateMachine presentationState =
            new BsOrderCardStateMachine();
        private uint lifecycleRevision;
        private uint tickRevision;
        private uint edgeRevision;
        private uint timeBoostRevision;
        private Tween lifecycleTween;
        private Tween visibilityTween;
        private Tween edgeTween;
        private Tween tickTween;
        private Tween timeBoostTween;
        private Tween timerExpiryTween;
        private Vector3 authoredScale = Vector3.one;
        private Vector3 authoredEdgeScale = Vector3.one;
        private Vector3 authoredTickScale = Vector3.one;
        private Vector3 authoredTimerScale = Vector3.one;
        private Vector2 restingAnchoredPosition;
        private Vector2 authoredIconPosition;
        private bool iconPositionCaptured;
        private Vector2 authoredChipColumnPosition;
        private bool chipColumnPositionCaptured;
        private float laidOutChipHeight;
        private int shownTimerSecond = -1;
        private float urgentTimerKick;
        private float timeBoostPulse;
        private float timeBoostFlash;
        private float timeBoostCountSeconds;
        private float timeBoostCountProgress = 1f;
        private float lastTimerRemaining;
        private float lastTimerTotal;
        private bool lastTimerMotionAllowed;
        private bool hasTimerInput;
        private Vector2 timerRestAnchoredPosition;
        private bool timerBumpLifted;
        private float timerExpiryPulse;
        private readonly List<PendingTimeBoostPresentation> pendingTimeBoosts =
            new List<PendingTimeBoostPresentation>(2);
        private int nextTimeBoostToken;
        private bool timerFillGeometryCaptured;
        private float timerFillFullWidth;
        private float timerFillLeftEdge;
        private Color timerToneColor = Color.white;
        private Color timerClockBaseColor = Color.white;
        private Vector2 timeBoostFeedbackBasePosition;
        private bool timeBoostFeedbackPoseCaptured;
        private bool authoredTimerWarningIssued;
        private bool authoredRecipeTrayWarningIssued;
        private bool authoredFormulaWarningIssued;
        private bool authoredPolishWarningIssued;
        private BsPalette palette;
        private readonly int[] groupedChipColors = new int[MaxUnits];
        private readonly int[] groupedChipCounts = new int[MaxUnits];

        private struct PendingTimeBoostPresentation
        {
            public int Token;
            public float Seconds;
        }

        public OrderDef Model { get; private set; }
        public RectTransform Rt => rt;
        internal Vector3 DeliveryPoofCenter => rt.TransformPoint(rt.rect.center);
        private Vector3 deliveryCenterWorld;

        internal void BeginSynchronizedDelivery()
        {
            ShowDelivered();
            deliveryCenterWorld = DeliveryPoofCenter;
        }

        internal void SampleSynchronizedDelivery(float opacity, bool finished)
        {
            if (rt == null || canvasGroup == null) return;
            rt.localScale = authoredScale;
            rt.position += deliveryCenterWorld - DeliveryPoofCenter;
            canvasGroup.alpha = finished ? 0f : Mathf.Clamp01(opacity);
        }

        public bool IsReady()
        {
            return rt != null && canvasGroup != null && background != null && edge != null
                   && cardPolish != null
                   && vesselPreview != null && vesselPreview.IsReady
                   && HasAuthoredRecipeTrayHierarchy()
                   && HasAuthoredFormulaHierarchy()
                   && HasAuthoredTimerHierarchy();
        }

        public void Initialize(BsPalette pal)
        {
            palette = pal;
            EnsureRecipeTray();
            EnsureCardPolish();
            // Update the palette each snapshot, but save rest poses only once so a tween's scale cannot become
            // the default.
            if (initialized) return;

            if (rt == null) rt = transform as RectTransform;
            authoredScale = rt != null ? rt.localScale : Vector3.one;
            if (rt != null && !restPositionInitialized)
            {
                restingAnchoredPosition = rt.anchoredPosition;
                restPositionInitialized = true;
            }
            if (iconFitBox != null)
            {
                authoredIconPosition = iconFitBox.anchoredPosition;
                iconPositionCaptured = true;
            }
            if (chipColumn != null)
            {
                authoredChipColumnPosition = chipColumn.anchoredPosition;
                chipColumnPositionCaptured = true;
            }
            authoredEdgeScale = edge != null
                ? edge.rectTransform.localScale
                : Vector3.one;
            authoredTickScale = tickBadge != null
                ? tickBadge.rectTransform.localScale
                : Vector3.one;
            authoredTimerScale = timerRoot != null
                ? timerRoot.localScale
                : Vector3.one;
            CaptureTimerFillGeometry();
            CaptureTimerArtState();
            highlighted = false;
            SetCardLift(0f);
            CanonicalizeCompletionEdge();
            desiredVisible = false;
            presentationState.Dispatch(BsOrderCardTrigger.InitializeHidden);
            CanonicalizeVisibility(false);
            initialized = true;
        }

        private void EnsureRecipeTray()
        {
            if (!HasAuthoredRecipeTrayHierarchy())
            {
                if (!authoredRecipeTrayWarningIssued)
                {
                    authoredRecipeTrayWarningIssued = true;
                    Debug.LogError(
                        "Authored Recipe Tray Border/Fill "
                        + "bindings invalid.",
                        this);
                }
                return;
            }

            recipeTrayBorder.gameObject.SetActive(showRecipeTray);
        }

        private bool HasAuthoredRecipeTrayHierarchy() =>
            chipColumn != null && recipeTrayBorder != null
            && recipeTrayBorder.transform.parent == chipColumn
            && recipeTrayFill != null
            && recipeTrayFill.transform.parent == recipeTrayBorder.transform
            && RecipeTrayIsBehindChips();

        private bool RecipeTrayIsBehindChips()
        {
            if (recipeTrayBorder == null || chips == null) return false;
            int trayIndex = recipeTrayBorder.transform.GetSiblingIndex();
            for (int i = 0; i < chips.Length; i++)
                if (chips[i] != null
                    && chips[i].transform.parent == chipColumn
                    && trayIndex >= chips[i].transform.GetSiblingIndex())
                    return false;
            return true;
        }

        private void EnsureCardPolish()
        {
            if (background == null) return;
            if (cardPolish == null)
            {
                if (!authoredPolishWarningIssued)
                {
                    authoredPolishWarningIssued = true;
                    Debug.LogError(
                        "Order card polish missing.", this);
                }
                return;
            }
            cardPolish.Bind(background);
            cardPolish.SetSourceOpacity(background.color.a);
        }

        private float RestingCardLift => highlighted ? 0.16f : 0f;

        private void SetCardLift(float value)
        {
            if (cardPolish != null) cardPolish.SetLift(value);
        }

        private Tween CardLiftTween(float target, float duration, Ease ease)
        {
            if (cardPolish == null) return null;
            return DOTween.To(
                    () => cardPolish != null ? cardPolish.Lift : 0f,
                    value =>
                    {
                        if (cardPolish != null) cardPolish.SetLift(value);
                    }, target, duration)
                .SetEase(ease).SetRecyclable(true);
        }

        public void SetOrder(OrderDef order, bool timedOrdersEnabled)
        {
            if (!initialized) Initialize(palette);

            bool orderChanged = !SameOrder(Model, order);
            if (orderChanged)
            {
                CancelTimerExpiredFeedback();
                CancelTimeBoostFeedback();
            }
            Model = order;
            bool has = order != null;

            if (icon != null && icon.sprite == null) icon.gameObject.SetActive(false);
            if (kindBadge != null) kindBadge.gameObject.SetActive(has);
            if (tickBadge != null) tickBadge.gameObject.SetActive(false);
            if (background != null)
            {
                background.color = has ? Color.white : emptySlotColor;
                if (cardPolish != null)
                    cardPolish.SetSourceOpacity(background.color.a);
            }
            if (orderChanged)
            {
                highlighted = false;
                SetCardLift(0f);
                CancelEdgeTween();
                CancelTickTween();
            }
            if (emptyLabel != null) emptyLabel.gameObject.SetActive(!has);

            if (!has)
            {
                if (description != null) description.text = "";
                if (timerRoot != null) timerRoot.gameObject.SetActive(false);
                ResetTimerVisual();
                DrawOrder(null);
                return;
            }

            bool layer = order.Kind == OrderKind.Layer;
            if (kindLabel != null) kindLabel.text = layer ? "LAYER" : "SET";
            if (kindBadge != null) kindBadge.color = layer ? layerBadgeColor : accentColor;
            if (description != null && palette != null)
                description.text = order.Describe(palette);

            bool timed = timedOrdersEnabled && order.TimeLimit > 0f;
            if (!timed) CancelTimeBoostFeedback();
            if (timed) EnsureTimerVisuals();
            if (timerRoot != null) timerRoot.gameObject.SetActive(timed);
            if (!timed || orderChanged) ResetTimerVisual();
            DrawOrder(order);
        }

        private void DrawOrder(OrderDef order)
        {
            int units = order != null && order.Contents != null ? order.Contents.Count : 0;
            bool singleUnitSet = order != null && order.Kind == OrderKind.Set
                                 && units == 1;
            bool asFill = order != null
                          && (order.Kind == OrderKind.Layer || singleUnitSet);
            bool asChips = order != null && order.Kind == OrderKind.Set
                           && !singleUnitSet;
            int chipsShown = DrawChips(asChips ? order : null, units);
            float glassScale = chipsShown > 0 ? glassVisualScaleWithChips : glassVisualScale;
            PlaceIconBox(chipsShown > 0, order != null ? order.Glass : GlassType.Shot, glassScale);
            if (vesselPreview == null) return;
            if (order == null) vesselPreview.Hide();
            else vesselPreview.Show(order.Glass, order.Contents, palette, asFill,
                glassScale, chipsShown == 0);
        }

        private void PlaceIconBox(bool chipsVisible, GlassType glass, float glassScale)
        {
            if (iconFitBox == null || !iconPositionCaptured) return;

            if (!chipsVisible)
            {
                iconFitBox.anchoredPosition = GetCenteredSoloIconPosition();
                return;
            }
            if (TryPlaceRecipeGroup(glass, glassScale)) return;

            if (chipColumn != null && chipColumnPositionCaptured)
                chipColumn.anchoredPosition = authoredChipColumnPosition;
            iconFitBox.anchoredPosition =
                new Vector2(iconSoloOffsetX, authoredIconPosition.y + horizontalChipsIconOffsetY);
        }

        private bool TryPlaceRecipeGroup(GlassType glass, float glassScale)
        {
            RectTransform content = iconFitBox.parent as RectTransform;
            if (rt == null || content == null || chipColumn == null || !chipColumnPositionCaptured
                || chipColumn.parent != content || laidOutChipHeight <= 0f || vesselPreview == null
                || !vesselPreview.TryGetVisibleGlassSpan(glass, glassScale, content,
                    out float glassBottom, out float glassTop))
                return false;

            float cardUnit = content.InverseTransformVector(rt.TransformVector(Vector3.up)).y;
            float chipHeight = content.InverseTransformVector(
                chipColumn.TransformVector(new Vector3(0f, laidOutChipHeight, 0f))).y;
            float areaTopLocal = rt.rect.center.y + recipeLayoutAreaTop;
            float areaBottomLocal = rt.rect.center.y + recipeLayoutAreaBottom;
            if (TryGetRestingTimerTop(out float timerTop))
                areaBottomLocal = Mathf.Max(areaBottomLocal, timerTop);
            float areaTop = CardToContentY(content, areaTopLocal);
            float areaBottom = CardToContentY(content, areaBottomLocal);

            float boxCentre = content.InverseTransformPoint(
                iconFitBox.TransformPoint(iconFitBox.rect.center)).y;
            float groupTop = glassTop - boxCentre;
            float chipTop = glassBottom - boxCentre - recipeRowGapBelowGlass * cardUnit;
            float groupBottom = chipTop - chipHeight;
            float boxY = (areaTop + areaBottom - groupTop - groupBottom) * 0.5f
                         + recipeGroupOffsetY * cardUnit;

            iconFitBox.anchoredPosition = new Vector2(iconSoloOffsetX,
                iconFitBox.anchoredPosition.y + boxY - boxCentre);
            float rowCentre = content.InverseTransformPoint(
                chipColumn.TransformPoint(chipColumn.rect.center)).y;
            chipColumn.anchoredPosition = new Vector2(authoredChipColumnPosition.x,
                chipColumn.anchoredPosition.y + boxY + chipTop - chipHeight * 0.5f - rowCentre);
            return true;
        }

        private bool TryGetRestingTimerTop(out float top)
        {
            top = 0f;
            if (timerRoot == null || !timerRoot.gameObject.activeSelf || timerRoot.parent != transform)
                return false;
            float y = timerRoot.localPosition.y;
            if (timerBumpLifted) y += timerRestAnchoredPosition.y - timerRoot.anchoredPosition.y;
            top = y + (1f - timerRoot.pivot.y) * timerRoot.rect.height * authoredTimerScale.y;
            return true;
        }

        private float CardToContentY(RectTransform content, float cardLocalY) =>
            content.InverseTransformPoint(
                rt.TransformPoint(new Vector3(rt.rect.center.x, cardLocalY, 0f))).y;

        private Vector2 GetCenteredSoloIconPosition()
        {
            RectTransform parent = iconFitBox.parent as RectTransform;
            if (rt == null || parent == null)
                return new Vector2(iconSoloOffsetX, authoredIconPosition.y);

            Vector3 cardCenterInParent = parent.InverseTransformPoint(
                rt.TransformPoint(rt.rect.center));
            Vector3 iconCenterInParent = parent.InverseTransformPoint(
                iconFitBox.TransformPoint(iconFitBox.rect.center));
            Vector2 centered = iconFitBox.anchoredPosition
                               + (Vector2)(cardCenterInParent - iconCenterInParent);
            centered.x += iconSoloOffsetX;
            return centered;
        }

        private static void SetNormalizedRect(RectTransform target, Vector2 min,
                                              Vector2 max)
        {
            target.anchorMin = min;
            target.anchorMax = max;
            target.pivot = new Vector2(0.5f, 0.5f);
            target.offsetMin = Vector2.zero;
            target.offsetMax = Vector2.zero;
        }

        private int DrawChips(OrderDef order, int units)
        {
            laidOutChipHeight = 0f;
            EnsureChipFormulaTokenCache();
            bool any = order != null && order.Contents != null
                       && units > 0 && chips != null;
            int grouped = any ? GroupChipRequirements(order, units) : 0;
            bool useGrouped = grouped >= 0 && CanRenderGroupedChips(grouped);
            int shown = any
                ? Mathf.Clamp(useGrouped ? grouped : units, 0, chips.Length)
                : 0;
            if (chipColumn != null) chipColumn.gameObject.SetActive(shown > 0);
            if (chips == null) return 0;

            Vector2 size = Vector2.zero;
            bool resize = false;
            if (shown > 0 && TryMeasureChips(out Vector2 measured))
            {
                size = measured;
                resize = true;
            }

            for (int i = 0; i < chips.Length; i++)
            {
                Image chip = chips[i];
                bool live = i < shown;
                int requirementIndex = live ? shown - 1 - i : i;
                int count = live && useGrouped
                    ? groupedChipCounts[requirementIndex]
                    : 1;
                SetChipFormulaToken(i, live ? count : 0);
                if (chip == null) continue;

                chip.gameObject.SetActive(live);
                if (!live) continue;

                if (resize) chip.rectTransform.sizeDelta = size;
                int colorIndex = useGrouped
                    ? groupedChipColors[requirementIndex]
                    : order.Contents[requirementIndex];
                chip.color = palette != null
                    ? palette.ColorAt(colorIndex)
                    : Color.magenta;
            }
            if (shown > 0) LayoutHorizontalChipFormula(shown, size);
            return shown;
        }

        private void LayoutHorizontalChipFormula(int shown, Vector2 measuredSize)
        {
            if (chipColumn == null || chips == null || shown <= 0) return;
            Rect line = chipColumn.rect;
            if (line.width <= 0f || line.height <= 0f) return;

            Vector2 sourceSize = measuredSize;
            if (sourceSize.x <= 0f || sourceSize.y <= 0f)
            {
                Image sample = null;
                for (int i = 0; i < shown && sample == null; i++)
                    if (chips[i] != null) sample = chips[i];
                if (sample == null) return;

                sourceSize = sample.rectTransform.rect.size;
                if (sourceSize.x <= 0f || sourceSize.y <= 0f) return;
            }

            float gap = Mathf.Max(0f, horizontalFormulaGap);
            float availableWidth = line.width - gap * Mathf.Max(0, shown - 1);
            if (availableWidth <= 0f) return;

            float fit = Mathf.Min(
                1f,
                Mathf.Min(
                    line.height / sourceSize.y,
                    availableWidth / shown / sourceSize.x));
            Vector2 fittedSize = sourceSize * fit;
            laidOutChipHeight = fittedSize.y;
            float rowWidth = fittedSize.x * shown + gap * Mathf.Max(0, shown - 1);
            float firstCenter = -rowWidth * 0.5f + fittedSize.x * 0.5f;

            for (int i = 0; i < shown; i++)
            {
                Image chip = chips[i];
                if (chip == null) continue;

                RectTransform chipRt = chip.rectTransform;
                chipRt.anchorMin = new Vector2(0.5f, 0.5f);
                chipRt.anchorMax = new Vector2(0.5f, 0.5f);
                chipRt.pivot = new Vector2(0.5f, 0.5f);
                chipRt.sizeDelta = fittedSize;
                chipRt.anchoredPosition = new Vector2(
                    firstCenter + i * (fittedSize.x + gap), 0f);
                LayoutChipCountOverlay(i, fittedSize.y);
            }
        }

        private int GroupChipRequirements(OrderDef order, int units)
        {
            Array.Clear(groupedChipCounts, 0, groupedChipCounts.Length);
            int grouped = 0;
            int count = Mathf.Min(units, order.Contents.Count);
            for (int i = 0; i < count; i++)
            {
                int color = order.Contents[i];
                int found = -1;
                for (int j = 0; j < grouped; j++)
                {
                    if (groupedChipColors[j] != color) continue;
                    found = j;
                    break;
                }

                if (found >= 0)
                {
                    groupedChipCounts[found]++;
                    continue;
                }

                if (grouped >= MaxUnits) return -1;
                groupedChipColors[grouped] = color;
                groupedChipCounts[grouped] = 1;
                grouped++;
            }
            return grouped;
        }

        private bool CanRenderGroupedChips(int grouped)
        {
            for (int i = 0; i < grouped; i++)
            {
                int displayIndex = grouped - 1 - i;
                if (groupedChipCounts[i] <= 1) continue;

                TextMeshProUGUI countText = ChipFormulaCountTextAt(displayIndex);
                if (ChipFormulaTokenAt(displayIndex) == null
                    || countText == null || countText.font == null) return false;
            }
            return true;
        }

        private void EnsureChipFormulaTokenCache()
        {
            if (HasAuthoredFormulaHierarchy() || authoredFormulaWarningIssued) return;

            authoredFormulaWarningIssued = true;
            Debug.LogError(
                "SET formula hierarchy invalid.",
                this);
        }

        private bool HasAuthoredFormulaHierarchy()
        {
            int count = chips != null ? chips.Length : 0;
            bool valid = count >= MaxUnits
                         && chipFormulaTokens != null
                         && chipFormulaTokens.Length == count
                         && chipFormulaCountTexts != null
                         && chipFormulaCountTexts.Length == count;
            for (int i = 0; valid && i < count; i++)
            {
                Image chip = chips[i];
                Image token = chipFormulaTokens[i];
                TextMeshProUGUI text = chipFormulaCountTexts[i];
                valid = chip != null && token != null
                        && chip.transform.parent == chipColumn
                        && token.transform.parent == chip.transform
                        && token.transform.GetSiblingIndex()
                           == chip.transform.childCount - 1
                        && text != null && text.transform.IsChildOf(token.transform)
                        && text.transform.GetSiblingIndex()
                           == token.transform.childCount - 1;
            }
            return valid;
        }

        private Image ChipFormulaTokenAt(int index)
        {
            return chipFormulaTokens != null
                   && index >= 0 && index < chipFormulaTokens.Length
                ? chipFormulaTokens[index]
                : null;
        }

        private TextMeshProUGUI ChipFormulaCountTextAt(int index)
        {
            return chipFormulaCountTexts != null
                   && index >= 0 && index < chipFormulaCountTexts.Length
                ? chipFormulaCountTexts[index]
                : null;
        }

        private void SetChipFormulaToken(int index, int count)
        {
            Image token = ChipFormulaTokenAt(index);
            TextMeshProUGUI countText = ChipFormulaCountTextAt(index);
            if (token == null) return;

            bool showCount = count > 1 && countText != null && countText.font != null;
            token.sprite = null;
            token.enabled = false;
            token.type = Image.Type.Simple;
            token.preserveAspect = false;
            token.color = Color.white;
            token.raycastTarget = false;
            token.gameObject.SetActive(showCount);

            if (countText != null)
            {
                countText.text = showCount ? count.ToString() : string.Empty;
                countText.gameObject.SetActive(showCount);
            }
        }

        private void LayoutChipCountOverlay(int index, float chipHeight)
        {
            Image token = ChipFormulaTokenAt(index);
            if (token == null) return;

            RectTransform tokenRt = token.rectTransform;
            SetNormalizedRect(tokenRt, Vector2.zero, Vector2.one);

            TextMeshProUGUI countText = ChipFormulaCountTextAt(index);
            if (countText == null) return;
            SetNormalizedRect(countText.rectTransform, Vector2.zero, Vector2.one);
            countText.fontSize = Mathf.Max(14f, chipHeight * 0.80f);
        }

        private bool TryMeasureChips(out Vector2 size)
        {
            size = Vector2.zero;
            if (!chipsFillColumn || chipColumn == null || chips == null) return false;

            Rect column = chipColumn.rect;
            if (column.width <= 0f || column.height <= 0f) return false;

            Sprite art = null;
            for (int i = 0; i < chips.Length && art == null; i++)
                if (chips[i] != null) art = chips[i].sprite;
            if (art == null || art.rect.width <= 0f || art.rect.height <= 0f)
                return false;

            float horizontalHeight = Mathf.Min(column.height, horizontalChipHeight);
            size = new Vector2(
                horizontalHeight * art.rect.width / art.rect.height,
                horizontalHeight);
            return true;
        }

        public void SetVisible(bool visible, bool animate)
        {
            if (canvasGroup == null) return;
            if (!initialized) Initialize(palette);

            if (!visible)
            {
                CancelTimerExpiredFeedback();
                CancelTimeBoostFeedback();
            }

            desiredVisible = visible;
            bool lifecycleBoundary = presentationState.State
                                     == BsOrderCardState.Uninitialized
                                     || presentationState.State
                                     == BsOrderCardState.Disabled;
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            {
                InvalidateLifecycleTweens();
                CanonicalizePose();
                presentationState.Dispatch(BsOrderCardTrigger.Disable);
                CanonicalizeVisibility(false);
                return;
            }
            // Hide immediately before the first render; a saved alpha of 1 must not cause a flash.
            if (!visible && lifecycleBoundary) animate = false;

            float targetAlpha = visible ? 1f : 0f;
            bool alreadyStable = visible
                ? presentationState.State == BsOrderCardState.Visible
                  && Mathf.Approximately(canvasGroup.alpha, 1f)
                : presentationState.State == BsOrderCardState.Hidden
                  && Mathf.Approximately(canvasGroup.alpha, 0f);
            bool matchingTransition = visible
                ? presentationState.State == BsOrderCardState.Dealing
                  && HasActiveTween(lifecycleTween, visibilityTween)
                : presentationState.State == BsOrderCardState.Exiting
                  && HasActiveTween(lifecycleTween, visibilityTween);

            if (animate && (alreadyStable || matchingTransition))
            {
                SetCanvasInteraction(false);
                return;
            }

            uint revision = InvalidateLifecycleTweens();
            CanonicalizePose();
            if (!animate || Mathf.Approximately(canvasGroup.alpha, targetAlpha))
            {
                presentationState.Dispatch(visible
                    ? BsOrderCardTrigger.ShowImmediate
                    : BsOrderCardTrigger.HideImmediate);
                CanonicalizeVisibility(visible);
                return;
            }

            bool accepted = presentationState.Dispatch(visible
                ? BsOrderCardTrigger.BeginDeal
                : BsOrderCardTrigger.BeginExit);
            if (!accepted)
            {
                presentationState.Dispatch(visible
                    ? BsOrderCardTrigger.ShowImmediate
                    : BsOrderCardTrigger.HideImmediate);
                CanonicalizeVisibility(visible);
                return;
            }

            SetCanvasInteraction(false);
            Tween tween = canvasGroup.DOFade(targetAlpha, 0.2f)
                .SetUpdate(true).SetRecyclable(true);
            visibilityTween = tween;
            tween.OnComplete(() => CompleteVisibilityTween(tween, revision, visible))
                .OnKill(() => ForgetVisibilityTween(tween, revision));
        }

        public void SetTimer(float remaining, float total, bool motionAllowed)
        {
            EnsureTimerVisuals();
            lastTimerRemaining = remaining;
            lastTimerTotal = total;
            lastTimerMotionAllowed = motionAllowed;
            hasTimerInput = true;
            if (timerRoot == null || !timerRoot.gameObject.activeSelf) return;

            bool countingIn = timeBoostCountProgress < 1f;
            float withheld = PendingTimeBoostSeconds() + CountingInTimeBoostSeconds();
            float safeRemaining = Mathf.Max(0f, remaining - withheld);
            float visibleTotal = Mathf.Max(0f, total - withheld);
            float t = visibleTotal > 0f
                ? Mathf.Clamp01(safeRemaining / visibleTotal)
                : 0f;
            SetTimerProgress(t);

            bool critical = safeRemaining > 0f && safeRemaining <= timerCriticalSeconds;
            timerToneColor = safeRemaining <= 0f || critical
                ? timerCriticalColor
                : t <= timerWarningRatio ? timerWarningColor : timerNormalColor;
            ApplyTimerFeedbackColors();

            int second = Mathf.CeilToInt(safeRemaining);
            if (second != shownTimerSecond)
            {
                SetTimerText(FormatClock(second));
                if (critical && shownTimerSecond >= 0 && !countingIn) urgentTimerKick = 1f;
                shownTimerSecond = second;
            }

            if (!critical || !motionAllowed)
            {
                urgentTimerKick = 0f;
                ApplyTimerMotion();
                return;
            }

            ApplyTimerMotion();
            urgentTimerKick = Mathf.MoveTowards(
                urgentTimerKick, 0f, Time.unscaledDeltaTime * 7.5f);
        }

        public Tween PlayTimerExpiredFeedback()
        {
            CancelTimerExpiredFeedback();
            if (!isActiveAndEnabled || !desiredVisible || !HasVisibleTimer
                || Model == null || Model.TimeLimit <= 0f)
                return null;

            CancelTimeBoostFeedback();
            SetTimer(0f, Model.TimeLimit, false);
            Sequence sequence = DOTween.Sequence()
                .SetTarget(this).SetUpdate(true).SetRecyclable(false);
            sequence.Append(DOTween.To(() => timerExpiryPulse, value =>
                {
                    timerExpiryPulse = value;
                    ApplyTimerMotion();
                }, 1f, 0.16f).SetEase(Ease.OutCubic));
            sequence.AppendInterval(0.06f);
            sequence.Append(DOTween.To(() => timerExpiryPulse, value =>
                {
                    timerExpiryPulse = value;
                    ApplyTimerMotion();
                }, 0f, 0.10f).SetEase(Ease.InOutSine));
            timerExpiryTween = sequence;
            sequence.OnKill(() =>
            {
                if (!ReferenceEquals(timerExpiryTween, sequence)) return;
                timerExpiryTween = null;
                timerExpiryPulse = 0f;
                ApplyTimerMotion();
            });
            return sequence;
        }

        private void CancelTimerExpiredFeedback()
        {
            Tween tween = timerExpiryTween;
            timerExpiryTween = null;
            if (tween != null && tween.IsActive()) tween.Kill(false);
            timerExpiryPulse = 0f;
            ApplyTimerMotion();
        }

        public void SuspendTimerEmphasis()
        {
            InvalidateTimeBoostTween();
            ResetTimerMotion();
        }

        public int HoldTimeBoostPresentation(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f
                || Model == null || Model.TimeLimit <= 0f)
                return 0;

            int token = ++nextTimeBoostToken;
            if (token <= 0)
            {
                nextTimeBoostToken = 1;
                token = 1;
            }
            pendingTimeBoosts.Add(new PendingTimeBoostPresentation
            {
                Token = token,
                Seconds = seconds
            });
            return token;
        }

        public void ReleaseTimeBoostPresentation(int token)
        {
            if (token <= 0) return;
            for (int i = pendingTimeBoosts.Count - 1; i >= 0; i--)
            {
                if (pendingTimeBoosts[i].Token != token) continue;
                pendingTimeBoosts.RemoveAt(i);
                return;
            }
        }

        public void PlayTimeBoostFeedback(float seconds)
        {
            bool canReceiveImpact = presentationState.State == BsOrderCardState.Visible
                                    || presentationState.State == BsOrderCardState.Shifting;
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f
                || Model == null || Model.TimeLimit <= 0f || !desiredVisible
                || !canReceiveImpact
                || !isActiveAndEnabled || !gameObject.activeInHierarchy)
                return;

            EnsureTimerVisuals();
            if (timerRoot == null || !timerRoot.gameObject.activeSelf) return;

            uint revision = InvalidateTimeBoostTween();
            timeBoostCountSeconds = seconds;
            // Start from the old number in this same frame so the new one never flashes first.
            ApplyTimeBoostImpact(0f);

            Tween tween = DOVirtual.Float(0f, TimeBoostImpactSeconds, TimeBoostImpactSeconds,
                    ApplyTimeBoostImpact)
                .SetEase(Ease.Linear).SetTarget(timerRoot).SetUpdate(true).SetRecyclable(true);
            timeBoostTween = tween;
            tween.OnComplete(() => CompleteTimeBoostTween(tween, revision))
                .OnKill(() => ForgetTimeBoostTween(tween, revision));
        }

        private void ApplyTimeBoostImpact(float elapsed)
        {
            if (elapsed < 0.07f)
            {
                float rise = elapsed / 0.07f;
                timeBoostPulse = 1f - (1f - rise) * (1f - rise);
            }
            else if (elapsed < 0.34f) timeBoostPulse = Mathf.Pow(1f - (elapsed - 0.07f) / 0.27f, 3f);
            else if (elapsed < 0.5f) timeBoostPulse = Mathf.Sin(Mathf.PI * (elapsed - 0.34f) / 0.16f) * 0.16f;
            else timeBoostPulse = 0f;

            timeBoostFlash = elapsed < 0.08f
                ? elapsed / 0.08f
                : elapsed < TimeBoostCountSeconds
                    ? 1f
                    : Mathf.Clamp01(1f - (elapsed - TimeBoostCountSeconds) / 0.30f);
            timeBoostCountProgress = Mathf.Clamp01(elapsed / TimeBoostCountSeconds);

            if (hasTimerInput)
            {
                SetTimer(lastTimerRemaining, lastTimerTotal, lastTimerMotionAllowed);
                return;
            }
            ApplyTimerMotion();
            ApplyTimerFeedbackColors();
        }

        private float CountingInTimeBoostSeconds() =>
            timeBoostCountProgress >= 1f
                ? 0f
                : timeBoostCountSeconds * Mathf.Pow(1f - timeBoostCountProgress, 3f);

        public void CancelTimeBoostFeedback()
        {
            ClearPendingTimeBoostPresentation();
            InvalidateTimeBoostTween();
        }

        public bool TryGetTimerAnchor(out Vector3 worldCenter)
        {
            worldCenter = default;
            if (timerRoot == null || !timerRoot.gameObject.activeInHierarchy) return false;
            worldCenter = timerRoot.TransformPoint(timerRoot.rect.center);
            return true;
        }

        public bool HasVisibleTimer =>
            timerRoot != null && timerRoot.gameObject.activeInHierarchy;

        public RectTransform TimerTarget => HasVisibleTimer ? timerRoot : null;

        public void SetRestingPosition(Vector2 anchoredPosition, bool snap)
        {
            if (rt == null) rt = transform as RectTransform;
            bool restingPositionUnchanged = restPositionInitialized
                && (restingAnchoredPosition - anchoredPosition).sqrMagnitude
                <= 0.000001f;
            restingAnchoredPosition = anchoredPosition;
            restPositionInitialized = true;
            if (!snap || rt == null) return;
            if (restingPositionUnchanged
                && (rt.anchoredPosition - anchoredPosition).sqrMagnitude
                <= 0.000001f) return;

            // Clear the old delivery glow before dealing a reused card, even if its new order has the same
            // value.
            if (!desiredVisible)
            {
                highlighted = false;
                CancelEdgeTween();
            }

            InvalidateLifecycleTweens();
            presentationState.Dispatch(desiredVisible
                ? BsOrderCardTrigger.ResetVisible
                : BsOrderCardTrigger.ResetHidden);
            rt.anchoredPosition = anchoredPosition;
            rt.localScale = authoredScale;
            SetCardLift(RestingCardLift);
            CanonicalizeVisibility(desiredVisible);
        }

        public Tween PlayDealIn(float delay = 0f, Action onLanded = null)
        {
            CancelTimerExpiredFeedback();
            CancelTimeBoostFeedback();
            if (rt == null || canvasGroup == null) return null;
            if (!isActiveAndEnabled || !gameObject.activeInHierarchy)
            {
                SetVisible(true, false);
                return null;
            }

            desiredVisible = true;
            uint revision = InvalidateLifecycleTweens();
            presentationState.Dispatch(BsOrderCardTrigger.BeginDeal);
            ResetTimerVisual();
            float width = Mathf.Max(1f, rt.rect.width);
            Vector2 start = restingAnchoredPosition + new Vector2(width * 0.48f, -width * 0.06f);
            rt.anchoredPosition = start;
            rt.localScale = authoredScale * 0.90f;
            SetCardLift(0.72f);
            canvasGroup.alpha = 0f;

            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            if (delay > 0f) sequence.AppendInterval(delay);
            sequence.Append(rt.DOAnchorPos(restingAnchoredPosition, 0.26f)
                .SetEase(Ease.OutCubic).SetRecyclable(true)
                // Start the swing on landing. A cancelled deal cannot leave a tilted card.
                .OnComplete(() =>
                {
                    PlayHangSwing();
                    onLanded?.Invoke();
                }));
            sequence.Join(canvasGroup.DOFade(1f, 0.14f)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Join(rt.DOScale(authoredScale * 1.035f, 0.21f)
                .SetEase(Ease.OutCubic).SetRecyclable(true));
            Tween liftTween = CardLiftTween(RestingCardLift, 0.30f, Ease.OutCubic);
            if (liftTween != null) sequence.Join(liftTween);
            sequence.Append(rt.DOScale(authoredScale, 0.11f)
                .SetEase(Ease.OutBack).SetRecyclable(true));
            return TrackLifecycle(sequence, revision, true);
        }

        public Tween PlayQueueExit(float duration)
        {
            CancelTimerExpiredFeedback();
            CancelTimeBoostFeedback();
            if (rt == null || canvasGroup == null) return null;

            desiredVisible = false;
            uint revision = InvalidateLifecycleTweens();
            if (!presentationState.Dispatch(BsOrderCardTrigger.BeginExit))
            {
                presentationState.Dispatch(BsOrderCardTrigger.HideImmediate);
                CanonicalizeVisibility(false);
                return null;
            }
            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            sequence.Append(canvasGroup.DOFade(0f, duration)
                .SetEase(Ease.InOutSine).SetRecyclable(true));
            return TrackLifecycle(sequence, revision, false);
        }

        public Tween PlayQueueShift(Vector2 destination, float duration, float delay)
        {
            SuspendTimerEmphasis();
            if (rt == null) return null;
            desiredVisible = true;
            uint revision = InvalidateLifecycleTweens();
            if (!presentationState.Dispatch(BsOrderCardTrigger.BeginShift))
            {
                presentationState.Dispatch(BsOrderCardTrigger.ShowImmediate);
                CanonicalizeVisibility(true);
            }

            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            if (delay > 0f) sequence.AppendInterval(delay);
            sequence.Append(rt.DOAnchorPos(destination, duration)
                .SetEase(Ease.InOutCubic).SetRecyclable(true));
            return TrackLifecycle(sequence, revision, false);
        }

        public void ShowDelivered()
        {
            SuspendTimerEmphasis();
            highlighted = false;
            desiredVisible = true;
            InvalidateLifecycleTweens();
            InvalidateTickTween();
            if (hangSwing != null) hangSwing.Stop();
            presentationState.Dispatch(BsOrderCardTrigger.ResetVisible);
            CanonicalizePose();
            CanonicalizeVisibility(true);
            if (tickBadge != null) tickBadge.gameObject.SetActive(false);
            PlayDeliveryEdgeGlow();
        }

        private void PlayDeliveryEdgeGlow()
        {
            if (edge == null) return;

            uint revision = InvalidateEdgeTween();
            edge.rectTransform.localScale = authoredEdgeScale;
            Sequence sequence = DOTween.Sequence()
                .SetTarget(edge).SetUpdate(true).SetRecyclable(true)
                .Append(edge.DOColor(WithAlpha(completionLineColor, 0.70f),
                        0.05f)
                    .SetEase(Ease.OutSine).SetRecyclable(true))
                .Append(edge.DOColor(WithAlpha(completionGlowColor, 0f), 0.20f)
                    .SetEase(Ease.InSine).SetRecyclable(true));
            TrackCompletionEdge(sequence, revision);
        }

        internal void CompletePendingDeal()
        {
            if (presentationState.State != BsOrderCardState.Dealing) return;
            desiredVisible = true;
            InvalidateLifecycleTweens();
            presentationState.Dispatch(BsOrderCardTrigger.ResetVisible);
            CanonicalizePose();
            CanonicalizeVisibility(true);
        }

        public void SetHighlighted(bool on)
        {
            if (highlighted == on) return;
            highlighted = on;
            SetCardLift(RestingCardLift);

            if (on) PlayCompletionEdgePulse();
            else FadeCompletionEdge();

            if (!on || rt == null || presentationState.State
                != BsOrderCardState.Visible || IsTweenActive(lifecycleTween)) return;
            uint revision = InvalidateLifecycleTweens();
            Sequence sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true)
                .Append(rt.DOScale(authoredScale * 1.025f, 0.11f)
                    .SetEase(Ease.OutCubic).SetRecyclable(true))
                .Append(rt.DOScale(authoredScale, 0.20f)
                    .SetEase(Ease.InOutSine).SetRecyclable(true));
            TrackPosePulse(sequence, revision);
        }

        private void PlayCompletionEdgePulse()
        {
            if (edge == null) return;

            uint revision = InvalidateEdgeTween();
            RectTransform lineRect = edge.rectTransform;
            Color lineWarm = WithAlpha(completionGlowColor, 0.62f);
            Color lineRest = CompletionEdgeRestColor();

            Sequence sequence = DOTween.Sequence()
                .SetTarget(edge).SetUpdate(true).SetRecyclable(true)
                .Append(edge.DOColor(completionLineColor, 0.10f)
                    .SetEase(Ease.OutSine).SetRecyclable(true))
                .Join(lineRect.DOScale(authoredEdgeScale * 1.012f, 0.14f)
                    .SetEase(Ease.OutCubic).SetRecyclable(true))
                .Append(edge.DOColor(lineWarm, 0.14f)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .AppendInterval(0.06f)
                .Append(edge.DOColor(lineRest, 0.36f)
                    .SetEase(Ease.InSine).SetRecyclable(true))
                .Join(lineRect.DOScale(authoredEdgeScale, 0.36f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));

            TrackCompletionEdge(sequence, revision);
        }

        private void FadeCompletionEdge()
        {
            if (edge == null) return;

            if (edge.color.a <= 0.002f)
            {
                CancelEdgeTween();
                return;
            }

            uint revision = InvalidateEdgeTween();
            Sequence sequence = DOTween.Sequence()
                .SetTarget(edge).SetUpdate(true).SetRecyclable(true)
                .Append(edge.DOFade(0f, 0.22f)
                    .SetEase(Ease.InSine).SetRecyclable(true))
                .Join(edge.rectTransform.DOScale(authoredEdgeScale, 0.22f)
                    .SetEase(Ease.OutSine).SetRecyclable(true));
            TrackCompletionEdge(sequence, revision);
        }

        public void ResetPose()
        {
            CancelTimeBoostFeedback();
            InvalidateLifecycleTweens();
            presentationState.Dispatch(desiredVisible
                ? BsOrderCardTrigger.ResetVisible
                : BsOrderCardTrigger.ResetHidden);
            if (rt != null)
            {
                rt.localScale = authoredScale;
                if (restPositionInitialized) rt.anchoredPosition = restingAnchoredPosition;
            }
            CanonicalizeVisibility(desiredVisible);
            InvalidateTickTween();
            if (tickBadge != null)
            {
                tickBadge.rectTransform.localScale = authoredTickScale;
                tickBadge.gameObject.SetActive(false);
            }
            highlighted = false;
            SetCardLift(0f);
            CancelEdgeTween();
            ResetTimerVisual();
        }

        private void OnEnable()
        {
            EnsureCardPolish();
            if (!initialized) return;
            presentationState.Dispatch(desiredVisible
                ? BsOrderCardTrigger.ResetVisible
                : BsOrderCardTrigger.ResetHidden);
            CanonicalizeVisibility(desiredVisible);
        }

        private void OnDisable()
        {
            CancelTimerExpiredFeedback();
            CancelTimeBoostFeedback();
            InvalidateLifecycleTweens();
            InvalidateTickTween();
            highlighted = false;
            CancelEdgeTween();
            presentationState.Dispatch(BsOrderCardTrigger.Disable);
            if (rt != null)
            {
                rt.localScale = authoredScale;
                if (restPositionInitialized) rt.anchoredPosition = restingAnchoredPosition;
            }
            SetCardLift(0f);
            CanonicalizeVisibility(false);
            if (tickBadge != null)
            {
                tickBadge.rectTransform.localScale = authoredTickScale;
                tickBadge.gameObject.SetActive(false);
            }
            ResetTimerVisual();
        }

        private Tween TrackLifecycle(Sequence sequence, uint revision, bool snapToRest)
        {
            if (sequence == null) return null;
            lifecycleTween = sequence;
            sequence.OnComplete(() =>
                {
                    if (revision != lifecycleRevision
                        || !ReferenceEquals(lifecycleTween, sequence)) return;
                    lifecycleTween = null;
                    presentationState.Dispatch(BsOrderCardTrigger.AnimationCompleted);
                    bool visible = presentationState.State == BsOrderCardState.Visible;
                    if (rt != null)
                    {
                        rt.localScale = authoredScale;
                        if (snapToRest && restPositionInitialized)
                            rt.anchoredPosition = restingAnchoredPosition;
                    }
                    SetCardLift(RestingCardLift);
                    CanonicalizeVisibility(visible);
                })
                .OnKill(() => ForgetLifecycleTween(sequence, revision));
            return sequence;
        }

        private Tween TrackPosePulse(Sequence sequence, uint revision)
        {
            if (sequence == null) return null;
            lifecycleTween = sequence;
            sequence.OnComplete(() =>
                {
                    if (revision != lifecycleRevision
                        || !ReferenceEquals(lifecycleTween, sequence)) return;
                    lifecycleTween = null;
                    if (rt != null) rt.localScale = authoredScale;
                    SetCardLift(RestingCardLift);
                    CanonicalizeVisibility(desiredVisible);
                })
                .OnKill(() => ForgetLifecycleTween(sequence, revision));
            return sequence;
        }

        private void TrackCompletionEdge(Sequence sequence, uint revision)
        {
            if (sequence == null) return;
            edgeTween = sequence;
            sequence.OnComplete(() => CompleteEdgeTween(sequence, revision))
                .OnKill(() => ForgetEdgeTween(sequence, revision));
        }

        private void PlayHangSwing()
        {
            if (hangSwing != null) hangSwing.Play();
        }

        private uint InvalidateLifecycleTweens()
        {
            if (hangSwing != null) hangSwing.Stop();
            lifecycleRevision++;
            Tween oldLifecycle = lifecycleTween;
            Tween oldVisibility = visibilityTween;
            lifecycleTween = null;
            visibilityTween = null;
            KillTween(oldLifecycle);
            KillTween(oldVisibility);
            return lifecycleRevision;
        }

        private uint InvalidateTickTween()
        {
            tickRevision++;
            Tween old = tickTween;
            tickTween = null;
            KillTween(old);
            return tickRevision;
        }

        private void CancelTickTween() => InvalidateTickTween();

        private uint InvalidateTimeBoostTween()
        {
            timeBoostRevision++;
            Tween old = timeBoostTween;
            timeBoostTween = null;
            KillTween(old);
            CanonicalizeTimeBoostFeedback();
            return timeBoostRevision;
        }

        private void CompleteTimeBoostTween(Tween tween, uint revision)
        {
            if (revision != timeBoostRevision
                || !ReferenceEquals(timeBoostTween, tween)) return;
            timeBoostTween = null;
            CanonicalizeTimeBoostFeedback();
        }

        private void ForgetTimeBoostTween(Tween tween, uint revision)
        {
            if (revision != timeBoostRevision
                || !ReferenceEquals(timeBoostTween, tween)) return;
            timeBoostTween = null;
            CanonicalizeTimeBoostFeedback();
        }

        private uint InvalidateEdgeTween()
        {
            edgeRevision++;
            Tween old = edgeTween;
            edgeTween = null;
            KillTween(old);
            return edgeRevision;
        }

        private void CancelEdgeTween()
        {
            InvalidateEdgeTween();
            CanonicalizeCompletionEdge();
        }

        private void CompleteEdgeTween(Tween tween, uint revision)
        {
            if (revision != edgeRevision || !ReferenceEquals(edgeTween, tween)) return;
            edgeTween = null;
            CanonicalizeCompletionEdge();
        }

        private void ForgetEdgeTween(Tween tween, uint revision)
        {
            if (revision != edgeRevision || !ReferenceEquals(edgeTween, tween)) return;
            edgeTween = null;
            CanonicalizeCompletionEdge();
        }

        private void CompleteVisibilityTween(Tween tween, uint revision, bool visible)
        {
            if (revision != lifecycleRevision
                || !ReferenceEquals(visibilityTween, tween)) return;
            visibilityTween = null;
            presentationState.Dispatch(BsOrderCardTrigger.AnimationCompleted);
            if (presentationState.State != (visible
                    ? BsOrderCardState.Visible
                    : BsOrderCardState.Hidden))
                presentationState.Dispatch(visible
                    ? BsOrderCardTrigger.ShowImmediate
                    : BsOrderCardTrigger.HideImmediate);
            CanonicalizeVisibility(visible);
        }

        private void ForgetVisibilityTween(Tween tween, uint revision)
        {
            if (revision == lifecycleRevision
                && ReferenceEquals(visibilityTween, tween)) visibilityTween = null;
        }

        private void ForgetLifecycleTween(Tween tween, uint revision)
        {
            if (revision == lifecycleRevision
                && ReferenceEquals(lifecycleTween, tween)) lifecycleTween = null;
        }

        private void CanonicalizePose()
        {
            if (rt != null)
            {
                rt.localScale = authoredScale;
                if (restPositionInitialized)
                    rt.anchoredPosition = restingAnchoredPosition;
            }
            SetCardLift(RestingCardLift);
        }

        private void CanonicalizeCompletionEdge()
        {
            if (edge != null)
            {
                edge.color = CompletionEdgeRestColor();
                edge.rectTransform.localScale = authoredEdgeScale;
            }
        }

        private Color CompletionEdgeRestColor() => highlighted
            ? WithAlpha(completionGlowColor, 0.16f)
            : Transparent(completionLineColor);

        private void CanonicalizeVisibility(bool visible)
        {
            if (canvasGroup == null) return;
            bool canRender = isActiveAndEnabled && gameObject.activeInHierarchy
                             && presentationState.State != BsOrderCardState.Disabled;
            canvasGroup.alpha = visible && canRender ? 1f : 0f;
            SetCanvasInteraction(false);
        }

        private void SetCanvasInteraction(bool interactive)
        {
            if (canvasGroup == null) return;
            canvasGroup.interactable = interactive;
            canvasGroup.blocksRaycasts = interactive;
        }

        private static void KillTween(Tween tween)
        {
            if (tween != null && tween.IsActive()) tween.Kill(false);
        }

        private static bool IsTweenActive(Tween tween) =>
            tween != null && tween.IsActive() && tween.IsPlaying();

        private static bool HasActiveTween(Tween first, Tween second) =>
            IsTweenActive(first) || IsTweenActive(second);

        private void ResetTimerVisual()
        {
            ClearPendingTimeBoostPresentation();
            shownTimerSecond = -1;
            ResetTimerMotion();
            SetTimerText(string.Empty);
            SetTimerProgress(1f);
            timerToneColor = timerNormalColor;
            ApplyTimerFeedbackColors();
        }

        private float PendingTimeBoostSeconds()
        {
            float total = 0f;
            for (int i = 0; i < pendingTimeBoosts.Count; i++)
                total += Mathf.Max(0f, pendingTimeBoosts[i].Seconds);
            return total;
        }

        private void ClearPendingTimeBoostPresentation()
        {
            pendingTimeBoosts.Clear();
        }

        private void ResetTimerMotion()
        {
            CancelTimerExpiredFeedback();
            urgentTimerKick = 0f;
            ApplyTimerMotion();
        }

        private void ApplyTimerMotion()
        {
            if (timerRoot == null) return;
            float scale = 1f + urgentTimerKick * 0.055f + timerExpiryPulse * 0.42f;
            float bumpX = timeBoostPulse * TimeBoostBumpX;
            float bumpY = timeBoostPulse * TimeBoostBumpY;
            timerRoot.localScale = new Vector3(
                authoredTimerScale.x * (scale + bumpX),
                authoredTimerScale.y * (scale + bumpY),
                authoredTimerScale.z);

            bool lift = bumpY != 0f;
            if (!lift && !timerBumpLifted) return;
            if (!timerBumpLifted) timerRestAnchoredPosition = timerRoot.anchoredPosition;
            timerBumpLifted = lift;
            float offset = bumpY * authoredTimerScale.y * timerRoot.rect.height
                           * (timerRoot.pivot.y - 0.5f);
            timerRoot.anchoredPosition = timerRestAnchoredPosition + new Vector2(0f, offset);
        }

        private void ApplyTimerFeedbackColors()
        {
            float flash = Mathf.Clamp01(timeBoostFlash);
            Color tone = Color.Lerp(timerToneColor, TimeBoostWarmColor, flash);
            if (timerText != null) timerText.color = tone;
            if (timerFill != null) timerFill.color = tone;
            if (timerClock != null)
                timerClock.color = Color.Lerp(
                    timerClockBaseColor, TimeBoostWarmColor, flash * 0.88f);
        }

        private void CanonicalizeTimeBoostFeedback()
        {
            timeBoostPulse = 0f;
            timeBoostFlash = 0f;
            timeBoostCountProgress = 1f;
            timeBoostCountSeconds = 0f;
            ApplyTimerMotion();
            ApplyTimerFeedbackColors();
            if (timeBoostFeedbackRoot == null) return;
            // SetOrder/OnDisable can cancel before the first boost. Capture the authored
            // offset before that first reset instead of replacing it with Vector2.zero.
            CaptureTimeBoostFeedbackPose();
            timeBoostFeedbackRoot.anchoredPosition = timeBoostFeedbackBasePosition;
            timeBoostFeedbackRoot.localScale = Vector3.one;
            if (timeBoostFeedbackCanvasGroup != null)
                timeBoostFeedbackCanvasGroup.alpha = 0f;
            timeBoostFeedbackRoot.gameObject.SetActive(false);
        }

        private static string FormatClock(int totalSeconds)
        {
            int safe = Mathf.Max(0, totalSeconds);
            int minutes = safe / 60;
            int seconds = safe - minutes * 60;
            return minutes.ToString("00") + ":" + seconds.ToString("00");
        }

        private void SetTimerText(string value)
        {
            if (timerText != null) timerText.text = value;
        }

        private void SetTimerProgress(float normalized)
        {
            if (timerFill == null) return;
            float value = Mathf.Clamp01(normalized);
            if (timerFill.type == Image.Type.Filled)
            {
                timerFill.fillAmount = value;
                return;
            }

            RectTransform fillRect = timerFill.rectTransform;
            if (!timerFillGeometryCaptured) CaptureTimerFillGeometry();
            float width = timerFillFullWidth * value;
            fillRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            Vector2 position = fillRect.anchoredPosition;
            position.x = timerFillLeftEdge + width * fillRect.pivot.x;
            fillRect.anchoredPosition = position;
        }

        private void CaptureTimeBoostFeedbackPose()
        {
            if (timeBoostFeedbackPoseCaptured || timeBoostFeedbackRoot == null) return;
            timeBoostFeedbackBasePosition = timeBoostFeedbackRoot.anchoredPosition;
            timeBoostFeedbackPoseCaptured = true;
        }

        private bool HasAuthoredTimeBoostFeedbackHierarchy() =>
            timerRoot != null && timeBoostFeedbackRoot != null
            && timeBoostFeedbackRoot.parent == timerRoot
            && timeBoostFeedbackCanvasGroup != null
            && timeBoostFeedbackCanvasGroup.gameObject
               == timeBoostFeedbackRoot.gameObject
            && (timeBoostFeedbackLabel != null
                || timeBoostFeedbackLegacyLabel != null)
            && (timeBoostFeedbackLabel == null
                || timeBoostFeedbackLabel.transform == timeBoostFeedbackRoot
                || timeBoostFeedbackLabel.transform.IsChildOf(timeBoostFeedbackRoot))
            && (timeBoostFeedbackLegacyLabel == null
                || timeBoostFeedbackLegacyLabel.transform == timeBoostFeedbackRoot
                || timeBoostFeedbackLegacyLabel.transform.IsChildOf(
                    timeBoostFeedbackRoot));

        private void CaptureTimerFillGeometry()
        {
            timerFillGeometryCaptured = false;
            if (timerFill == null) return;

            RectTransform fillRect = timerFill.rectTransform;
            float width = fillRect.rect.width;
            if (width <= 0f) width = Mathf.Abs(fillRect.sizeDelta.x);
            if (width <= 0f) return;

            timerFillFullWidth = width;
            timerFillLeftEdge = fillRect.anchoredPosition.x - width * fillRect.pivot.x;
            timerFillGeometryCaptured = true;
        }

        private void CaptureTimerArtState()
        {
            timerToneColor = timerFill != null ? timerFill.color : timerNormalColor;
            if (timerClock != null) timerClockBaseColor = timerClock.color;
            ApplyTimerFeedbackColors();
        }

        private void EnsureTimerVisuals()
        {
            if (HasAuthoredTimerHierarchy()) return;
            if (authoredTimerWarningIssued) return;

            authoredTimerWarningIssued = true;
            Debug.LogError(
                "Timer bindings invalid.", this);
        }

        private bool HasAuthoredTimerHierarchy() =>
            timerRoot != null && timerRoot.parent == transform
            && timerText != null
            && timerText.transform.IsChildOf(timerRoot)
            && (timerClock == null || timerClock.transform.IsChildOf(timerRoot))
            && HasAuthoredTimeBoostFeedbackHierarchy();

        private static bool SameOrder(OrderDef a, OrderDef b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Kind != b.Kind || a.Glass != b.Glass
                || !Mathf.Approximately(a.TimeLimit, b.TimeLimit))
                return false;

            int count = a.Contents != null ? a.Contents.Count : 0;
            if (count != (b.Contents != null ? b.Contents.Count : 0)) return false;
            for (int i = 0; i < count; i++)
                if (a.Contents[i] != b.Contents[i]) return false;
            return true;
        }

        private static Color WithAlpha(Color color, float alpha) =>
            new Color(color.r, color.g, color.b,
                Mathf.Clamp01(color.a * alpha));

        private static Color Transparent(Color c) => WithAlpha(c, 0f);
    }
}
