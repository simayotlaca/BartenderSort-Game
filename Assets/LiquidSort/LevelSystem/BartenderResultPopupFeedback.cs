using TMPro;
using UnityEngine;

namespace LiquidSort.Levels
{
    internal static class BartenderResultPopupFeedback
    {
        public static void SetMessage(ref TMP_Text label, RectTransform card,
                                      TMP_Text styleSource, string message)
        {
            bool visible = !string.IsNullOrWhiteSpace(message);
            if (label == null)
            {
                if (!visible || card == null) return;

                var feedbackObject = new GameObject("SaveFeedback", typeof(RectTransform),
                    typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                feedbackObject.layer = card.gameObject.layer;
                var rect = (RectTransform)feedbackObject.transform;
                rect.SetParent(card, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                // Both authored cards' bottom buttons end at -460 inside CardContent, which may
                // scale the artwork down. Keep the message just below them at full text size so
                // level/reward information and every action remain unobstructed.
                Transform content = card.Find("CardContent");
                float contentScale = content != null ? content.localScale.y : 1f;
                rect.anchoredPosition = new Vector2(0f, -(460f * contentScale + 40f));
                rect.sizeDelta = new Vector2(520f, 56f);

                label = feedbackObject.GetComponent<TextMeshProUGUI>();
                if (styleSource != null)
                {
                    label.font = styleSource.font;
                    label.fontSharedMaterial = styleSource.fontSharedMaterial;
                }
                label.color = Color.white;
                label.raycastTarget = false;
                label.alignment = TextAlignmentOptions.Center;
                label.enableAutoSizing = true;
                label.fontSize = 24f;
                label.fontSizeMin = 20f;
                label.fontSizeMax = 24f;
                label.textWrappingMode = TextWrappingModes.Normal;
                label.richText = false;
            }

            label.text = visible ? message : string.Empty;
            label.gameObject.SetActive(visible);
        }
    }
}
