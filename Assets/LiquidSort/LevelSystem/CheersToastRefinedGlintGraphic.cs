using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>The four-ray contact glint of the cheers toast; its ray length is animated by the toast.</summary>
    [AddComponentMenu("")]
    public sealed class CheersToastRefinedGlintGraphic : MaskableGraphic
    {
        private float rayLength = 4f;
        private float rayWidth = 1.8f;

        internal void SetShape(float length, float width)
        {
            if (Mathf.Approximately(rayLength, length) && Mathf.Approximately(rayWidth, width)) return;
            rayLength = length;
            rayWidth = width;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;
            const int capSegments = 8;
            const int perimeterCount = (capSegments + 1) * 2;
            float halfWidth = rayWidth * .5f;
            for (int ray = 0; ray < 4; ray++)
            {
                float angle = Mathf.PI * .25f + ray * Mathf.PI * .5f;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 normal = new Vector2(-direction.y, direction.x);
                int center = vh.currentVertCount;
                vertex.position = direction * ((2 + rayLength) * .5f);
                vh.AddVert(vertex);
                for (int cap = 0; cap < 2; cap++)
                for (int i = 0; i <= capSegments; i++)
                {
                    float theta = (cap == 0 ? -.5f : .5f) * Mathf.PI + i * Mathf.PI / capSegments;
                    Vector2 capCenter = direction * (cap == 0 ? rayLength : 2f);
                    vertex.position = capCenter + halfWidth
                        * (direction * Mathf.Cos(theta) + normal * Mathf.Sin(theta));
                    vh.AddVert(vertex);
                }
                for (int i = 0; i < perimeterCount; i++)
                    vh.AddTriangle(center, center + i + 1, center + (i + 1) % perimeterCount + 1);
            }
        }
    }
}
