using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>UI sounds for Buttons, Sliders and Toggles, attached in their scene or prefab.</summary>
    [AddComponentMenu("Liquid Sort/UI/Button Sound")]
    [DisallowMultipleComponent]
    public sealed class BsButtonSound : MonoBehaviour, IPointerDownHandler,
                                        IPointerUpHandler
    {
        [Header("Button")]
        public BsSfx ClickSound = BsSfx.ButtonClick;
        public bool EnableClickSound = true;

        [Header("Slider")]
        public BsSfx SliderSound = BsSfx.SliderTick;
        [Min(0f)] public float SliderThrottle = 0.08f;
        public bool EnableSliderSound = true;

        [Header("Toggle")]
        public BsSfx ToggleOnSound = BsSfx.ToggleOn;
        public BsSfx ToggleOffSound = BsSfx.ToggleOff;
        public bool EnableToggleSound = true;

        [Header("Pitch")]
        [Range(0f, 0.3f)] public float PitchVariation = 0.05f;

        private Button button;
        private Slider slider;
        private Toggle toggle;
        private float lastSliderSoundTime = -999f;
        private bool ignoreNextToggle;
        private bool pointerClickPending;
        private bool clearPointerClickInLateUpdate;
        private int lastPointerReleaseFrame = -1;

        private void Awake()
        {
            button = GetComponent<Button>();
            slider = GetComponent<Slider>();
            toggle = GetComponent<Toggle>();

            if (button != null) button.onClick.AddListener(HandleButtonClicked);
            if (slider != null) slider.onValueChanged.AddListener(HandleSliderChanged);
            if (toggle != null)
            {
                ignoreNextToggle = true;
                toggle.onValueChanged.AddListener(HandleToggleChanged);
            }
        }

        private void Start() => ignoreNextToggle = false;

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(HandleButtonClicked);
            if (slider != null) slider.onValueChanged.RemoveListener(HandleSliderChanged);
            if (toggle != null) toggle.onValueChanged.RemoveListener(HandleToggleChanged);
        }

        private void OnDisable()
        {
            pointerClickPending = false;
            clearPointerClickInLateUpdate = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!EnableClickSound || button == null || !button.interactable
                || slider != null || toggle != null)
                return;

            pointerClickPending = true;
            clearPointerClickInLateUpdate = false;
            lastPointerReleaseFrame = -1;
            Play(ClickSound);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!pointerClickPending) return;
            lastPointerReleaseFrame = Time.frameCount;
            clearPointerClickInLateUpdate = true;
        }

        private void LateUpdate()
        {
            if (!clearPointerClickInLateUpdate) return;
            pointerClickPending = false;
            clearPointerClickInLateUpdate = false;
            lastPointerReleaseFrame = -1;
        }

        private void HandleButtonClicked()
        {
            if (pointerClickPending || lastPointerReleaseFrame == Time.frameCount)
            {
                pointerClickPending = false;
                clearPointerClickInLateUpdate = false;
                lastPointerReleaseFrame = -1;
                return;
            }
            if (!EnableClickSound || button == null || slider != null || toggle != null)
                return;
            Play(ClickSound);
        }

        private void HandleSliderChanged(float _)
        {
            if (!EnableSliderSound || slider == null || !slider.interactable) return;

            float now = Time.unscaledTime;
            if (now - lastSliderSoundTime < SliderThrottle) return;
            lastSliderSoundTime = now;
            Play(SliderSound);
        }

        private void HandleToggleChanged(bool isOn)
        {
            if (!EnableToggleSound || ignoreNextToggle
                || toggle == null || !toggle.interactable)
                return;
            Play(isOn ? ToggleOnSound : ToggleOffSound);
        }

        private void Play(BsSfx sfx)
        {
            float pitch = 1f + Random.Range(-PitchVariation, PitchVariation);
            BsAudio.UI(sfx, pitch);
        }
    }
}
