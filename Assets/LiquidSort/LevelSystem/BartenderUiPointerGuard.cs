using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Raycasts UI immediately for raw input. It works before the input module updates, unlike
    /// EventSystem.IsPointerOverGameObject.
    /// </summary>
    internal static class BartenderUiPointerGuard
    {
        private static readonly List<RaycastResult> Results =
            new List<RaycastResult>(8);

        private static EventSystem cachedEventSystem;
        private static PointerEventData pointerData;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            cachedEventSystem = null;
            pointerData = null;
            Results.Clear();
        }

        public static bool IsPointerOverUi(Vector2 screenPosition, int pointerId)
        {
            EventSystem current = EventSystem.current;
            if (current == null) return false;

            if (pointerData == null || cachedEventSystem != current)
            {
                cachedEventSystem = current;
                pointerData = new PointerEventData(current);
            }
            else
            {
                pointerData.Reset();
            }

            pointerData.position = screenPosition;
            pointerData.pointerId = pointerId;
            Results.Clear();
            current.RaycastAll(pointerData, Results);

            // RaycastAll is front-to-back. Only the first valid target owns the tap, even across UI and
            // world raycasters.
            for (int i = 0; i < Results.Count; i++)
            {
                RaycastResult hit = Results[i];
                if (hit.gameObject == null || hit.module == null) continue;
                return hit.module is GraphicRaycaster;
            }
            return false;
        }
    }
}
