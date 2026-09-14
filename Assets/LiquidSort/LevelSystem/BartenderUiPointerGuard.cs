using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
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
