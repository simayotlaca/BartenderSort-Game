using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Dims and blocks the canvas outside one live UI target, without moving that target.</summary>
    [AddComponentMenu("")]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class BartenderTutorialFocusScrim : MaskableGraphic, ICanvasRaycastFilter
    {
        private RectTransform target;
        private RectTransform callout;
        private readonly Vector3[] corners = new Vector3[4];
        private Rect window;
        private EventSystem heldNavigation;
        private bool navigationWasEnabled;

        public void Focus(RectTransform targetRect, RectTransform calloutRect)
        {
            target = targetRect;
            callout = calloutRect;
            raycastTarget = true;
            heldNavigation = EventSystem.current;
            if (heldNavigation != null)
            {
                navigationWasEnabled = heldNavigation.sendNavigationEvents;
                heldNavigation.sendNavigationEvents = false;
                heldNavigation.SetSelectedGameObject(null);
            }
            RefreshWindow();
            SetVerticesDirty();
        }

        private void LateUpdate() => RefreshWindow();

        private void RefreshWindow()
        {
            Rect next = default;
            if (target != null && target.gameObject.activeInHierarchy)
            {
                target.GetWorldCorners(corners);
                Vector2 min = rectTransform.InverseTransformPoint(corners[0]);
                Vector2 max = min;
                for (int i = 1; i < corners.Length; i++)
                {
                    Vector2 point = rectTransform.InverseTransformPoint(corners[i]);
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
                next = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                if (callout != null)
                {
                    Vector3 position = callout.localPosition;
                    position.y = next.center.y;
                    callout.localPosition = position;
                }
            }
            if (window == next) return;
            window = next;
            SetVerticesDirty();
        }

        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera) =>
            target == null || !target.gameObject.activeInHierarchy
            || !RectTransformUtility.RectangleContainsScreenPoint(target, screenPoint, eventCamera);

        protected override void OnPopulateMesh(VertexHelper vertices)
        {
            vertices.Clear();
            Rect bounds = rectTransform.rect;
            if (window.width <= 0f || window.height <= 0f)
            {
                AddRect(vertices, bounds);
                return;
            }
            float left = Mathf.Clamp(window.xMin, bounds.xMin, bounds.xMax);
            float right = Mathf.Clamp(window.xMax, bounds.xMin, bounds.xMax);
            float bottom = Mathf.Clamp(window.yMin, bounds.yMin, bounds.yMax);
            float top = Mathf.Clamp(window.yMax, bounds.yMin, bounds.yMax);
            AddRect(vertices, Rect.MinMaxRect(bounds.xMin, bounds.yMin, left, bounds.yMax));
            AddRect(vertices, Rect.MinMaxRect(right, bounds.yMin, bounds.xMax, bounds.yMax));
            AddRect(vertices, Rect.MinMaxRect(left, bounds.yMin, right, bottom));
            AddRect(vertices, Rect.MinMaxRect(left, top, right, bounds.yMax));
        }

        private void AddRect(VertexHelper vertices, Rect rect)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;
            int first = vertices.currentVertCount;
            vertices.AddVert(new Vector3(rect.xMin, rect.yMin), color, Vector2.zero);
            vertices.AddVert(new Vector3(rect.xMin, rect.yMax), color, Vector2.zero);
            vertices.AddVert(new Vector3(rect.xMax, rect.yMax), color, Vector2.zero);
            vertices.AddVert(new Vector3(rect.xMax, rect.yMin), color, Vector2.zero);
            vertices.AddTriangle(first, first + 1, first + 2);
            vertices.AddTriangle(first, first + 2, first + 3);
        }

        protected override void OnDisable()
        {
            EventSystem navigation = heldNavigation;
            heldNavigation = null;
            if (navigation != null) navigation.sendNavigationEvents = navigationWasEnabled;
            base.OnDisable();
        }
    }
}
