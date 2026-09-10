using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Adds a short surface wave to the toast liquid. LiquidSlosh moves the whole image; this bends only the
    /// top, keeping the bottom and mask fixed.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    [AddComponentMenu("UI/Effects/Cheers Toast Liquid Wave")]
    public sealed class CheersToastLiquidWave : BaseMeshEffect
    {
        [Header("Authored sequence clock")]
        [SerializeField] private Animator sequenceAnimator;
        [SerializeField] private string animatorState = "Base Layer.Toast One Shot";
        [Tooltip("Enable only when clipTimeSeconds has a linear 0-to-duration curve in the toast clip. This also supports Animation-window sampling.")]
        [SerializeField] private bool useAuthoredClipTime;
        [SerializeField, Min(0f)] private float clipTimeSeconds;
        [SerializeField, Min(0f)] private float contactTime = BartenderCheersSequence.ContactTime;
        [SerializeField, Range(0f, 0.08f)] private float responseDelay = 0.025f;
        [SerializeField, Range(0.2f, 0.6f)] private float settlingDuration = 0.4f;

        [Header("Surface in the full sprite canvas (bottom = 0)")]
        [SerializeField, Range(0f, 1f)] private float surfaceV = 0.73f;
        [SerializeField, Range(0.08f, 0.4f)] private float deformationDepth = 0.26f;
        [SerializeField, Range(0f, 1f)] private float surfaceCenterU = 0.5f;
        [SerializeField, Range(0.2f, 1f)] private float surfaceWidthU = 0.55f;
        [Tooltip("Wave height as a fraction of the full liquid sprite canvas height.")]
        [SerializeField, Range(0f, 0.04f)] private float amplitude = 0.025f;
        [Tooltip("Use +1 for the cocktail and -1 for the mug to mirror the impact response.")]
        [SerializeField, Range(-1f, 1f)] private float direction = 1f;
        [SerializeField, Range(0.5f, 1.5f)] private float oscillations = 1.1f;

        [Header("Mesh resolution")]
        [SerializeField, Range(8, 40)] private int columns = 24;
        [SerializeField, Range(8, 32)] private int rows = 20;

        private Image liquidImage;
        private int animatorStateHash;
        private Vector2 lastWave;

        public float SequenceTimeSeconds => ReadSequenceTime();

        /// <summary>The surface's vertical offset in full-sprite UV units.</summary>
        public float EvaluateSurfaceDisplacementUV(float u)
        {
            Vector2 wave = EvaluateWave(ReadSequenceTime());
            float phase = (u - surfaceCenterU) / Mathf.Max(0.01f, surfaceWidthU) * Mathf.PI;
            return wave.x * Mathf.Sin(phase) + wave.y * Mathf.Cos(2f * phase);
        }

        protected override void OnEnable()
        {
            liquidImage = GetComponent<Image>();
            if (sequenceAnimator == null)
                sequenceAnimator = GetComponentInParent<Animator>();
            animatorStateHash = Animator.StringToHash(animatorState);
            lastWave = new Vector2(float.NaN, float.NaN);
            base.OnEnable();
        }

        private void LateUpdate()
        {
            Vector2 wave = EvaluateWave(ReadSequenceTime());
            // Stop rebuilding the UI mesh once the wave settles.
            if (wave.x == lastWave.x && wave.y == lastWave.y) return;
            lastWave = wave;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        private float ReadSequenceTime()
        {
            if (useAuthoredClipTime) return clipTimeSeconds;
            if (sequenceAnimator == null || !sequenceAnimator.isInitialized ||
                sequenceAnimator.runtimeAnimatorController == null || sequenceAnimator.layerCount == 0)
                return 0f;

            AnimatorStateInfo state = sequenceAnimator.GetCurrentAnimatorStateInfo(0);
            if (state.fullPathHash != animatorStateHash) return 0f;

            // Read Animator time so replay, pause, speed changes and explicit seeks all follow the same clip.
            return Mathf.Clamp01(state.normalizedTime) * state.length;
        }

        private Vector2 EvaluateWave(float sequenceTime)
        {
            float elapsed = sequenceTime - contactTime - responseDelay;
            float duration = Mathf.Max(0.01f, settlingDuration);
            if (elapsed <= 0f || elapsed >= duration || amplitude <= 0f)
                return Vector2.zero;

            float progress = elapsed / duration;
            float attack = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / 0.025f));
            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 1f, progress));
            float envelope = 1.6f * attack * Mathf.Exp(-6f * progress) * fade;
            float phase = progress * oscillations * 2f * Mathf.PI;
            float strength = amplitude * direction * envelope;
            return new Vector2(Mathf.Cos(phase) * strength, Mathf.Sin(phase) * strength * 0.35f);
        }

        public override void ModifyMesh(VertexHelper vertices)
        {
            if (!IsActive() || vertices.currentVertCount != 4) return;
            if (liquidImage == null) liquidImage = GetComponent<Image>();
            // Only Simple Image quads are supported. Leave other sprite geometry unchanged.
            if (liquidImage == null || liquidImage.type != Image.Type.Simple || liquidImage.useSpriteMesh)
                return;

            Vector2 wave = EvaluateWave(ReadSequenceTime());
            if (wave.x == 0f && wave.y == 0f) return;

            UIVertex bottomLeft = default;
            UIVertex topLeft = default;
            UIVertex topRight = default;
            UIVertex bottomRight = default;
            // Follow Image.GenerateSimpleSprite's vertex order to keep its UVs, padding, colour and other
            // channels.
            vertices.PopulateUIVertex(ref bottomLeft, 0);
            vertices.PopulateUIVertex(ref topLeft, 1);
            vertices.PopulateUIVertex(ref topRight, 2);
            vertices.PopulateUIVertex(ref bottomRight, 3);

            Rect canvasRect = liquidImage.GetPixelAdjustedRect();
            if (canvasRect.width <= 0f || canvasRect.height <= 0f) return;
            int columnCount = Mathf.Clamp(columns, 8, 40);
            int rowCount = Mathf.Clamp(rows, 8, 32);
            float stableV = surfaceV - Mathf.Max(0.01f, deformationDepth);
            float span = Mathf.Max(0.01f, surfaceWidthU);
            vertices.Clear();

            for (int row = 0; row <= rowCount; row++)
            {
                float y = (float)row / rowCount;
                UIVertex left = Lerp(bottomLeft, topLeft, y);
                UIVertex right = Lerp(bottomRight, topRight, y);
                for (int column = 0; column <= columnCount; column++)
                {
                    UIVertex vertex = Lerp(left, right, (float)column / columnCount);
                    // Use the full canvas so Inspector surface coordinates match the PNG.
                    float u = (vertex.position.x - canvasRect.xMin) / canvasRect.width;
                    float v = (vertex.position.y - canvasRect.yMin) / canvasRect.height;
                    float influence = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(stableV, surfaceV, v));
                    float spatialPhase = (u - surfaceCenterU) / span * Mathf.PI;
                    float displacement = wave.x * Mathf.Sin(spatialPhase) + wave.y * Mathf.Cos(2f * spatialPhase);
                    vertex.position.y += displacement * influence * canvasRect.height;
                    vertices.AddVert(vertex);
                }
            }

            int stride = columnCount + 1;
            for (int row = 0; row < rowCount; row++)
            {
                for (int column = 0; column < columnCount; column++)
                {
                    int bottom = row * stride + column;
                    // Match uGUI's original clockwise triangle winding.
                    vertices.AddTriangle(bottom, bottom + stride, bottom + stride + 1);
                    vertices.AddTriangle(bottom + stride + 1, bottom + 1, bottom);
                }
            }
        }

        private static UIVertex Lerp(UIVertex a, UIVertex b, float t)
        {
            return new UIVertex
            {
                position = Vector3.LerpUnclamped(a.position, b.position, t),
                color = Color32.Lerp(a.color, b.color, t),
                uv0 = Vector4.LerpUnclamped(a.uv0, b.uv0, t),
                uv1 = Vector4.LerpUnclamped(a.uv1, b.uv1, t),
                uv2 = Vector4.LerpUnclamped(a.uv2, b.uv2, t),
                uv3 = Vector4.LerpUnclamped(a.uv3, b.uv3, t),
                normal = Vector3.LerpUnclamped(a.normal, b.normal, t),
                tangent = Vector4.LerpUnclamped(a.tangent, b.tangent, t)
            };
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            animatorStateHash = Animator.StringToHash(animatorState);
            base.OnValidate();
        }
#endif
    }
}
