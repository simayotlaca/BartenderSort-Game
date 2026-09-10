using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Reveals the existing BARTENDER artwork one letter at a time. Shared slanted UV seams preserve
    /// the original wordmark exactly once settled; no replacement font or generated artwork is used.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    [AddComponentMenu("UI/Effects/Cheers Toast Letter Reveal")]
    public sealed class CheersToastLetterReveal : BaseMeshEffect
    {
        [SerializeField] private Animator sequenceAnimator;
        [SerializeField] private string animatorState = "Base Layer.Toast One Shot";
        [SerializeField] private float startTime = 0.10f;
        [SerializeField] private float stagger = 0.085f;
        [SerializeField] private float letterDuration = 0.34f;
        [SerializeField] private float pivotV = 0.74f;
        // X = bottom U, Y = top U in the full source image. These seams follow the tilted letters.
        [SerializeField] private Vector2[] seams = {
            new Vector2(0f, 0f), new Vector2(0.183f, 0.134f),
            new Vector2(0.343f, 0.224f), new Vector2(0.423f, 0.340f),
            new Vector2(0.429f, 0.457f), new Vector2(0.525f, 0.556f),
            new Vector2(0.636f, 0.666f), new Vector2(0.727f, 0.777f),
            new Vector2(0.796f, 0.878f), new Vector2(1f, 1f)
        };

        private Image titleImage;
        private int stateHash;
        private float lastTime = float.NaN;

        protected override void OnEnable()
        {
            titleImage = GetComponent<Image>();
            if (sequenceAnimator == null) sequenceAnimator = GetComponentInParent<Animator>();
            stateHash = Animator.StringToHash(animatorState);
            lastTime = float.NaN;
            base.OnEnable();
        }

        private float EndTime => startTime + Mathf.Max(0, seams.Length - 2) * stagger + letterDuration;

        private float ReadTime()
        {
            if (sequenceAnimator == null || !sequenceAnimator.isInitialized
                || sequenceAnimator.runtimeAnimatorController == null || sequenceAnimator.layerCount == 0)
                return 0f;
            AnimatorStateInfo state = sequenceAnimator.GetCurrentAnimatorStateInfo(0);
            return state.fullPathHash == stateHash ? Mathf.Clamp01(state.normalizedTime) * state.length : 0f;
        }

        private void LateUpdate()
        {
            if (seams == null || seams.Length < 2) return;
            float time = Mathf.Clamp(ReadTime(), 0f, EndTime);
            if (time == lastTime) return;
            lastTime = time;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        private static float Shape(float progress, float a, float b, float c, float d)
        {
            if (progress < 0.42f) return Mathf.Lerp(a, b, Mathf.SmoothStep(0f, 1f, progress / 0.42f));
            if (progress < 0.72f) return Mathf.Lerp(b, c, Mathf.SmoothStep(0f, 1f, (progress - 0.42f) / 0.30f));
            return Mathf.Lerp(c, d, Mathf.SmoothStep(0f, 1f, (progress - 0.72f) / 0.28f));
        }

        private static UIVertex Interpolate(UIVertex a, UIVertex b, float t)
        {
            UIVertex v = a;
            v.position = Vector3.LerpUnclamped(a.position, b.position, t);
            v.uv0 = Vector4.LerpUnclamped(a.uv0, b.uv0, t);
            v.uv1 = Vector4.LerpUnclamped(a.uv1, b.uv1, t);
            v.uv2 = Vector4.LerpUnclamped(a.uv2, b.uv2, t);
            v.uv3 = Vector4.LerpUnclamped(a.uv3, b.uv3, t);
            v.color = Color32.Lerp(a.color, b.color, t);
            return v;
        }

        private Rect FullArtworkRect()
        {
            Rect rect = titleImage.GetPixelAdjustedRect();
            Sprite sprite = titleImage.overrideSprite;
            if (sprite == null || !titleImage.preserveAspect || rect.width <= 0f || rect.height <= 0f)
                return rect;
            float aspect = sprite.rect.width / sprite.rect.height;
            Vector2 pivot = titleImage.rectTransform.pivot;
            if (aspect > rect.width / rect.height)
            {
                float height = rect.width / aspect;
                rect.y += (rect.height - height) * pivot.y;
                rect.height = height;
            }
            else
            {
                float width = rect.height * aspect;
                rect.x += (rect.width - width) * pivot.x;
                rect.width = width;
            }
            return rect;
        }

        private static void AddVertex(VertexHelper output, UIVertex vertex, Vector2 pivot,
            float sx, float sy, float dy, float angle, float alpha)
        {
            float x = (vertex.position.x - pivot.x) * sx;
            float y = (vertex.position.y - pivot.y) * sy;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            vertex.position = new Vector3(pivot.x + c * x - s * y,
                pivot.y + s * x + c * y + dy, vertex.position.z);
            Color32 color = vertex.color;
            color.a = (byte)Mathf.RoundToInt(color.a * alpha);
            vertex.color = color;
            output.AddVert(vertex);
        }

        public override void ModifyMesh(VertexHelper output)
        {
            if (!IsActive() || output.currentVertCount != 4 || seams == null || seams.Length < 2) return;
            if (titleImage == null) titleImage = GetComponent<Image>();
            if (titleImage.type != Image.Type.Simple || titleImage.useSpriteMesh) return;
            float time = ReadTime();
            if (time >= EndTime) return; // Original quad: exact artwork, no seams, no further rebuilding.

            UIVertex bl = default(UIVertex), tl = default(UIVertex), tr = default(UIVertex), br = default(UIVertex);
            output.PopulateUIVertex(ref bl, 0); output.PopulateUIVertex(ref tl, 1);
            output.PopulateUIVertex(ref tr, 2); output.PopulateUIVertex(ref br, 3);
            // A packed sprite may have transparent padding trimmed from its quad. Seam coordinates
            // still refer to the full artwork; interpolate the original vertices to retain atlas UVs.
            Rect full = FullArtworkRect();
            float quadWidth = br.position.x - bl.position.x;
            if (full.width <= 0f || full.height <= 0f || quadWidth <= 0f) return;
            float bottomV = (bl.position.y - full.yMin) / full.height;
            float topV = (tl.position.y - full.yMin) / full.height;
            float height = full.height;
            output.Clear();
            for (int i = 0; i < seams.Length - 1; i++)
            {
                float elapsed = time - startTime - i * stagger;
                if (elapsed <= 0f) continue;
                float progress = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, letterDuration));
                float sx = Shape(progress, 0.60f, 1.24f, 0.96f, 1f);
                float sy = Shape(progress, 0.60f, 1.16f, 1.04f, 1f);
                float dy = Shape(progress, -0.22f, 0.035f, -0.008f, 0f) * height;
                float angle = Shape(progress, -10f, 3f, -1f, 0f) * (i % 2 == 0 ? 1f : -1f) * Mathf.Deg2Rad;
                float alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / 0.04f));
                Vector2 left = seams[i], right = seams[i + 1];
                float centerU = (Mathf.Lerp(left.x, left.y, pivotV) + Mathf.Lerp(right.x, right.y, pivotV)) * 0.5f;
                Vector2 pivot = new Vector2(full.xMin + full.width * centerU, full.yMin + height * pivotV);
                float leftBottom = Mathf.Clamp01((full.xMin + full.width * Mathf.Lerp(left.x, left.y, bottomV) - bl.position.x) / quadWidth);
                float leftTop = Mathf.Clamp01((full.xMin + full.width * Mathf.Lerp(left.x, left.y, topV) - bl.position.x) / quadWidth);
                float rightTop = Mathf.Clamp01((full.xMin + full.width * Mathf.Lerp(right.x, right.y, topV) - bl.position.x) / quadWidth);
                float rightBottom = Mathf.Clamp01((full.xMin + full.width * Mathf.Lerp(right.x, right.y, bottomV) - bl.position.x) / quadWidth);
                int first = output.currentVertCount;
                AddVertex(output, Interpolate(bl, br, leftBottom), pivot, sx, sy, dy, angle, alpha);
                AddVertex(output, Interpolate(tl, tr, leftTop), pivot, sx, sy, dy, angle, alpha);
                AddVertex(output, Interpolate(tl, tr, rightTop), pivot, sx, sy, dy, angle, alpha);
                AddVertex(output, Interpolate(bl, br, rightBottom), pivot, sx, sy, dy, angle, alpha);
                output.AddTriangle(first, first + 1, first + 2);
                output.AddTriangle(first + 2, first + 3, first);
            }
        }
    }
}
