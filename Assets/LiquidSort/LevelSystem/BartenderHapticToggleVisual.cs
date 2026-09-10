using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Figma's pill switch: the yellow handle exposes an On/Off track label.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Toggle))]
    public sealed class BartenderHapticToggleVisual : MonoBehaviour
    {
        [SerializeField] private RectTransform handle;
        [SerializeField] private Image track;
        [SerializeField] private TMP_Text stateLabel;
        [Tooltip("Off background. If empty, the track changes colour.")]
        [SerializeField] private Image offTrack;
        [Tooltip("Off artwork. Falls back to the TMP label.")]
        [SerializeField] private Image offLabel;
        [Tooltip("On artwork. Falls back to the TMP label.")]
        [SerializeField] private Image onLabel;
        [SerializeField] private Color enabledColor =
            new Color(0.10f, 0.35f, 0.86f, 1f);
        [SerializeField] private Color disabledColor =
            new Color(0.90f, 0.02f, 0.16f, 1f);
        [SerializeField] private float handleTravel = 35f;

        private Toggle toggle;

        private void Awake() => Resolve();

        private void OnEnable()
        {
            Resolve();
            if (toggle != null)
            {
                toggle.onValueChanged.RemoveListener(HandleValueChanged);
                toggle.onValueChanged.AddListener(HandleValueChanged);
            }
            Refresh();
        }

        private void OnDisable()
        {
            if (toggle != null)
                toggle.onValueChanged.RemoveListener(HandleValueChanged);
        }

        public void Refresh()
        {
            Resolve();
            if (toggle == null) return;

            bool enabled = toggle.isOn;
            bool hasExactOffArt = offTrack != null;
            bool hasExactOnArt = onLabel != null;
            if (track != null)
            {
                // Keep targetGraphic active in both states so the whole switch stays clickable.
                track.gameObject.SetActive(true);
                track.color = enabled
                    ? (hasExactOnArt ? Color.white : enabledColor)
                    : (hasExactOffArt ? Color.clear : disabledColor);
            }
            if (offTrack != null) offTrack.gameObject.SetActive(!enabled);
            if (onLabel != null)
            {
                onLabel.gameObject.SetActive(enabled);
                onLabel.rectTransform.anchoredPosition =
                    new Vector2(-handleTravel, 0f);
            }
            if (stateLabel != null)
            {
                bool useTmpLabel = enabled ? onLabel == null : offLabel == null;
                stateLabel.gameObject.SetActive(useTmpLabel);
                stateLabel.text = enabled ? "On" : "Off";
                RectTransform labelRect = stateLabel.rectTransform;
                labelRect.anchoredPosition = new Vector2(
                    enabled ? -handleTravel : handleTravel, 0f);
            }
            if (offLabel != null)
            {
                offLabel.gameObject.SetActive(!enabled);
                offLabel.rectTransform.anchoredPosition =
                    new Vector2(handleTravel, 0f);
            }
            if (handle != null)
                handle.anchoredPosition = new Vector2(
                    enabled ? handleTravel : -handleTravel, 0f);
        }

        private void Resolve()
        {
            if (toggle == null) toggle = GetComponent<Toggle>();
        }

        private void HandleValueChanged(bool _) => Refresh();
    }
}
