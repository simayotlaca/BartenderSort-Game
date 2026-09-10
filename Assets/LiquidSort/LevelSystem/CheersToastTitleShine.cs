using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Adds short glints inside the wordmark. The toast Animator supplies time so replay and seeking stay
    /// consistent.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    [AddComponentMenu("UI/Effects/Cheers Toast Title Shine")]
    public sealed class CheersToastTitleShine : MonoBehaviour, IMaterialModifier
    {
        [SerializeField] private Animator sequenceAnimator;
        [SerializeField] private string animatorState = "Base Layer.Toast One Shot";

        private static readonly int SpriteUvRectId = Shader.PropertyToID("_SpriteUVRect");
        private static readonly int ShinePositionId = Shader.PropertyToID("_ShinePosition");
        private static readonly int ShineAmountId = Shader.PropertyToID("_ShineAmount");
        private static readonly int FlashAmountId = Shader.PropertyToID("_FlashAmount");

        private Image titleImage;
        private Material instanceMaterial;
        private int animatorStateHash;
        private Vector3 lastEnvelope;
        private Vector4 lastSpriteUv;
        private bool propertiesInitialized;

        private void OnEnable()
        {
            titleImage = GetComponent<Image>();
            if (sequenceAnimator == null)
                sequenceAnimator = GetComponentInParent<Animator>();
            animatorStateHash = Animator.StringToHash(animatorState);
            propertiesInitialized = false;
            titleImage.SetMaterialDirty();
        }

        private void LateUpdate()
        {
            if (instanceMaterial != null) UpdateProperties(false);
        }

        public Material GetModifiedMaterial(Material baseMaterial)
        {
            if (!isActiveAndEnabled || baseMaterial == null
                || !baseMaterial.HasProperty(ShineAmountId))
                return baseMaterial;

            if (titleImage == null) titleImage = GetComponent<Image>();
            if (instanceMaterial == null || instanceMaterial.shader != baseMaterial.shader)
            {
                ReleaseMaterial();
                instanceMaterial = new Material(baseMaterial)
                {
                    name = "CheersToastTitleShine (Instance)",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }
            else
            {
                // Image runs first in component order. Copy its current stencil material and mask keywords
                // whenever uGUI rebuilds them.
                instanceMaterial.CopyPropertiesFromMaterial(baseMaterial);
            }

            UpdateProperties(true);
            return instanceMaterial;
        }

        private void UpdateProperties(bool force)
        {
            Vector3 envelope = Evaluate(ReadSequenceTime());
            Sprite sprite = titleImage.overrideSprite;
            Vector4 spriteUv = sprite != null
                ? DataUtility.GetOuterUV(sprite)
                : new Vector4(0f, 0f, 1f, 1f);
            if (!force && propertiesInitialized
                && envelope == lastEnvelope && spriteUv == lastSpriteUv) return;

            instanceMaterial.SetVector(SpriteUvRectId, spriteUv);
            instanceMaterial.SetFloat(ShinePositionId, envelope.x);
            instanceMaterial.SetFloat(ShineAmountId, envelope.y);
            instanceMaterial.SetFloat(FlashAmountId, envelope.z);
            lastEnvelope = envelope;
            lastSpriteUv = spriteUv;
            propertiesInitialized = true;
        }

        private float ReadSequenceTime()
        {
            if (sequenceAnimator == null || !sequenceAnimator.isInitialized
                || sequenceAnimator.runtimeAnimatorController == null
                || sequenceAnimator.layerCount == 0) return 0f;

            AnimatorStateInfo state = sequenceAnimator.GetCurrentAnimatorStateInfo(0);
            return state.fullPathHash == animatorStateHash
                ? Mathf.Clamp01(state.normalizedTime) * state.length
                : 0f;
        }

        /// <summary>
        /// X is glint position, Y glint strength, Z white flash. The browser preview shares this timing
        /// formula.
        /// </summary>
        public static Vector3 Evaluate(float timeSeconds)
        {
            Vector2 shine = Sweep(timeSeconds, 1.60f, 1.95f);
            if (shine.y == 0f) shine = Sweep(timeSeconds, 2.50f, 2.88f);

            float flash = Flash(timeSeconds, 1.60f, 0.22f)
                + Flash(timeSeconds, 2.50f, 0.50f)
                + Flash(timeSeconds, BartenderCheersSequence.ContactTime, 0.20f);
            return new Vector3(shine.x, shine.y, Mathf.Clamp01(flash));
        }

        private static Vector2 Sweep(float time, float start, float end)
        {
            if (time <= start || time >= end) return new Vector2(-0.3f, 0f);
            float progress = (time - start) / (end - start);
            return new Vector2(-0.3f + 1.6f * progress,
                0.42f * Mathf.Sin(Mathf.PI * progress));
        }

        private static float Flash(float time, float start, float strength)
        {
            float elapsed = time - start;
            if (elapsed < 0f || elapsed >= 0.24f) return 0f;
            float tail = 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.18f, 0.24f, elapsed));
            return strength * Mathf.Exp(-elapsed / 0.06f) * tail;
        }

        private void OnDisable()
        {
            if (titleImage != null) titleImage.SetMaterialDirty();
            ReleaseMaterial();
        }

        private void OnDestroy() => ReleaseMaterial();

        private void ReleaseMaterial()
        {
            Material material = instanceMaterial;
            instanceMaterial = null;
            propertiesInitialized = false;
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            animatorStateHash = Animator.StringToHash(animatorState);
            propertiesInitialized = false;
            if (titleImage != null) titleImage.SetMaterialDirty();
        }
#endif
    }
}
