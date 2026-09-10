using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>I draw the falling column from PourAnimator's timeline, without my own update loop, clock or volume state.</summary>
    [DisallowMultipleComponent]
    public sealed class PourStream : MonoBehaviour
    {
        private const float WidthEpsilon = 0.0001f;
        private const float TipTaperRoyalSpan = 0.22f;
        private const float VisibilityFloorRoyal = 0.0005f;
        private static readonly float[] TailClock =
            { 0f, .111111f, .222222f, .333333f, .444444f,
              .555556f, .666667f, .777778f, .888889f, 1f };
        private static readonly float[] TailHeight =
            { 1f, .934783f, .820652f, .695652f, .581522f,
              .461957f, .342391f, .228261f, .119565f, 0f };

        public Material material;
        [Tooltip("Width ramp evaluated from the parent timeline's absolute emission age.")]
        public float flowRampTime = 0.045f;
        public float lipDrop = 0.16f;
        public int segments = 20;
        public int sortingOrder = 4;
        public string sortingLayer = "Default";

        [Header("Authored Renderer")]
        [Tooltip("MeshFilter authored on the stream object. If unassigned, the existing "
                 + "component on this GameObject is used.")]
        [SerializeField] private MeshFilter filter;
        [Tooltip("MeshRenderer authored on the stream object. If unassigned, the existing "
                 + "component on this GameObject is used.")]
        [SerializeField] private MeshRenderer meshRenderer;
        private Mesh mesh;
        private bool rendererReady;
        private bool rendererErrorReported;
        private LiquidBottle source;
        private LiquidBottle target;
        private Color color;
        private bool initialized;
        private bool active;
        private bool emitting;
        private bool tailCompleted;
        private Vector2 sourceLipLocal;
        private Vector3 lip;
        private float fallX, landY, headY, tailY, detachedFallHeight;
        private float activeWidth, activeTipWidth;
        private float activeLipDrop;
        private float activeTipTaper, activeVisibilityFloor;

        private readonly List<Vector3> vertices = new List<Vector3>(192);
        private readonly List<Color32> colors = new List<Color32>(192);
        private readonly List<Vector2> uvs = new List<Vector2>(192);
        private readonly List<int> triangles = new List<int>(640);

        public bool Active => active;
        public bool TailCompleted => tailCompleted;
        public float FallX => fallX;
        public bool IsReady => rendererReady && material != null;

        private void Awake() => InitializeRenderer();

        private bool InitializeRenderer()
        {
            if (rendererReady)
            {
                if (material != null) return true;
                if (meshRenderer != null) meshRenderer.enabled = false;
                return false;
            }
            if (filter == null) filter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();
            if (filter == null || meshRenderer == null || material == null)
            {
                if (meshRenderer != null) meshRenderer.enabled = false;
                if (!rendererErrorReported)
                {
                    rendererErrorReported = true;
                    Debug.LogError(
                        "[PourStream] MeshFilter, MeshRenderer and Material must be "
                        + "authored on the PourStream GameObject.", this);
                }
                return false;
            }

            mesh = new Mesh { name = "PourStream", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.sortingLayerName = sortingLayer;
            meshRenderer.sortingOrder = sortingOrder;
            meshRenderer.enabled = false;
            rendererReady = true;
            return true;
        }

        /// <summary>I bind both vessels and the emission lip. Widths are world units; shared mesh distances follow board scale.</summary>
        public void Begin(LiquidBottle from, LiquidBottle to, Color liquidColor,
            float bodyWidth, float leadingWidth, float referenceDistanceScale,
            Vector2 emissionLipLocal)
        {
            Cancel();
            if (!InitializeRenderer() || from == null || to == null) return;
            source = from;
            target = to;
            color = liquidColor;
            sourceLipLocal = emissionLipLocal;
            activeWidth = Mathf.Max(WidthEpsilon, bodyWidth);
            activeTipWidth = Mathf.Clamp(leadingWidth, WidthEpsilon, activeWidth);
            float distanceScale = Mathf.Max(0.0001f, referenceDistanceScale);
            activeLipDrop = Mathf.Max(0.0001f, lipDrop * distanceScale);
            activeTipTaper = Mathf.Max(0.0001f, TipTaperRoyalSpan * distanceScale);
            activeVisibilityFloor = VisibilityFloorRoyal * distanceScale;
            gameObject.layer = target.gameObject.layer;
            meshRenderer.sortingLayerName = target.sortingLayer;
            meshRenderer.sortingOrder = TargetInteriorSortingOrder(target);
            initialized = true;
            active = true;
            emitting = true;
            UpdateEmissionEndpoints();
            headY = lip.y;
            tailY = lip.y;
        }

        /// <summary>Head 0..1 crosses the fall; tail stays zero until detachment, then follows. I sample the surface after the parent updates receiver volume.</summary>
        public void RenderFrame(float headProgress, float tailProgress,
            float emissionAge, bool emitting)
        {
            if (!initialized) return;
            if (source == null || target == null) { Cancel(); return; }

            if (emitting)
            {
                this.emitting = true;
                UpdateEmissionEndpoints();
            }
            else
            {
                if (this.emitting) StopEmitting();
                // The lip and fall X stay frozen while the receiving surface rises.
                landY = target.ContactWorldYAt(fallX);
            }

            float head = Mathf.Clamp01(headProgress);
            float tail = emitting ? 0f : Mathf.Clamp01(tailProgress);
            tailCompleted = !emitting && tail >= 1f;
            headY = Mathf.Lerp(lip.y, landY, head);
            tailY = emitting
                ? lip.y
                : Mathf.Min(lip.y,
                    landY + detachedFallHeight * ReferenceTailHeight(tail));
            active = emitting || tail < 1f;
            if (!active)
            {
                meshRenderer.enabled = false;
                return;
            }
            BuildMesh(head, Mathf.Max(0f, emissionAge));
        }

        /// <summary>Call before the source leaves its final pour pose so the detached column cannot follow the returning glass.</summary>
        public void StopEmitting()
        {
            if (!initialized || !emitting) return;
            if (source == null || target == null) { Cancel(); return; }
            UpdateEmissionEndpoints();
            detachedFallHeight = Mathf.Max(0f, lip.y - landY);
            emitting = false;
        }

        public void Cancel()
        {
            initialized = false;
            active = false;
            emitting = false;
            tailCompleted = false;
            source = null;
            target = null;
            detachedFallHeight = 0f;
            if (meshRenderer != null) meshRenderer.enabled = false;
            if (mesh != null) mesh.Clear();
        }

        private void UpdateEmissionEndpoints()
        {
            lip = source.transform.TransformPoint(
                new Vector3(sourceLipLocal.x, sourceLipLocal.y, 0f));
            fallX = target.MouthWorld.x;
            landY = target.ContactWorldYAt(fallX);
        }

        private void OnDisable() => Cancel();

        private void OnDestroy()
        {
            Cancel();
            if (filter != null && filter.sharedMesh == mesh) filter.sharedMesh = null;
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
            mesh = null;
        }

        private static int TargetInteriorSortingOrder(LiquidBottle receiver)
        {
            int order = receiver.sortingOrder + 3;
            Transform front = receiver.transform.Find("FrontGlass");
            Renderer frontRenderer = front != null ? front.GetComponent<Renderer>() : null;
            if (frontRenderer != null)
                order = Mathf.Max(receiver.sortingOrder + 1,
                    frontRenderer.sortingOrder - 1);
            return order;
        }

        private void BuildMesh(float headProgress, float emissionAge)
        {
            vertices.Clear();
            colors.Clear();
            uvs.Clear();
            triangles.Clear();

            float ramp = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01(emissionAge / Mathf.Max(0.001f, flowRampTime)));

            int count = Mathf.Max(3, segments);
            float top = Mathf.Max(tailY, headY);
            float capRadius = emitting && headProgress < 1f
                ? Mathf.Min(activeTipWidth * ramp * 0.50f,
                    Mathf.Max(0f, top - headY) / 2.44f)
                : 0f;
            // headY is the visible tip, not the centre of a cap extending below it.
            float bottom = headY + capRadius * 1.22f;
            bool drawStrip = top - bottom >= activeVisibilityFloor;
            if (!drawStrip)
            {
                meshRenderer.enabled = false;
                return;
            }

            meshRenderer.enabled = true;
            // I use the liquid palette's coloured light and shade; neutral highlights belong to the glass layer.
            Color cap = LiquidPalette.CapFor(color);
            Color denseShade = LiquidPalette.ShadeFor(color);
            Color coreFloat = Color.Lerp(color, cap, 0.34f);
            coreFloat.a = color.a * 0.96f;
            // I use the authored cap colour directly so channel clipping cannot turn it white.
            Color highlightFloat = cap;
            highlightFloat.a = color.a * 0.98f;
            Color shadeFloat = Color.Lerp(color, denseShade, 0.58f);
            shadeFloat.a = color.a * 0.82f;
            Color32 coreColor = coreFloat;
            Color32 highlightColor = highlightFloat;
            Color32 shadeColor = shadeFloat;
            Color32 clearHighlight = highlightColor;
            Color32 clearShade = shadeColor;
            clearHighlight.a = 0;
            clearShade.a = 0;
            if (drawStrip)
            {
                for (int i = 0; i <= count; i++)
                {
                    float y = Mathf.Lerp(top, bottom, i / (float)count);
                    Vector2 p = PointAt(y);
                    Vector2 previous = PointAt(Mathf.Lerp(top, bottom,
                        Mathf.Max(0f, (i - 1) / (float)count)));
                    Vector2 next = PointAt(Mathf.Lerp(top, bottom,
                        Mathf.Min(1f, (i + 1) / (float)count)));

                    Vector2 tangent = next - previous;
                    if (tangent.sqrMagnitude < 1e-8f) tangent = Vector2.down;
                    tangent.Normalize();
                    Vector2 normal = new Vector2(-tangent.y, tangent.x);

                    // I taper the leading tip over a board-scaled distance; the fed column keeps a fixed width
                    // after contact.
                    float toHead = headProgress < 1f
                        ? Mathf.InverseLerp(activeTipTaper, 0f, y - bottom) : 0f;
                    float w = Mathf.Lerp(activeWidth, activeTipWidth, toHead * toHead)
                              * ramp * 0.5f;
                    float feather = w * 0.80f;
                    float core = w * 0.38f;

                    vertices.Add(new Vector3(p.x - normal.x * w, p.y - normal.y * w, 0f));
                    vertices.Add(new Vector3(p.x - normal.x * feather, p.y - normal.y * feather, 0f));
                    vertices.Add(new Vector3(p.x - normal.x * core, p.y - normal.y * core, 0f));
                    vertices.Add(new Vector3(p.x + normal.x * core, p.y + normal.y * core, 0f));
                    vertices.Add(new Vector3(p.x + normal.x * feather, p.y + normal.y * feather, 0f));
                    vertices.Add(new Vector3(p.x + normal.x * w, p.y + normal.y * w, 0f));
                    colors.Add(clearHighlight);
                    colors.Add(highlightColor);
                    colors.Add(coreColor);
                    colors.Add(coreColor);
                    colors.Add(shadeColor);
                    colors.Add(clearShade);
                    uvs.Add(new Vector2(0f, i / (float)count));
                    uvs.Add(new Vector2(0.10f, i / (float)count));
                    uvs.Add(new Vector2(0.31f, i / (float)count));
                    uvs.Add(new Vector2(0.69f, i / (float)count));
                    uvs.Add(new Vector2(0.90f, i / (float)count));
                    uvs.Add(new Vector2(1f, i / (float)count));

                    if (i > 0)
                    {
                        int previousRow = (i - 1) * 6;
                        int currentRow = i * 6;
                        for (int lane = 0; lane < 5; lane++)
                        {
                            int a = previousRow + lane;
                            int b = currentRow + lane;
                            triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                            triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                        }
                    }
                }

                // I hide the leading cap at contact, when the parent timeline takes over the receiving surface.
                if (capRadius > activeVisibilityFloor)
                    AddLeadingCap(PointAt(bottom), capRadius,
                        highlightColor, coreColor);
            }

            // I convert world geometry to mesh-local positions so the animator's parent cannot transform the fall
            // twice.
            for (int i = 0; i < vertices.Count; i++)
                vertices[i] = transform.InverseTransformPoint(vertices[i]);
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0, false);
            mesh.RecalculateBounds();
        }

        private static float ReferenceTailHeight(float progress)
        {
            float t = Mathf.Clamp01(progress);
            for (int i = 1; i < TailClock.Length; i++)
            {
                if (t > TailClock[i]) continue;
                float span = TailClock[i] - TailClock[i - 1];
                float local = span > 0f ? (t - TailClock[i - 1]) / span : 1f;
                return Mathf.Lerp(TailHeight[i - 1], TailHeight[i], local);
            }
            return 0f;
        }

        private void AddLeadingCap(Vector2 centre, float radius, Color32 bright, Color32 body)
        {
            // Numerical guards must not create a visible minimum width that stops the stream scaling with the
            // glass.
            if (radius <= activeVisibilityFloor) return;

            const int sides = 12;
            int centreIndex = vertices.Count;
            vertices.Add(new Vector3(centre.x, centre.y, 0f));
            colors.Add(body);
            uvs.Add(new Vector2(0.5f, 0.5f));

            int innerStart = vertices.Count;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i / (float)sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius * 0.78f;
                float y = Mathf.Sin(angle) * radius * 0.95f;
                vertices.Add(new Vector3(centre.x + x, centre.y + y, 0f));
                colors.Add(x < 0f ? bright : body);
                uvs.Add(new Vector2(x / (radius * 2f) + 0.5f,
                    y / (radius * 2f) + 0.5f));
            }

            int outerStart = vertices.Count;
            Color32 clearBright = bright;
            Color32 clearBody = body;
            clearBright.a = 0;
            clearBody.a = 0;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i / (float)sides * Mathf.PI * 2f;
                float x = Mathf.Cos(angle) * radius;
                float y = Mathf.Sin(angle) * radius * 1.22f;
                vertices.Add(new Vector3(centre.x + x, centre.y + y, 0f));
                colors.Add(x < 0f ? clearBright : clearBody);
                uvs.Add(new Vector2(x / (radius * 2f) + 0.5f,
                    y / (radius * 2.44f) + 0.5f));
            }

            for (int i = 0; i < sides; i++)
            {
                triangles.Add(centreIndex);
                triangles.Add(innerStart + i);
                triangles.Add(innerStart + i + 1);

                triangles.Add(innerStart + i);
                triangles.Add(outerStart + i);
                triangles.Add(innerStart + i + 1);
                triangles.Add(innerStart + i + 1);
                triangles.Add(outerStart + i);
                triangles.Add(outerStart + i + 1);
            }
        }

        /// <summary>Path of the stream: a short bezier off the lip, then a vertical fall.</summary>
        private Vector2 PointAt(float y)
        {
            float drop = Mathf.Max(0.0001f, activeLipDrop);
            float t = (lip.y - y) / drop;
            if (t >= 1f) return new Vector2(fallX, y);

            // I invert quadratic Y so world Y stays at the visible position, including the bend under the lip.
            t = Mathf.Sqrt(Mathf.Clamp01(t));
            Vector2 p0 = new Vector2(lip.x, lip.y);
            Vector2 p2 = new Vector2(fallX, lip.y - drop);
            Vector2 p1 = new Vector2(Mathf.Lerp(lip.x, fallX, 0.8f), lip.y);
            float u = 1f - t;
            Vector2 point = u * u * p0 + 2f * u * t * p1 + t * t * p2;
            return new Vector2(point.x, y);
        }
    }
}
