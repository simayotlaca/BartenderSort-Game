using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Draws a liquid ribbon on impact. Place its Image under Pose, before LiquidMask and FrontImage, so the
    /// opaque Body hides its root.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Image))]
    [AddComponentMenu("UI/Effects/Cheers Toast Liquid Spill")]
    public sealed class CheersToastLiquidSpill : BaseMeshEffect
    {
        [SerializeField] private Image sourceLiquid;
        [SerializeField] private CheersToastLiquidWave sourceWave;
        [SerializeField, Range(0f, 1f)] private float sourceU = 0.62f;
        [SerializeField, Range(0f, 1f)] private float sourceV = 0.724f;
        [SerializeField, Min(0f)] private float contactTime = BartenderCheersSequence.ContactTime;
        [SerializeField, Range(0f, 0.08f)] private float responseDelay = 0.015f;
        [SerializeField, Range(0.2f, 0.5f)] private float duration = 0.3f;
        [SerializeField, Range(-1f, 1f)] private float direction = -1f;
        [SerializeField, Min(0f)] private float peakHeight = 110f;
        [SerializeField, Min(0f)] private float peakSpread = 65f;
        [SerializeField, Min(0f)] private float rootWidth = 26f;
        [SerializeField, Min(0f)] private float tipWidth = 10f;
        [Tooltip("Opaque painted region of the source sprite; X across the ribbon, Y from root to tip.")]
        [SerializeField] private Rect sampleUv = new Rect(0.3f, 0.44f, 0.09f, 0.25f);
        [SerializeField, Range(8, 32)] private int segments = 18;
        [SerializeField, Range(4, 16)] private int capSegments = 8;
        [SerializeField, Range(0f, 2f)] private float edgeFeather = 1f;

        private Image spillImage;
        private float lastTime = float.NaN;
        private bool wasActive;

        protected override void OnEnable()
        {
            spillImage = GetComponent<Image>();
            if (sourceWave == null && sourceLiquid != null)
                sourceWave = sourceLiquid.GetComponent<CheersToastLiquidWave>();
            lastTime = float.NaN;
            wasActive = false;
            base.OnEnable();
        }

        private void LateUpdate()
        {
            float time = sourceWave != null ? sourceWave.SequenceTimeSeconds : 0f;
            float elapsed = time - contactTime - responseDelay;
            bool active = elapsed > 0f && elapsed < duration;
            if (time != lastTime && (active || wasActive))
            {
                if (graphic != null) graphic.SetVerticesDirty();
            }
            lastTime = time;
            wasActive = active;
        }

        public override void ModifyMesh(VertexHelper vertices)
        {
            // Never leave the Image's unmodified rectangular texture on screen.
            if (!IsActive() || sourceLiquid == null || sourceWave == null || vertices.currentVertCount != 4)
            {
                vertices.Clear();
                return;
            }
            if (spillImage == null) spillImage = GetComponent<Image>();
            if (spillImage == null || spillImage.type != Image.Type.Simple || spillImage.useSpriteMesh)
            {
                vertices.Clear();
                return;
            }

            float elapsed = sourceWave.SequenceTimeSeconds - contactTime - responseDelay;
            if (elapsed <= 0f || elapsed >= duration)
            {
                vertices.Clear();
                return;
            }

            UIVertex bottomLeft = default;
            UIVertex topLeft = default;
            UIVertex topRight = default;
            UIVertex bottomRight = default;
            vertices.PopulateUIVertex(ref bottomLeft, 0);
            vertices.PopulateUIVertex(ref topLeft, 1);
            vertices.PopulateUIVertex(ref topRight, 2);
            vertices.PopulateUIVertex(ref bottomRight, 3);
            vertices.Clear();

            RectTransform spillRect = spillImage.rectTransform;
            RectTransform sourceRect = sourceLiquid.rectTransform;
            Rect sourceCanvas = sourceLiquid.GetPixelAdjustedRect();
            float displacedV = sourceV + sourceWave.EvaluateSurfaceDisplacementUV(sourceU);
            Vector3 sourcePoint = new Vector3(
                sourceCanvas.xMin + sourceCanvas.width * sourceU,
                sourceCanvas.yMin + sourceCanvas.height * displacedV, 0f);
            Vector2 origin = spillRect.InverseTransformPoint(sourceRect.TransformPoint(sourcePoint));

            // Counter Pose rotation on this upright canvas while keeping dimensions in UI units.
            Vector2 up = ((Vector2)spillRect.InverseTransformDirection(Vector3.up)).normalized;
            Vector2 right = ((Vector2)spillRect.InverseTransformDirection(Vector3.right)).normalized;
            // Overlap the waterline so tilting cannot expose a detached splash root.
            origin -= up * (rootWidth * 0.24f);

            float growth = Smooth01(elapsed / 0.06f);
            float fall = Smooth01((elapsed - 0.06f) / Mathf.Max(0.01f, duration - 0.06f));
            float height = peakHeight * growth * (1f - 0.6f * fall);
            float spread = peakSpread * growth * (1f + 0.18f * fall);
            float widthGain = Smooth01(elapsed / 0.035f) *
                (1f - Smooth01((elapsed - 0.16f) / Mathf.Max(0.01f, duration - 0.16f)));
            Vector2 p0 = origin;
            Vector2 p1 = origin + right * (direction * 0.12f * spread) + up * (0.65f * height);
            Vector2 p2 = origin + right * (direction * 0.60f * spread) + up * (1.05f * height);
            Vector2 p3 = origin + right * (direction * spread) + up * ((0.82f - 0.72f * fall) * height);

            int count = Mathf.Clamp(segments, 8, 32);
            Vector2 tipTangent = up;
            Vector2 tipNormal = right;
            float endHalfWidth = 0f;
            float feather = edgeFeather * widthGain;
            for (int row = 0; row <= count; row++)
            {
                float t = (float)row / count;
                Vector2 point = Bezier(p0, p1, p2, p3, t);
                Vector2 tangent = BezierTangent(p0, p1, p2, p3, t).normalized;
                if (tangent.sqrMagnitude < 0.0001f) tangent = up;
                Vector2 normal = new Vector2(-tangent.y, tangent.x);
                // A wide root, narrow neck and round head shape the liquid into droplets.
                float rootFlare = rootWidth * Mathf.Pow(1f - t, 3f);
                float head = tipWidth * Smooth01((t - 0.60f) / 0.40f);
                float halfWidth = 0.5f * (8f + rootFlare + head) * widthGain;
                AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight,
                    point - normal * (halfWidth + feather), 0f, t, 0f);
                AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight,
                    point - normal * halfWidth, 0f, t, 1f);
                AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight,
                    point + normal * halfWidth, 1f, t, 1f);
                AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight,
                    point + normal * (halfWidth + feather), 1f, t, 0f);
                tipTangent = tangent;
                tipNormal = normal;
                endHalfWidth = halfWidth;
            }

            for (int row = 0; row < count; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    int index = row * 4 + column;
                    vertices.AddTriangle(index, index + 4, index + 5);
                    vertices.AddTriangle(index + 5, index + 1, index);
                }
            }

            // Join the strip edges with a round head and the same one-unit feather, without another sprite.
            int center = vertices.currentVertCount;
            AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight, p3, 0.5f, 1f, 1f);
            int capCount = Mathf.Clamp(capSegments, 4, 16);
            for (int step = 0; step <= capCount; step++)
            {
                float angle = Mathf.PI * step / capCount;
                float cross = 0.5f - 0.5f * Mathf.Cos(angle);
                Vector2 radial = -tipNormal * Mathf.Cos(angle) + tipTangent * Mathf.Sin(angle);
                AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight,
                    p3 + radial * endHalfWidth, cross, 1f, 1f);
                AddVertex(vertices, bottomLeft, topLeft, topRight, bottomRight,
                    p3 + radial * (endHalfWidth + feather), cross, 1f, 0f);
                if (step == capCount) continue;
                int inner = center + 1 + step * 2;
                vertices.AddTriangle(center, inner, inner + 2);
                vertices.AddTriangle(inner, inner + 1, inner + 3);
                vertices.AddTriangle(inner + 3, inner + 2, inner);
            }
        }

        private void AddVertex(VertexHelper vertices, UIVertex bottomLeft, UIVertex topLeft,
            UIVertex topRight, UIVertex bottomRight, Vector2 position, float cross, float along, float opacity)
        {
            float u = Mathf.Lerp(sampleUv.xMin, sampleUv.xMax, cross);
            float v = Mathf.Lerp(sampleUv.yMin, sampleUv.yMax, along);
            UIVertex vertex = bottomLeft;
            vertex.position = new Vector3(position.x, position.y, bottomLeft.position.z);
            vertex.uv0 = Vector4.LerpUnclamped(
                Vector4.LerpUnclamped(bottomLeft.uv0, bottomRight.uv0, u),
                Vector4.LerpUnclamped(topLeft.uv0, topRight.uv0, u), v);
            // Keep the backing Image transparent. Only the generated ribbon gets liquid colour.
            Color32 color = sourceLiquid.color;
            color.a = (byte)Mathf.RoundToInt(color.a * opacity);
            vertex.color = color;
            vertices.AddVert(vertex);
        }

        private static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float s = 1f - t;
            return s * s * s * a + 3f * s * s * t * b + 3f * s * t * t * c + t * t * t * d;
        }

        private static Vector2 BezierTangent(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float s = 1f - t;
            return 3f * s * s * (b - a) + 6f * s * t * (c - b) + 3f * t * t * (d - c);
        }
    }
}
