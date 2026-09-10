using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Draws a texture-free gradient inside the card's existing parent mask.</summary>
    [DisallowMultipleComponent]
    public sealed class OrderCardLightOverlay : MaskableGraphic
    {
        private const int Columns = 6;
        private const int Rows = 8;

        [SerializeField] private Color highlightColor =
            new Color32(0xFF, 0xF6, 0xE4, 0x3D);
        [SerializeField] private Color shadeColor =
            new Color32(0x3A, 0x24, 0x1C, 0x18);
        [SerializeField, Range(0f, 1.5f)] private float intensity = 1f;

        public void SetIntensity(float value)
        {
            float clamped = Mathf.Clamp(value, 0f, 1.5f);
            if (Mathf.Approximately(intensity, clamped)) return;
            intensity = clamped;
            SetVerticesDirty();
        }

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            Rect rect = GetPixelAdjustedRect();

            for (int y = 0; y <= Rows; y++)
            {
                float v = EdgeBiasedCoordinate(y, Rows);
                float py = Mathf.Lerp(rect.yMin, rect.yMax, v);
                for (int x = 0; x <= Columns; x++)
                {
                    float u = EdgeBiasedCoordinate(x, Columns);
                    float px = Mathf.Lerp(rect.xMin, rect.xMax, u);
                    UIVertex vertex = UIVertex.simpleVert;
                    vertex.position = new Vector3(px, py);
                    vertex.color = EvaluateLight(u, v);
                    vertexHelper.AddVert(vertex);
                }
            }

            int rowWidth = Columns + 1;
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++)
                {
                    int lowerLeft = y * rowWidth + x;
                    int lowerRight = lowerLeft + 1;
                    int upperLeft = lowerLeft + rowWidth;
                    int upperRight = upperLeft + 1;
                    vertexHelper.AddTriangle(lowerLeft, upperLeft, upperRight);
                    vertexHelper.AddTriangle(lowerLeft, upperRight, lowerRight);
                }
            }
        }

        private Color32 EvaluateLight(float u, float v)
        {
            // Soft light from the top-left, with a small cream highlight on the rim.
            float topField = SmoothRange(0.52f, 1f, v);
            float leftField = SmoothRange(0.55f, 1f, 1f - u);
            float broadLight = Mathf.Clamp01(topField * 0.52f + leftField * 0.20f);
            float topRim = SmoothRange(0.84f, 1f, v) * Mathf.Lerp(0.72f, 1f, 1f - u);
            float leftRim = SmoothRange(0.86f, 1f, 1f - u) * Mathf.Lerp(0.40f, 1f, v);
            float highlight = Mathf.Clamp01(
                broadLight * 0.22f + Mathf.Max(topRim, leftRim) * 0.78f);

            // Shade the lower and right edges to anchor the card without adding another frame.
            float bottomEdge = 1f - SmoothRange(0.04f, 0.22f, v);
            float rightEdge = SmoothRange(0.78f, 0.98f, u)
                              * Mathf.Lerp(1f, 0.28f, v);
            float diagonal = SmoothRange(1.02f, 1.72f, u + (1f - v));
            float shade = Mathf.Clamp01(
                Mathf.Max(bottomEdge * Mathf.Lerp(0.48f, 1f, u), rightEdge)
                + diagonal * 0.16f);

            // Fade at the inset edge to hide a rectangular seam.
            float horizontalFade = Mathf.Min(
                SmoothRange(0f, 0.075f, u),
                SmoothRange(0f, 0.075f, 1f - u));
            float verticalFade = Mathf.Min(
                SmoothRange(0f, 0.055f, v),
                SmoothRange(0f, 0.055f, 1f - v));
            float surfaceFade = Mathf.Min(horizontalFade, verticalFade);
            highlight *= surfaceFade;
            shade *= surfaceFade;

            float highlightAlpha = highlightColor.a * highlight * intensity;
            float shadeAlpha = shadeColor.a * shade * intensity;
            float outputAlpha = highlightAlpha
                                + shadeAlpha * (1f - highlightAlpha);
            if (outputAlpha <= 0.0001f) return new Color32(0, 0, 0, 0);

            Vector3 premultiplied = new Vector3(
                highlightColor.r, highlightColor.g, highlightColor.b)
                * highlightAlpha
                + new Vector3(shadeColor.r, shadeColor.g, shadeColor.b)
                * shadeAlpha * (1f - highlightAlpha);
            Color result = new Color(
                premultiplied.x / outputAlpha,
                premultiplied.y / outputAlpha,
                premultiplied.z / outputAlpha,
                outputAlpha);
            return result;
        }

        private static float SmoothRange(float from, float to, float value)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, value));
        }

        private static float EdgeBiasedCoordinate(int index, int divisions)
        {
            float linear = index / (float)divisions;
            return 0.5f - 0.5f * Mathf.Cos(Mathf.PI * linear);
        }
    }
}
