using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>A soft, static halo behind the daily-orders entry artwork.</summary>
    [DisallowMultipleComponent]
    public sealed class DailyOrdersBacklight : MaskableGraphic
    {
        private const int Segments = 48;
        private const int Rings = 10;

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            Vector2 center = rect.center;
            Vector2 radius = rect.size * 0.5f;
            mesh.AddVert(center, color, Vector2.zero);

            for (int ring = 1; ring <= Rings; ring++)
            {
                float distance = ring / (float)Rings;
                Color tint = color;
                tint.a *= 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(0.25f, 1f, distance));
                for (int segment = 0; segment < Segments; segment++)
                {
                    float angle = segment * (Mathf.PI * 2f / Segments);
                    Vector2 position = center + new Vector2(
                        Mathf.Cos(angle) * radius.x,
                        Mathf.Sin(angle) * radius.y) * distance;
                    mesh.AddVert(position, tint, Vector2.zero);
                }
            }

            for (int segment = 0; segment < Segments; segment++)
            {
                int next = (segment + 1) % Segments;
                mesh.AddTriangle(0, 1 + next, 1 + segment);
                for (int ring = 1; ring < Rings; ring++)
                {
                    int inner = 1 + (ring - 1) * Segments;
                    int outer = inner + Segments;
                    mesh.AddTriangle(inner + segment, inner + next, outer + next);
                    mesh.AddTriangle(inner + segment, outer + next, outer + segment);
                }
            }
        }
    }
}
