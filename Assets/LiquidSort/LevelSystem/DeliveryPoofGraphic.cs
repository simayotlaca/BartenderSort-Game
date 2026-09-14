using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Delivery Poof Graphic")]
    public sealed class DeliveryPoofGraphic : MaskableGraphic
    {
        internal const float DisappearSeconds = 0.30f;
        internal const float TotalSeconds = 0.50f;

        private static readonly Color Lavender = new Color32(188, 134, 255, 255);
        private static readonly Color Champagne = new Color32(255, 229, 162, 255);
        private static readonly Color Ivory = new Color32(255, 249, 230, 255);
        private static readonly int MistTimeId = Shader.PropertyToID("_MistTime");

        private Vector2 origin;
        private Vector2 target;
        private Vector2 sourceFootprint;
        private Vector2 destinationFootprint;
        private float elapsed;
        private float scale;
        private bool sampled;
        private bool cardGlint = true;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        internal void Sample(Vector2 source, Vector2 destination, Color tint,
                             float seconds, float referenceScale,
                             Vector2 sourceSize = default, Vector2 destinationSize = default,
                             float objectScale = 1f, bool drawCardGlint = true)
        {
            raycastTarget = false;
            cardGlint = drawCardGlint;
            if (!Finite(source) || !Finite(destination) || !Finite(seconds)
                || !Finite(referenceScale) || referenceScale <= 0f
                || !Finite(sourceSize) || !Finite(destinationSize) || !Finite(objectScale)
                || seconds < 0f || seconds >= TotalSeconds)
            {
                Clear();
                return;
            }
            origin = source;
            target = destination;
            elapsed = seconds;
            scale = Mathf.Clamp(referenceScale, 0.0001f, 10000f);
            sourceFootprint = sourceSize.x > 0f && sourceSize.y > 0f
                ? sourceSize / scale : new Vector2(80f, 180f);
            destinationFootprint = destinationSize.x > 0f && destinationSize.y > 0f
                ? destinationSize / scale : new Vector2(185f, 265f);
            if (!Finite(sourceFootprint) || !Finite(destinationFootprint))
            {
                Clear();
                return;
            }
            sampled = true;
            if (material != null && material.HasProperty(MistTimeId))
                material.SetFloat(MistTimeId, seconds);
            SetVerticesDirty();
        }

        internal void Clear()
        {
            if (!sampled) return;
            sampled = false;
            SetVerticesDirty();
        }

        protected override void OnDisable()
        {
            sampled = false;
            base.OnDisable();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (!sampled) return;
            DrawGlassGlitter(mesh);
            if (cardGlint) DrawCardGlint(mesh);
        }

        private void DrawGlassGlitter(VertexHelper mesh)
        {
            Vector2 size = sourceFootprint * scale;
            for (int i = 0; i < 3; i++)
            {
                float start = 0.07f + i * 0.019f;
                float end = 0.255f + i * 0.011f;
                float life = Mathf.InverseLerp(start, end, elapsed);
                if (elapsed <= start || life >= 1f) continue;
                float envelope = Smooth(Mathf.InverseLerp(0f, 0.24f, life))
                    * (1f - Smooth(Mathf.InverseLerp(0.40f, 1f, life)));
                float side = i == 1 ? 1f : -1f;
                float x = i == 0 ? -0.26f : i == 1 ? 0.25f : -0.08f;
                float y = i == 0 ? -0.17f : i == 1 ? 0.02f : 0.28f;
                Vector2 center = origin + new Vector2(
                    size.x * (x + side * life * 0.06f),
                    size.y * (y + life * 0.065f));
                float growth = Mathf.Lerp(0.65f, 1.15f, Smooth(life)) * (i == 2 ? 0.82f : 1f);
                DrawMist(mesh, center, Vector2.Scale(size, new Vector2(0.28f, 0.125f)) * growth,
                    side * (0.10f + life * 0.12f), envelope * 0.34f, i + 3);
            }

            for (int i = 0; i < 30; i++)
            {
                float seed = Hash(i + 1);
                float drift = Hash(i + 41);
                float height = Hash(i + 81);
                float start = 0.035f + height * 0.095f;
                float duration = Mathf.Lerp(0.21f, 0.30f, drift);
                float life = Mathf.InverseLerp(start, start + duration, elapsed);
                if (elapsed <= start || life >= 1f) continue;
                float visibility = Smooth(Mathf.InverseLerp(0f, 0.16f, life))
                    * (1f - Smooth(Mathf.InverseLerp(0.52f, 1f, life)));
                float shimmer = 0.65f + 0.35f * Mathf.Pow(
                    Mathf.Sin((life * 1.6f + seed) * Mathf.PI), 2f);
                float pulse = visibility * shimmer;
                float x = (seed * 2f - 1f) * 0.47f;
                Vector2 offset = new Vector2(
                    size.x * (x + Mathf.Sin(life * 2.8f + seed * 6.28f) * life * 0.11f),
                    size.y * (height * 0.72f - 0.34f + life * (0.13f + drift * 0.15f)));
                Vector2 position = origin + offset;
                bool glint = i % 8 == 0;
                Color color = glint ? (i == 8 ? Lavender : Champagne)
                    : (i % 4 == 0 ? Lavender : Champagne);
                float radius = glint
                    ? Mathf.Min(size.x * 0.085f, scale * Mathf.Lerp(3.5f, 5.8f, drift))
                    : Mathf.Min(size.x * 0.034f, scale * Mathf.Lerp(1.15f, 2.0f, drift));
                radius *= 0.8f + shimmer * 0.2f;
                DrawGlow(mesh, position, Vector2.one * radius * (glint ? 2.1f : 2.4f),
                    color, pulse * (glint ? 0.12f : 0.17f), 12);
                if (glint)
                    DrawStar(mesh, position, radius, pulse * 0.95f, seed * 0.35f, color);
                else
                    DrawGlow(mesh, position, Vector2.one * radius, Color.Lerp(color, Ivory, 0.6f),
                        pulse * 0.95f, 8);
            }
        }

        private static float Hash(int seed)
        {
            float value = Mathf.Sin(seed * 127.1f + 311.7f) * 43758.5453f;
            return value - Mathf.Floor(value);
        }

        private void DrawCardGlint(VertexHelper mesh)
        {
            float life = Mathf.InverseLerp(0.10f, 0.31f, elapsed);
            if (elapsed <= 0.10f || life >= 1f) return;
            float pulse = Mathf.Max(0f, Mathf.Sin(Mathf.PI * life));
            Vector2 size = destinationFootprint * scale;
            Vector2 position = target + Vector2.Scale(size, new Vector2(0.35f, 0.30f));
            float radius = Mathf.Min(8.5f * scale, size.x * 0.055f);
            DrawGlow(mesh, position, Vector2.one * radius * 2.3f, Lavender, pulse * 0.16f);
            DrawStar(mesh, position, radius * (0.75f + pulse * 0.25f), pulse, 0.08f, Champagne);
        }

        private static void DrawMist(VertexHelper mesh, Vector2 center, Vector2 radius,
                                     float rotation, float opacity, int seed)
        {
            if (opacity <= 0.001f) return;
            int first = mesh.currentVertCount;
            float c = Mathf.Cos(rotation), s = Mathf.Sin(rotation);
            for (int i = 0; i < 4; i++)
            {
                float u = i == 1 || i == 2 ? 1f : 0f;
                float v = i >= 2 ? 1f : 0f;
                Vector2 point = new Vector2((u * 2f - 1f) * radius.x, (v * 2f - 1f) * radius.y);
                UIVertex vertex = UIVertex.simpleVert;
                vertex.position = center + new Vector2(point.x * c - point.y * s, point.x * s + point.y * c);
                vertex.color = WithAlpha(Color.white, opacity);
                vertex.uv0 = new Vector4(2f + seed * 2f + u, v, 0f, 0f);
                mesh.AddVert(vertex);
            }
            mesh.AddTriangle(first, first + 1, first + 2);
            mesh.AddTriangle(first, first + 2, first + 3);
        }

        private static void DrawGlow(VertexHelper mesh, Vector2 center, Vector2 radius,
                                     Color tint, float alpha, int segments = 24)
        {
            int first = mesh.currentVertCount;
            AddVertex(mesh, center, WithAlpha(tint, alpha));
            for (int i = 0; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                Vector2 radial = new Vector2(Mathf.Cos(angle) * radius.x, Mathf.Sin(angle) * radius.y);
                AddVertex(mesh, center + radial * 0.40f, WithAlpha(tint, alpha * 0.50f));
                AddVertex(mesh, center + radial, WithAlpha(tint, 0f));
                if (i == 0) continue;
                int previous = first + 1 + (i - 1) * 2;
                int next = previous + 2;
                mesh.AddTriangle(first, previous, next);
                mesh.AddTriangle(previous, previous + 1, next);
                mesh.AddTriangle(next, previous + 1, next + 1);
            }
        }

        private static void DrawStar(VertexHelper mesh, Vector2 center, float radius,
                                     float alpha, float rotation, Color tint)
        {
            int first = mesh.currentVertCount;
            AddVertex(mesh, center, WithAlpha(Ivory, alpha));
            for (int i = 0; i <= 8; i++)
            {
                float angle = i * Mathf.PI * 0.25f + rotation;
                float reach = i % 2 == 0 ? radius : radius * 0.19f;
                AddVertex(mesh, center + new Vector2(Mathf.Cos(angle) * 0.78f,
                    Mathf.Sin(angle) * 1.18f) * reach, WithAlpha(tint, alpha * 0.92f));
                if (i > 0) mesh.AddTriangle(first, first + i, first + i + 1);
            }
        }

        private static void AddVertex(VertexHelper mesh, Vector2 position, Color tint)
        {
            UIVertex vertex = UIVertex.simpleVert;
            vertex.position = position;
            vertex.color = tint;
            mesh.AddVert(vertex);
        }

        private static Color WithAlpha(Color tint, float alpha)
        {
            tint.a = Mathf.Clamp01(alpha);
            return tint;
        }
        private static float Smooth(float t) => t * t * (3f - 2f * t);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
    }
}
