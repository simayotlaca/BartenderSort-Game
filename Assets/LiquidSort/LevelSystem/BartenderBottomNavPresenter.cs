using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Updates the selected tab, marker and icons in one frame. World-to-local positioning supports
    /// reordered tabs; each tab's BsButtonSound owns its audio.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderBottomNavPresenter : MonoBehaviour
    {
        private const float FallbackSelectedScale = 1.35f;

        [Header("Authored navigation hierarchy")]
        [SerializeField] private RectTransform marker;
        [Tooltip("Tab order used by selection APIs. Keep arrays in the same order.")]
        [SerializeField] private Button[] tabs = new Button[0];
        [Tooltip("Optional label graphic for each tab; use null entries when a tab has none.")]
        [SerializeField] private Graphic[] labels = new Graphic[0];
        [Tooltip("The icon/visual RectTransform moved and resized for each tab.")]
        [SerializeField] private RectTransform[] tabVisuals = new RectTransform[0];

        private RectTransform navRect;
        // Tabs that animate targetGraphic without a separate icon child.
        private List<Graphic> shrinkCompensated;
        // Keep tap areas at least as large as their authored size.
        private Vector2[] authoredTabSizes;
        [Tooltip("Purple base plates outside the selected tab.")]
        [SerializeField] private RectTransform[] basePlates = new RectTransform[0];
        private RectTransform[] baseAtTab;
        private Vector2[] visualRestPositions;
        private Vector2[] visualRestSizes;
        private float selectedLift;
        private float selectedScale = FallbackSelectedScale;
        private bool selectionGeometryCaptured;
        private bool baseMappingCaptured;
        private int selectedIndex = -1;
        private bool resolved;
        private bool bindingsValid;
        private bool bindingErrorReported;

        private void OnEnable()
        {
            Resolve();
            if (bindingsValid) SnapToSelectedOrCenter();
        }

        private void OnDisable()
        {
            if (bindingsValid) SnapToSelectedOrCenter();
        }

        // Resolve references.

        private void Resolve()
        {
            if (resolved) return;
            resolved = true;
            bindingsValid = false;
            navRect = transform as RectTransform;
            int count = tabs != null ? tabs.Length : 0;
            string error = null;
            if (navRect == null) error = "presenter root RectTransform değil";
            else if (marker == null) error = "Selection Marker bağlı değil";
            else if (count == 0) error = "Tabs dizisi boş";
            else if (tabVisuals == null || tabVisuals.Length != count)
                error = "Tab Visuals dizisi Tabs ile aynı uzunlukta değil";
            else if (labels == null || labels.Length != count)
                error = "Labels dizisi Tabs ile aynı uzunlukta değil";
            else if (basePlates == null || basePlates.Length == 0)
                error = "Base Plates dizisi boş";

            shrinkCompensated = null;
            for (int i = 0; i < count; i++)
            {
                if (tabs == null || tabVisuals == null || i >= tabVisuals.Length) break;
                if (tabs[i] == null)
                {
                    error = error ?? $"Tab {i} Button bağlı değil";
                    continue;
                }
                if (tabVisuals[i] == null)
                {
                    error = error ?? $"Tab {i} Visual bağlı değil";
                    continue;
                }

                Graphic target = tabs[i].targetGraphic;
                if (target == null || target.rectTransform != tabVisuals[i]) continue;
                shrinkCompensated ??= new List<Graphic>();
                shrinkCompensated.Add(target);
            }

            if (error == null)
            {
                bindingsValid = true;
                return;
            }
            if (!bindingErrorReported)
            {
                bindingErrorReported = true;
                Debug.LogError(
                    "Bottom Navigation authored binding error: " + error + ".", this);
            }
        }

        /// <summary>
        /// Use unselected tabs for rest size and the selected tab for its lift/scale. Its enlarged Home pose
        /// is not the baseline.
        /// </summary>
        private void CaptureSelectionGeometry(int authoredSelectedIndex)
        {
            if (selectionGeometryCaptured || tabVisuals == null) return;
            int count = tabVisuals.Length;
            visualRestPositions = new Vector2[count];
            visualRestSizes = new Vector2[count];
            authoredTabSizes = new Vector2[count];

            for (int i = 0; i < count; i++)
            {
                if (tabVisuals[i] == null) continue;
                visualRestPositions[i] = tabVisuals[i].anchoredPosition;
                visualRestSizes[i] = tabVisuals[i].sizeDelta;
                authoredTabSizes[i] = tabVisuals[i].sizeDelta;
            }

            RectTransform selected = authoredSelectedIndex >= 0
                                     && authoredSelectedIndex < count
                ? tabVisuals[authoredSelectedIndex]
                : null;
            if (selected == null) return;

            float restY = MedianVisualValue(authoredSelectedIndex, true, false);
            float restWidth = MedianVisualValue(authoredSelectedIndex, false, true);
            float restHeight = MedianVisualValue(authoredSelectedIndex, false, false);

            selectedLift = selected.anchoredPosition.y - restY;
            float widthScale = Mathf.Abs(restWidth) > 0.01f
                ? Mathf.Abs(selected.sizeDelta.x / restWidth)
                : 0f;
            float heightScale = Mathf.Abs(restHeight) > 0.01f
                ? Mathf.Abs(selected.sizeDelta.y / restHeight)
                : 0f;

            bool authoredSizeExpanded = widthScale > 1.01f || heightScale > 1.01f;
            bool authoredPositionLifted = Mathf.Abs(selectedLift) > 0.5f;

            if (widthScale > 1.01f && heightScale > 1.01f)
                selectedScale = (widthScale + heightScale) * 0.5f;
            else if (heightScale > 1.01f)
                selectedScale = heightScale;
            else if (widthScale > 1.01f)
                selectedScale = widthScale;

            if (!authoredPositionLifted)
                selectedLift = Mathf.Abs(restHeight) * (selectedScale - 1f) * 0.4f;

            // Without a saved small icon pose, undo only the scale or lift actually present on the selected
            // icon.
            if (authoredPositionLifted)
                visualRestPositions[authoredSelectedIndex] = new Vector2(
                    selected.anchoredPosition.x, restY);
            if (authoredSizeExpanded)
                visualRestSizes[authoredSelectedIndex] = selected.sizeDelta / selectedScale;

            selectionGeometryCaptured = true;
            CompensateShrunkHitAreas();
        }

        private float MedianVisualValue(int excludedIndex, bool positionY, bool sizeX)
        {
            int count = 0;
            for (int i = 0; i < tabVisuals.Length; i++)
                if (i != excludedIndex && tabVisuals[i] != null) count++;

            if (count == 0)
            {
                RectTransform fallback = tabVisuals[excludedIndex];
                if (positionY) return fallback.anchoredPosition.y;
                return sizeX ? fallback.sizeDelta.x : fallback.sizeDelta.y;
            }

            float[] values = new float[count];
            int cursor = 0;
            for (int i = 0; i < tabVisuals.Length; i++)
            {
                RectTransform visual = tabVisuals[i];
                if (i == excludedIndex || visual == null) continue;
                values[cursor++] = positionY
                    ? visual.anchoredPosition.y
                    : (sizeX ? visual.sizeDelta.x : visual.sizeDelta.y);
            }

            Array.Sort(values);
            int middle = values.Length / 2;
            return values.Length % 2 == 0
                ? (values[middle - 1] + values[middle]) * 0.5f
                : values[middle];
        }

        private void CaptureBaseMapping(int authoredSelectedIndex)
        {
            if (baseMappingCaptured || tabs == null) return;
            baseMappingCaptured = true;
            baseAtTab = new RectTransform[tabs.Length];

            for (int plateIndex = 0; plateIndex < basePlates.Length; plateIndex++)
            {
                RectTransform plate = basePlates[plateIndex];
                if (plate == null) continue;

                int closestTab = -1;
                float closestDistance = float.MaxValue;
                for (int tabIndex = 0; tabIndex < tabs.Length; tabIndex++)
                {
                    if (tabIndex == authoredSelectedIndex || tabs[tabIndex] == null
                        || baseAtTab[tabIndex] != null)
                        continue;

                    float distance = Mathf.Abs(WorldCenterX(plate)
                                               - WorldCenterX(tabs[tabIndex].transform));
                    if (distance >= closestDistance) continue;
                    closestDistance = distance;
                    closestTab = tabIndex;
                }

                if (closestTab >= 0) baseAtTab[closestTab] = plate;
            }
        }

        /// <summary>Moves the new tab's purple base to the old slot in the same frame.</summary>
        private void MoveBasePlate(int incomingIndex, int outgoingIndex)
        {
            if (incomingIndex == outgoingIndex || outgoingIndex < 0 || baseAtTab == null
                || incomingIndex < 0 || incomingIndex >= baseAtTab.Length
                || outgoingIndex >= baseAtTab.Length)
                return;

            RectTransform moving = baseAtTab[incomingIndex];
            if (moving == null) return;

            baseAtTab[incomingIndex] = null;
            baseAtTab[outgoingIndex] = moving;
            float targetX = AnchoredXForTab(moving, tabs[outgoingIndex]);
            Vector2 position = moving.anchoredPosition;
            position.x = targetX;
            moving.anchoredPosition = position;
        }

        private void SnapBasePlatesToMappedSlots()
        {
            if (baseAtTab == null) return;
            for (int i = 0; i < baseAtTab.Length; i++)
            {
                RectTransform plate = baseAtTab[i];
                if (plate == null || tabs[i] == null) continue;
                Vector2 position = plate.anchoredPosition;
                position.x = AnchoredXForTab(plate, tabs[i]);
                plate.anchoredPosition = position;
            }
        }

        private void ApplySelectionVisuals(int index)
        {
            if (tabVisuals == null) return;
            CaptureSelectionGeometry(index);

            for (int i = 0; i < tabVisuals.Length; i++)
            {
                bool on = i == index;

                if (labels != null && i < labels.Length && labels[i] != null)
                {
                    float alpha = on ? 1f : 0f;
                    Color color = labels[i].color;
                    color.a = alpha;
                    labels[i].color = color;
                }

                RectTransform visual = tabVisuals[i];
                if (visual == null || visualRestPositions == null || visualRestSizes == null)
                    continue;

                Vector2 targetPosition = visualRestPositions[i];
                Vector2 targetSize = visualRestSizes[i];
                if (on)
                {
                    targetPosition.y += selectedLift;
                    targetSize *= selectedScale;
                }

                visual.anchoredPosition = targetPosition;
                visual.sizeDelta = targetSize;
            }
        }

        /// <summary>
        /// Keeps tap areas at authored size when <see cref="tabVisuals"/> uses the button rectangle itself.
        /// Separate icon children need no change.
        /// </summary>
        private void CompensateShrunkHitAreas()
        {
            if (shrinkCompensated == null || tabVisuals == null
                || authoredTabSizes == null || visualRestSizes == null) return;
            for (int i = 0; i < tabVisuals.Length; i++)
            {
                RectTransform visual = tabVisuals[i];
                if (visual == null) continue;
                Graphic graphic = tabs[i] != null ? tabs[i].targetGraphic : null;
                if (graphic == null || !shrinkCompensated.Contains(graphic)) continue;

                // Negative padding expands the raycast area. Split the missing authored size evenly across
                // both edges.
                Vector2 authored = authoredTabSizes[i];
                Vector2 rest = visualRestSizes[i];
                float padX = Mathf.Max(0f, (authored.x - rest.x) * 0.5f);
                float padY = Mathf.Max(0f, (authored.y - rest.y) * 0.5f);
                if (padX <= 0f && padY <= 0f) continue;
                graphic.raycastPadding = new Vector4(-padX, -padY, -padX, -padY);
            }
        }

        // Tab selection.

        /// <summary>Selects a tab and moves the marker and icons to it.</summary>
        public void Select(int index)
        {
            Resolve();
            if (!bindingsValid) return;
            if (tabs == null || index < 0 || index >= tabs.Length) return;
            if (index == selectedIndex) return;

            int outgoingIndex = selectedIndex;
            selectedIndex = index;
            MoveMarkerTo(index);
            MoveBasePlate(index, outgoingIndex);
            ApplySelectionVisuals(index);
        }

        /// <summary>Selects by Button reference so callers do not depend on tab order.</summary>
        public bool SelectTab(Button tab)
        {
            if (tab == null) return false;
            Resolve();
            if (!bindingsValid || tabs == null) return false;

            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i] != tab) continue;
                Select(i);
                return true;
            }

            return false;
        }

        private void SnapToSelectedOrCenter()
        {
            if (tabs == null || tabs.Length == 0) return;

            int nearest = selectedIndex >= 0 && selectedIndex < tabs.Length
                ? selectedIndex
                : 0;

            // At startup, select the tab nearest the marker. Without a marker, use the largest visual.
            if (selectedIndex < 0 && marker != null)
            {
                float best = float.MaxValue;
                float markerX = marker.anchoredPosition.x;
                for (int i = 0; i < tabs.Length; i++)
                {
                    if (tabs[i] == null) continue;
                    float distance = Mathf.Abs(LocalCenterX(tabs[i]) - markerX);
                    if (distance >= best) continue;
                    best = distance;
                    nearest = i;
                }
            }
            else if (selectedIndex < 0 && tabVisuals != null)
            {
                float largestArea = float.MinValue;
                for (int i = 0; i < tabVisuals.Length; i++)
                {
                    if (tabVisuals[i] == null) continue;
                    Vector2 size = tabVisuals[i].rect.size;
                    float area = Mathf.Abs(size.x * size.y);
                    if (area <= largestArea) continue;
                    largestArea = area;
                    nearest = i;
                }
            }

            selectedIndex = nearest;
            CaptureBaseMapping(nearest);
            SnapBasePlatesToMappedSlots();
            MoveMarkerTo(nearest);
            ApplySelectionVisuals(nearest);
        }

        private void MoveMarkerTo(int index)
        {
            if (marker == null || tabs[index] == null) return;
            float targetX = LocalCenterX(tabs[index]);
            Vector2 snapped = marker.anchoredPosition;
            snapped.x = targetX;
            marker.anchoredPosition = snapped;
        }

        /// <summary>Converts the tab's world centre to local x so changed anchors or tab counts still work.</summary>
        private float LocalCenterX(Button tab)
        {
            var rect = tab.transform as RectTransform;
            if (rect == null || navRect == null) return 0f;
            Vector3 world = rect.TransformPoint(rect.rect.center);
            return navRect.InverseTransformPoint(world).x;
        }

        private static float WorldCenterX(Transform target)
        {
            if (target is RectTransform rect)
                return rect.TransformPoint(rect.rect.center).x;
            return target != null ? target.position.x : 0f;
        }

        /// <summary>Converts a tab centre to the moving base's anchor-space x, including nested bases.</summary>
        private static float AnchoredXForTab(RectTransform moving, Button tab)
        {
            if (moving == null || tab == null) return 0f;
            RectTransform parent = moving.parent as RectTransform;
            RectTransform tabRect = tab.transform as RectTransform;
            if (parent == null || tabRect == null) return moving.anchoredPosition.x;

            Vector3 currentWorld = moving.TransformPoint(moving.rect.center);
            Vector3 targetWorld = tabRect.TransformPoint(tabRect.rect.center);
            float currentLocalX = parent.InverseTransformPoint(currentWorld).x;
            float targetLocalX = parent.InverseTransformPoint(targetWorld).x;
            return moving.anchoredPosition.x + targetLocalX - currentLocalX;
        }
    }
}
