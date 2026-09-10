using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort
{
    /// <summary>I draw receiver contact from the pour's DOTween timeline; this component has no separate clock or update loop.</summary>
    [DisallowMultipleComponent]
    public sealed class PourContactEffect : MonoBehaviour
    {
        private const int ArcSegments = 40;
        private const int CurveSamples = 4;
        private const int MaxVertices = 12288;
        private const int ClipCapacity = 96;
        private const float GeometryEpsilon = 0.000001f;
        private const float ContactKey116 = .052633f;
        private const float ContactKey120 = .263160f;
        private const float ContactKey126 = .578949f;
        private const float ContactKey133 = .947367f;
        private const float TailKey126 = .111111f;
        private const float TailKey133 = .888889f;
        private static readonly Vector2[] UnitCircle = BuildUnitCircle();

        // I draw the stream's broad root and its two settling shoulders. Values use the reference's 76 px chord.
        private static readonly float[] CrestX =
            { -.250000f, -.197368f, -.144737f, -.092105f, -.052632f,
              -.026316f, 0f, .026316f, .052632f, .105263f, .157895f,
               .210526f, .263158f };
        private static readonly float[] FirstCrest =
            { 0f, 0f, 0f, .013158f, .013158f, .013158f, .013158f,
              .013158f, .013158f, .013158f, 0f, 0f, 0f };
        private static readonly float[] EarlyCrest =
            { 0f, 0f, .013158f, .065789f, .068f, .070f, .072f,
              .074f, .076f, .078947f, 0f, 0f, 0f };
        private static readonly float[] FedCrest =
            { 0f, .013158f, .092105f, .131579f, .129f, .127f, .125f,
              .123f, .121f, .118421f, .092105f, 0f, 0f };
        private static readonly float[] LastFeedCrest =
            { 0f, .052632f, .065789f, .105263f, .107f, .110f, .112f,
              .114f, .116f, .118421f, .065789f, .013158f, 0f };
        private static readonly float[] RaisedCrest =
            { 0f, .052632f, .065789f, .118421f, .105263f, .092105f,
              .065789f, .078947f, .105263f, .118421f, .092105f,
              .013158f, 0f };

        // I animate bright strokes inside a steady crown with fixed lanes and phases for repeatable forward
        // playback. Values use the same 76 px chord.
        private const int SheenStrokes = 3;
        private const int SheenSamples = 7;
        private const float SheenStep = 1e-12f;
        // Cycles over the 0..2 contact clock, about one undulation every 0.17 s.
        private const float SheenCycles = 2.1f;
        private static readonly float[] SheenLaneX = { -.086f, .026f, .118f };
        private static readonly float[] SheenLaneHeight = { .46f, .58f, .40f };
        private static readonly float[] SheenSpan = { .052f, .064f, .045f };
        private static readonly float[] SheenPhase = { 0f, .37f, .71f };

        private struct FxVertex
        {
            internal Vector2 position;
            internal Color color;
            internal FxVertex(Vector2 p, Color c) { position = p; color = c; }
        }

        private Mesh mesh;
        [Header("Authored Renderer")]
        [Tooltip("Authored vertex-colour material shared with the pour stream.")]
        [SerializeField] private Material material;
        [Tooltip("MeshFilter authored on the contact object. If unassigned, the existing "
                 + "component on this GameObject is used.")]
        [SerializeField] private MeshFilter filter;
        [Tooltip("MeshRenderer authored on the contact object. If unassigned, the existing "
                 + "component on this GameObject is used.")]
        [SerializeField] private MeshRenderer meshRenderer;
        private bool rendererReady;
        private bool rendererErrorReported;
        private LiquidBottle target;
        private Transform targetTransform;
        private bool active;
        private float requestedWorldX;
        private float capturedChord;
        private float worldZ;
        private Color faceColor, shadeColor, lightColor;
        private Vector2 surfaceCentre;
        private float halfWidth, halfDepth, contactX, contactY, effectWidth;
        private bool openMouth;
        private float mouthY, mouthLeft, mouthRight;
        private Vector2 cupCentre;
        private float cupRadiusX, cupRadiusY;
        private readonly float[] crest = new float[13];
        // CrestX spans 0.513 of effectWidth; widening lets the settling blob reach the full chord.
        private const float CrestCollapseSpread = 1.75f;
        private float crestSpread = 1f;

        private readonly List<Vector3> vertices = new List<Vector3>(MaxVertices);
        private readonly List<Color32> colors = new List<Color32>(MaxVertices);
        private readonly List<Vector2> uvs = new List<Vector2>(MaxVertices);
        private readonly List<int> indices = new List<int>(MaxVertices * 3);
        private readonly List<int> interiorTriangles = new List<int>(768);
        private readonly List<int> earIndices = new List<int>(256);
        private Vector2[] localInterior;
        private Vector2[] worldInterior;
        private Matrix4x4 cachedTargetLocalToWorld;
        private Matrix4x4 frameWorldToLocal;
        private bool worldInteriorValid;
        private readonly Vector2[] capPolygon = new Vector2[ArcSegments];
        private readonly FxVertex[] clipA = new FxVertex[ClipCapacity];
        private readonly FxVertex[] clipB = new FxVertex[ClipCapacity];
        private readonly FxVertex[] shape = new FxVertex[ClipCapacity];

        public bool IsReady => rendererReady && material != null;

        private void Awake() => InitializeRenderer();

        /// <summary>Creates one persistent mesh for the authored renderer components.</summary>
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
                        "[PourContactEffect] MeshFilter, MeshRenderer and Material must be "
                        + "authored on the PourContactEffect GameObject.", this);
                }
                return false;
            }

            mesh = new Mesh { name = "Pour contact contours", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            meshRenderer.enabled = false;
            rendererReady = true;
            return true;
        }

        /// <summary>The master passes the same latched X and final volume used by the jet.</summary>
        public void Begin(LiquidBottle receiver, Color tint, float impactWorldX,
                          float settledVolume)
        {
            Clear();
            if (!InitializeRenderer() || receiver == null) return;
            target = receiver;
            targetTransform = receiver.transform;
            worldInteriorValid = false;
            requestedWorldX = impactWorldX;
            Vector2 requested = target.WorldToLiquidFrame(
                new Vector3(impactWorldX, target.SurfaceWorldY, targetTransform.position.z));
            target.CaptureReceiveImpactGeometry(requested.x, settledVolume,
                out _, out float localChord);
            capturedChord = localChord * Mathf.Abs(targetTransform.lossyScale.x);
            faceColor = LiquidPalette.CapFor(tint);
            faceColor.a = 1f;
            shadeColor = Color.Lerp(tint, LiquidPalette.ShadeFor(tint), .32f);
            shadeColor.a = 1f;
            // I use the saturated cap colour for the brightest strand so channel clipping cannot bleach the
            // splash.
            lightColor = faceColor;
            gameObject.layer = target.gameObject.layer;
            meshRenderer.sortingLayerName = target.sortingLayer;
            meshRenderer.sortingOrder = target.sortingOrder + 2;
            Transform front = targetTransform.Find("FrontGlass");
            Renderer frontRenderer = front != null ? front.GetComponent<Renderer>() : null;
            if (frontRenderer != null)
                meshRenderer.sortingOrder = Mathf.Min(meshRenderer.sortingOrder,
                    frontRenderer.sortingOrder - 2);
            PrepareInterior();
            active = interiorTriangles.Count > 0;
        }

        /// <summary>arrivalProgress is uneased contact time; tailProgress runs 0..1 for the detached column. settleProgress starts after the tail clears, independent of volume easing.</summary>
        public void RenderFrame(float arrivalProgress, float tailProgress,
            float settleProgress)
        {
            if (!active || target == null || mesh == null) return;
            float arrival = Mathf.Clamp01(arrivalProgress);
            float tail = Mathf.Clamp01(tailProgress);
            float settle = Mathf.Clamp01(settleProgress);
            if (settle >= 1f) { Clear(); return; }
            float appear = settle > 0f ? 1f : Smooth(0f, .045f, arrival);
            // I keep the shared gate alive until each drop finishes, so the second drop survives to its final key.
            float visibility = appear;
            vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
            if (visibility <= .001f || !ReadLiveGeometry())
            {
                meshRenderer.enabled = false;
                return;
            }
            frameWorldToLocal = transform.worldToLocalMatrix;

            float cupX = settle > 0f
                ? Mathf.Lerp(.236842f, .263158f, Smooth(0f, .50f, settle))
                : ArrivalKey(arrival, .184211f, .197368f, .210526f, .236842f);
            float cupY = settle > 0f
                ? Mathf.Lerp(.111842f, .098684f, Smooth(0f, .50f, settle))
                : ArrivalKey(arrival, .111842f, .105263f, .111842f, .111842f);
            cupRadiusX = Mathf.Min(effectWidth * cupX, halfWidth * .80f);
            cupRadiusY = Mathf.Min(effectWidth * cupY, halfDepth * .98f);
            cupCentre = new Vector2(contactX, contactY - effectWidth * .118421f);
            float cupLife = 1f - Smooth(.38f, .82f, settle);
            float rings = visibility * (1f - Smooth(.50f, .86f, settle));
            float cupTone = 1f - .55f * Smooth(.15f, .50f, settle);

            // I draw the cup's far half behind the crest and its bright near edge last, in front.
            AddEllipseFill(cupCentre, cupRadiusX, cupRadiusY,
                WithAlpha(shadeColor, visibility * cupLife * .48f * cupTone), true);
            AddEllipseArc(cupCentre, cupRadiusX, cupRadiusY, 0f, Mathf.PI,
                effectWidth * .006f, WithAlpha(shadeColor, rings * .70f * cupTone), true);
            EvaluateCrest(arrival, tail, settle);
            BuildCrest(visibility);

            AddEllipseArc(cupCentre + Vector2.down * halfDepth * .08f,
                cupRadiusX, cupRadiusY, Mathf.PI, Mathf.PI * 2f,
                effectWidth * .009f, WithAlpha(shadeColor, rings * .70f), true);
            AddEllipseArc(cupCentre, cupRadiusX * .97f, cupRadiusY * .92f,
                Mathf.PI * 1.02f, Mathf.PI * 1.98f,
                effectWidth * .007f, WithAlpha(lightColor, rings * .86f), true);
            AddEllipseArc(cupCentre + Vector2.up * halfDepth * .14f,
                cupRadiusX * .70f, cupRadiusY * .63f,
                Mathf.PI * 1.06f, Mathf.PI * 1.94f,
                effectWidth * .005f, WithAlpha(lightColor, rings * .66f), true);
            BuildCrestSheen(arrival, settle, visibility);
            BuildDrops(arrival, tail, settle, visibility);

            mesh.Clear(false);
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0, false);
            mesh.RecalculateBounds();
            meshRenderer.enabled = indices.Count > 0;
        }

        public void Clear()
        {
            active = false;
            target = null;
            targetTransform = null;
            worldInteriorValid = false;
            if (meshRenderer != null) meshRenderer.enabled = false;
            if (mesh != null) mesh.Clear(false);
        }

        private bool ReadLiveGeometry()
        {
            if (!target.TryGetContactSurfaceGeometry(out Vector2 localCentre,
                    out float localHalfWidth, out float localHalfDepth)) return false;
            Vector3 targetPosition = targetTransform.position;
            Vector3 targetScale = targetTransform.lossyScale;
            float scaleX = Mathf.Abs(targetScale.x);
            float scaleY = Mathf.Abs(targetScale.y);
            surfaceCentre = new Vector2(
                targetPosition.x + localCentre.x * scaleX,
                targetPosition.y + localCentre.y * scaleY);
            halfWidth = localHalfWidth * scaleX;
            halfDepth = localHalfDepth * scaleY;
            if (halfWidth <= GeometryEpsilon || halfDepth <= GeometryEpsilon) return false;
            contactX = Mathf.Clamp(requestedWorldX,
                surfaceCentre.x - halfWidth, surfaceCentre.x + halfWidth);
            float across = Mathf.Clamp((contactX - surfaceCentre.x)
                / Mathf.Max(0.0001f, halfWidth), -1f, 1f);
            contactY = surfaceCentre.y + halfDepth
                * Mathf.Sqrt(Mathf.Max(0f, 1f - across * across));
            // The final chord limits size; the effect must still fit the current narrow pool.
            float available = Mathf.Max(GeometryEpsilon,
                halfWidth - Mathf.Abs(contactX - surfaceCentre.x));
            effectWidth = Mathf.Min(capturedChord, Mathf.Min(halfWidth * 2f, available * 2.6f));
            worldZ = targetPosition.z - .012f;
            // Above an open rim, I clip X to its aperture instead of the closed polygon so raised drops keep their
            // tops.
            openMouth = target.mouthHalfWidth > GeometryEpsilon
                && Mathf.Abs(Mathf.DeltaAngle(targetTransform.eulerAngles.z, 0f)) < .05f;
            if (openMouth)
            {
                Vector3 left = targetTransform.TransformPoint(
                    new Vector3(target.mouthLocal.x - target.mouthHalfWidth,
                        target.mouthLocal.y, 0f));
                Vector3 right = targetTransform.TransformPoint(
                    new Vector3(target.mouthLocal.x + target.mouthHalfWidth,
                        target.mouthLocal.y, 0f));
                Vector3 mouth = targetTransform.TransformPoint(
                    new Vector3(target.mouthLocal.x, target.mouthLocal.y, 0f));
                mouthY = mouth.y;
                mouthLeft = Mathf.Min(left.x, right.x);
                mouthRight = Mathf.Max(left.x, right.x);
            }
            Matrix4x4 targetLocalToWorld = targetTransform.localToWorldMatrix;
            if (!worldInteriorValid
                || !MatricesExactlyEqual(cachedTargetLocalToWorld, targetLocalToWorld))
            {
                for (int i = 0; i < localInterior.Length; i++)
                {
                    Vector3 p = targetTransform.TransformPoint(localInterior[i]);
                    worldInterior[i] = new Vector2(p.x, p.y);
                }
                cachedTargetLocalToWorld = targetLocalToWorld;
                worldInteriorValid = true;
            }
            for (int i = 0; i < ArcSegments; i++)
            {
                Vector2 unit = UnitCircle[i];
                capPolygon[i] = surfaceCentre + new Vector2(
                    unit.x * halfWidth, unit.y * halfDepth);
            }
            return effectWidth > GeometryEpsilon;
        }

        /// <summary>The measured collapse falls 61 px in 0.06 s, then 13 px in 0.25 s. I preserve that fast drop and slow settle instead of using one smoothstep.</summary>
        private static float CollapseFall(float settle)
        {
            const float knee = .19f;
            const float share = .82f;
            return settle <= knee
                ? share * Smooth(0f, knee, settle)
                : share + (1f - share) * Smooth(knee, 1f, settle);
        }

        private void EvaluateCrest(float arrival, float tail, float settle)
        {
            if (settle > 0f)
            {
                // I widen the crown as it drops so it spreads toward the walls instead of shrinking in place.
                crestSpread = Mathf.Lerp(1f, CrestCollapseSpread, Smooth(0f, .55f, settle));
                float fall = CollapseFall(settle);
                for (int i = 0; i < crest.Length; i++)
                    crest[i] = RaisedCrest[i] * (1f - fall);
                return;
            }
            crestSpread = 1f;

            float[] a, b;
            float blend;
            if (arrival <= ContactKey116)
            { a = FirstCrest; b = FirstCrest; blend = 0f; }
            else if (arrival <= ContactKey120)
            { a = FirstCrest; b = EarlyCrest; blend = Smooth(ContactKey116, ContactKey120, arrival); }
            else if (arrival <= ContactKey126)
            { a = EarlyCrest; b = FedCrest; blend = Smooth(ContactKey120, ContactKey126, arrival); }
            else if (arrival <= ContactKey133)
            { a = FedCrest; b = LastFeedCrest; blend = Smooth(ContactKey126, ContactKey133, arrival); }
            else
            { a = LastFeedCrest; b = LastFeedCrest; blend = 0f; }

            // Only the final 1/9 of tail travel exposes the two shoulders; the stream owns the tall centre until
            // then.
            float tailBlend = Smooth(TailKey133, 1f, tail);
            for (int i = 0; i < crest.Length; i++)
            {
                float arrivingHeight = Mathf.Lerp(a[i], b[i], blend);
                crest[i] = Mathf.Lerp(arrivingHeight, RaisedCrest[i], tailBlend);
            }
        }

        private void BuildCrest(float alpha)
        {
            Vector2 previous = CrestPoint(0, 0f);
            float previousRoot = CrestRoot(previous);
            for (int segment = 0; segment < CrestX.Length - 1; segment++)
            {
                for (int sample = 1; sample <= CurveSamples; sample++)
                {
                    Vector2 next = CrestPoint(segment, sample / (float)CurveSamples);
                    float nextRoot = CrestRoot(next);
                    if (previousRoot > .0001f || nextRoot > .0001f)
                    {
                        Vector2 bottomA = CrestFoot(previous.x, previousRoot);
                        Vector2 bottomB = CrestFoot(next.x, nextRoot);
                        AddQuad(new FxVertex(previous, WithAlpha(faceColor, alpha)),
                            new FxVertex(next, WithAlpha(faceColor, alpha)),
                            new FxVertex(bottomB, WithAlpha(shadeColor, alpha * .94f)),
                            new FxVertex(bottomA, WithAlpha(shadeColor, alpha * .94f)), false);
                        Vector2 normal = new Vector2(-(next - previous).y,
                            (next - previous).x).normalized * (effectWidth * .006f);
                        AddQuad(new FxVertex(previous, WithAlpha(faceColor, alpha)),
                            new FxVertex(next, WithAlpha(faceColor, alpha)),
                            new FxVertex(next + normal, WithAlpha(faceColor, 0f)),
                            new FxVertex(previous + normal, WithAlpha(faceColor, 0f)), false);
                    }
                    previous = next;
                    previousRoot = nextRoot;
                }
            }
        }

        private Vector2 CrestPoint(int segment, float t)
        {
            int last = CrestX.Length - 1;
            Vector2 p0 = new Vector2(CrestX[Mathf.Max(0, segment - 1)], crest[Mathf.Max(0, segment - 1)]);
            Vector2 p1 = new Vector2(CrestX[segment], crest[segment]);
            Vector2 p2 = new Vector2(CrestX[segment + 1], crest[segment + 1]);
            Vector2 p3 = new Vector2(CrestX[Mathf.Min(last, segment + 2)], crest[Mathf.Min(last, segment + 2)]);
            Vector2 local = Bezier(p1, p1 + (p2 - p0) / 6f,
                p2 - (p3 - p1) / 6f, p2, t);
            return new Vector2(contactX + local.x * effectWidth * crestSpread,
                contactY + Mathf.Max(0f, local.y) * effectWidth);
        }

        private float CrestRoot(Vector2 point)
        {
            float height = Mathf.Max(0f, point.y - contactY);
            return Mathf.Clamp01(height / Mathf.Max(effectWidth * .131579f,
                GeometryEpsilon));
        }

        private Vector2 CrestFoot(float x, float root)
        {
            float across = Mathf.Clamp((x - cupCentre.x) / Mathf.Max(cupRadiusX, GeometryEpsilon), -1f, 1f);
            Vector2 deep = new Vector2(x, cupCentre.y - cupRadiusY * .82f
                * Mathf.Sqrt(Mathf.Max(0f, 1f - across * across)));
            return Vector2.Lerp(new Vector2(x, contactY), deep, root);
        }

        /// <summary>I read stroke timing from the parent timeline for repeatable forward playback. Captured state and Clear make backward or repeated scrubbing unsafe.</summary>
        private void BuildCrestSheen(float arrival, float settle, float alpha)
        {
            if (alpha <= .001f) return;
            // I hide the strokes before the crest flattens so they cannot float above a settled surface.
            float life = settle > 0f
                ? 1f - Smooth(0f, .62f, settle)
                : Smooth(ContactKey116, ContactKey126, arrival);
            if (life <= .001f) return;

            // I stop churn when arrival reaches 1 and collapse starts, then fade the frozen strokes to avoid
            // post-fill wobble.
            float phase = settle > 0f ? 1f : arrival;
            float thickness = effectWidth * .0115f;
            for (int i = 0; i < SheenStrokes; i++)
            {
                float wave = Mathf.Sin((phase * SheenCycles + SheenPhase[i]) * Mathf.PI * 2f);
                float lane = SheenLaneX[i] + wave * .012f;
                float half = SheenSpan[i] * (.78f + .22f * wave);
                // I use the lowest headroom under each stroke so it cannot poke through a crest shoulder.
                float room = Mathf.Min(CrestLocalHeightAt(lane),
                    Mathf.Min(CrestLocalHeightAt(lane - half),
                        CrestLocalHeightAt(lane + half)));
                if (room <= .012f) continue;

                float mid = room * SheenLaneHeight[i];
                float bow = room * (.16f + .12f * wave);
                Vector2 a = SheenPoint(lane - half, mid * .82f);
                Vector2 b = SheenPoint(lane - half * .34f, mid + bow);
                Vector2 c = SheenPoint(lane + half * .34f, mid + bow * .55f);
                Vector2 d = SheenPoint(lane + half, mid * .82f);
                AddSheenStroke(a, b, c, d, thickness * (.80f + .20f * wave),
                    alpha * life * (.52f + .22f * Mathf.Abs(wave)));
            }
        }

        /// <summary>Crest height at a normalized X, in the same effectWidth units.</summary>
        private float CrestLocalHeightAt(float localX)
        {
            int last = CrestX.Length - 1;
            if (localX <= CrestX[0] || localX >= CrestX[last]) return 0f;
            for (int i = 1; i <= last; i++)
            {
                if (localX > CrestX[i]) continue;
                float span = CrestX[i] - CrestX[i - 1];
                float t = span > GeometryEpsilon ? (localX - CrestX[i - 1]) / span : 1f;
                return Mathf.Max(0f, Mathf.Lerp(crest[i - 1], crest[i], t));
            }
            return 0f;
        }

        private Vector2 SheenPoint(float localX, float localY) =>
            new Vector2(contactX + localX * effectWidth,
                contactY + Mathf.Max(0f, localY) * effectWidth);

        /// <summary>A round ended ribbon: both tips reach zero width and zero alpha.</summary>
        private void AddSheenStroke(Vector2 a, Vector2 b, Vector2 c, Vector2 d,
            float width, float alpha)
        {
            Vector2 previous = a;
            float previousWidth = 0f;
            float previousAlpha = 0f;
            for (int i = 1; i <= SheenSamples; i++)
            {
                float t = i / (float)SheenSamples;
                Vector2 point = Bezier(a, b, c, d, t);
                Vector2 step = point - previous;
                if (step.sqrMagnitude < SheenStep) continue;
                Vector2 normal = new Vector2(-step.y, step.x).normalized;
                float taper = Mathf.Sin(Mathf.PI * t);
                float currentWidth = width * taper * .5f;
                float currentAlpha = alpha * taper;
                Color near = WithAlpha(lightColor, previousAlpha);
                Color far = WithAlpha(lightColor, currentAlpha);
                AddQuad(
                    new FxVertex(previous - normal * previousWidth, near),
                    new FxVertex(point - normal * currentWidth, far),
                    new FxVertex(point + normal * currentWidth, far),
                    new FxVertex(previous + normal * previousWidth, near), false);
                previous = point;
                previousWidth = currentWidth;
                previousAlpha = currentAlpha;
            }
        }

        private void BuildDrops(float arrival, float tail, float settle, float visibility)
        {
            // Drop A separates from the right root first, passes the short stem and vanishes at the rim.
            Vector2 first;
            float firstAlpha;
            float firstRadiusX;
            float firstRadiusY;
            if (settle > 0f)
            {
                // After the column clears, I keep the drop's separation X and let it fall into the surface.
                first = Vector2.Lerp(new Vector2(.24229f, .27269f),
                    new Vector2(.25600f, 0f), Smooth(0f, .30f, settle));
                firstAlpha = 1f - Smooth(.18f, .32f, settle);
                firstRadiusX = .03947f;
                firstRadiusY = .034f;
            }
            else if (tail >= TailKey126)
            {
                float attached = Smooth(ContactKey120, ContactKey126, arrival);
                Vector2 attachedPosition = Vector2.Lerp(new Vector2(.072f, .082f),
                    new Vector2(.12124f, .25705f), attached);
                float attachedRadiusX = Mathf.Lerp(.018f, .04605f, attached);
                float attachedRadiusY = Mathf.Lerp(.022f, .03947f, attached);
                if (tail <= TailKey133)
                    first = Vector2.Lerp(attachedPosition,
                        new Vector2(.22904f, .27534f),
                        Smooth(TailKey126, TailKey133, tail));
                else
                    first = Vector2.Lerp(new Vector2(.22904f, .27534f),
                        new Vector2(.24229f, .27269f),
                        Smooth(TailKey133, 1f, tail));
                float separation = Smooth(TailKey126, TailKey133, tail);
                firstAlpha = Mathf.Lerp(attached, 1f, separation);
                firstRadiusX = Mathf.Lerp(attachedRadiusX, .03947f, separation);
                firstRadiusY = Mathf.Lerp(attachedRadiusY, .03289f, separation);
            }
            else
            {
                float attached = Smooth(ContactKey120, ContactKey126, arrival);
                first = Vector2.Lerp(new Vector2(.072f, .082f),
                    new Vector2(.12124f, .25705f), attached);
                firstAlpha = attached;
                firstRadiusX = Mathf.Lerp(.018f, .04605f, attached);
                firstRadiusY = Mathf.Lerp(.022f, .03947f, attached);
            }
            firstAlpha *= visibility;
            if (firstAlpha > .001f)
                AddDrop(new Vector2(contactX, contactY) + first * effectWidth,
                    effectWidth * firstRadiusX, effectWidth * firstRadiusY, firstAlpha);

            // Drop B separates from the final short stem and stays near the centre until its last key.
            Vector2 second;
            float secondAlpha;
            if (settle > 0f)
            {
                // Drop B falls without sideways drift; its higher start makes it land later.
                second = Vector2.Lerp(new Vector2(.04555f, .34413f),
                    new Vector2(.05600f, 0f), Smooth(0f, .36f, settle));
                secondAlpha = 1f - Smooth(.22f, .38f, settle);
            }
            else
            {
                second = Vector2.Lerp(new Vector2(.03947f, .32895f),
                    new Vector2(.04555f, .34413f), Smooth(TailKey133, 1f, tail));
                secondAlpha = Smooth(.74f, TailKey133, tail);
            }
            secondAlpha *= visibility;
            if (secondAlpha > .001f)
                AddDrop(new Vector2(contactX, contactY) + second * effectWidth,
                    effectWidth * .03947f, effectWidth * .03289f, secondAlpha);
        }

        private void AddDrop(Vector2 centre, float radiusX, float radiusY, float alpha)
        {
            AddFeatheredEllipse(centre, radiusX, radiusY,
                WithAlpha(faceColor, alpha), effectWidth * .006f);
            float radius = Mathf.Min(radiusX, radiusY);
            AddEllipseFill(centre + new Vector2(-.22f, .24f) * radius,
                radius * .30f, radius * .23f,
                WithAlpha(lightColor, alpha * .55f), false);
        }

        private void AddFeatheredEllipse(Vector2 centre, float radiusX, float radiusY,
            Color color, float feather)
        {
            Color transparent = WithAlpha(color, 0f);
            FxVertex middle = new FxVertex(centre, color);
            for (int i = 0; i < ArcSegments; i++)
            {
                Vector2 va = UnitCircle[i];
                Vector2 vb = UnitCircle[i + 1];
                FxVertex innerA = new FxVertex(centre + Vector2.Scale(va, new Vector2(radiusX, radiusY)), color);
                FxVertex innerB = new FxVertex(centre + Vector2.Scale(vb, new Vector2(radiusX, radiusY)), color);
                AddTriangle(middle, innerA, innerB, false);
                AddQuad(innerA, innerB,
                    new FxVertex(innerB.position + vb * feather, transparent),
                    new FxVertex(innerA.position + va * feather, transparent), false);
            }
        }

        private void AddEllipseFill(Vector2 centre, float radiusX, float radiusY, Color color, bool capOnly)
        {
            FxVertex middle = new FxVertex(centre, color);
            for (int i = 0; i < ArcSegments; i++)
            {
                Vector2 va = UnitCircle[i];
                Vector2 vb = UnitCircle[i + 1];
                AddTriangle(middle,
                    new FxVertex(centre + new Vector2(va.x * radiusX, va.y * radiusY), color),
                    new FxVertex(centre + new Vector2(vb.x * radiusX, vb.y * radiusY), color), capOnly);
            }
        }

        private void AddEllipseArc(Vector2 centre, float radiusX, float radiusY,
            float from, float to, float thickness, Color color, bool capOnly)
        {
            const int segments = 26;
            float a = Mathf.Lerp(from, to, 0f);
            float cosA = Mathf.Cos(a);
            float sinA = Mathf.Sin(a);
            for (int i = 0; i < segments; i++)
            {
                float b = Mathf.Lerp(from, to, (i + 1) / (float)segments);
                float cosB = Mathf.Cos(b);
                float sinB = Mathf.Sin(b);
                Vector2 pa = centre + new Vector2(cosA * radiusX, sinA * radiusY);
                Vector2 pb = centre + new Vector2(cosB * radiusX, sinB * radiusY);
                Vector2 na = new Vector2(cosA / Mathf.Max(radiusX, GeometryEpsilon),
                    sinA / Mathf.Max(radiusY, GeometryEpsilon)).normalized * thickness * .5f;
                Vector2 nb = new Vector2(cosB / Mathf.Max(radiusX, GeometryEpsilon),
                    sinB / Mathf.Max(radiusY, GeometryEpsilon)).normalized * thickness * .5f;
                AddQuad(new FxVertex(pa - na, color), new FxVertex(pb - nb, color),
                    new FxVertex(pb + nb, color), new FxVertex(pa + na, color), capOnly);
                cosA = cosB;
                sinA = sinB;
            }
        }

        private void AddQuad(FxVertex a, FxVertex b, FxVertex c, FxVertex d, bool capOnly)
        {
            AddTriangle(a, b, c, capOnly);
            AddTriangle(a, c, d, capOnly);
        }

        // I clip triangles to the vessel and cap-only rings to the live ellipse. One triangulation also handles
        // concave and asymmetric bowls.
        private void AddTriangle(FxVertex a, FxVertex b, FxVertex c, bool capOnly)
        {
            float minimumArea = Mathf.Max(1e-14f, effectWidth * effectWidth * 1e-8f);
            if (Mathf.Abs(Cross(b.position - a.position, c.position - a.position)) < minimumArea)
                return;
            clipA[0] = a; clipA[1] = b; clipA[2] = c;
            FxVertex[] input = clipA, output = clipB;
            int count = 3;
            if (capOnly)
            {
                for (int i = 0; i < ArcSegments && count > 0; i++)
                {
                    count = ClipEdge(input, count, output, capPolygon[i],
                        capPolygon[(i + 1) % ArcSegments], 1f);
                    FxVertex[] swap = input; input = output; output = swap;
                }
            }
            if (count < 3) return;
            for (int i = 0; i < count; i++) shape[i] = input[i];
            int shapeCount = count;
            Vector2 low = shape[0].position, high = low;
            for (int i = 1; i < shapeCount; i++)
            { low = Vector2.Min(low, shape[i].position); high = Vector2.Max(high, shape[i].position); }
            for (int i = 0; i < interiorTriangles.Count; i += 3)
            {
                Vector2 v0 = worldInterior[interiorTriangles[i]];
                Vector2 v1 = worldInterior[interiorTriangles[i + 1]];
                Vector2 v2 = worldInterior[interiorTriangles[i + 2]];
                Vector2 triLow = Vector2.Min(v0, Vector2.Min(v1, v2));
                Vector2 triHigh = Vector2.Max(v0, Vector2.Max(v1, v2));
                if (high.x < triLow.x || low.x > triHigh.x || high.y < triLow.y || low.y > triHigh.y) continue;
                int interiorCount;
                if (openMouth && !capOnly)
                    interiorCount = ClipEdge(shape, shapeCount, clipA,
                        new Vector2(mouthLeft, mouthY), new Vector2(mouthRight, mouthY), -1f);
                else
                {
                    for (int j = 0; j < shapeCount; j++) clipA[j] = shape[j];
                    interiorCount = shapeCount;
                }
                float winding = Cross(v1 - v0, v2 - v0) >= 0f ? 1f : -1f;
                count = ClipEdge(clipA, interiorCount, clipB, v0, v1, winding);
                count = ClipEdge(clipB, count, clipA, v1, v2, winding);
                count = ClipEdge(clipA, count, clipB, v2, v0, winding);
                AppendPolygon(clipB, count);
            }
            if (openMouth && !capOnly)
            {
                // This half-plane excludes the interior pass so rear-rim overlap cannot double the alpha.
                count = ClipEdge(shape, shapeCount, clipA,
                    new Vector2(mouthLeft, mouthY), new Vector2(mouthRight, mouthY), 1f);
                count = ClipEdge(clipA, count, clipB,
                    new Vector2(mouthRight, mouthY), new Vector2(mouthRight, mouthY + 1f), 1f);
                count = ClipEdge(clipB, count, clipA,
                    new Vector2(mouthLeft, mouthY + 1f), new Vector2(mouthLeft, mouthY), 1f);
                AppendPolygon(clipA, count);
            }
        }

        private static int ClipEdge(FxVertex[] input, int count, FxVertex[] output,
            Vector2 a, Vector2 b, float winding)
        {
            if (count == 0) return 0;
            int written = 0;
            FxVertex previous = input[count - 1];
            float previousDistance = Cross(b - a, previous.position - a) * winding;
            for (int i = 0; i < count; i++)
            {
                FxVertex current = input[i];
                float distance = Cross(b - a, current.position - a) * winding;
                bool wasInside = previousDistance >= 0f;
                bool isInside = distance >= 0f;
                if (wasInside != isInside)
                {
                    float t = Mathf.Clamp01(previousDistance / (previousDistance - distance));
                    if (written < ClipCapacity)
                        output[written++] = new FxVertex(Vector2.Lerp(previous.position, current.position, t),
                            Color.Lerp(previous.color, current.color, t));
                }
                if (isInside && written < ClipCapacity) output[written++] = current;
                previous = current;
                previousDistance = distance;
            }
            return written;
        }

        private void AppendPolygon(FxVertex[] polygon, int count)
        {
            if (count < 3 || vertices.Count + count > MaxVertices) return;
            int first = vertices.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 p = polygon[i].position;
                vertices.Add(frameWorldToLocal.MultiplyPoint3x4(
                    new Vector3(p.x, p.y, worldZ)));
                colors.Add(polygon[i].color);
                uvs.Add(new Vector2(.5f, .5f));
            }
            for (int i = 1; i < count - 1; i++)
            { indices.Add(first); indices.Add(first + i); indices.Add(first + i + 1); }
        }

        private void PrepareInterior()
        {
            worldInteriorValid = false;
            Vector2[] polygon = target.InteriorPolygon;
            int count = polygon != null ? polygon.Length : 0;
            if (count > 1 && (polygon[0] - polygon[count - 1]).sqrMagnitude < GeometryEpsilon * GeometryEpsilon)
                count--;
            if (localInterior == null || localInterior.Length != count)
            { localInterior = new Vector2[count]; worldInterior = new Vector2[count]; }
            for (int i = 0; i < count; i++) localInterior[i] = polygon[i];
            interiorTriangles.Clear(); earIndices.Clear();
            float area = 0f;
            for (int i = 0; i < count; i++)
            { area += Cross(localInterior[i], localInterior[(i + 1) % count]); earIndices.Add(i); }
            float winding = area >= 0f ? 1f : -1f;
            int guard = count * count;
            while (earIndices.Count > 3 && guard-- > 0)
            {
                bool cut = false;
                for (int i = 0; i < earIndices.Count; i++)
                {
                    int ia = earIndices[(i + earIndices.Count - 1) % earIndices.Count];
                    int ib = earIndices[i];
                    int ic = earIndices[(i + 1) % earIndices.Count];
                    Vector2 a = localInterior[ia], b = localInterior[ib], c = localInterior[ic];
                    float bend = Cross(b - a, c - b) * winding;
                    if (Mathf.Abs(bend) <= GeometryEpsilon)
                    { earIndices.RemoveAt(i); cut = true; break; }
                    if (bend < 0f) continue;
                    bool occupied = false;
                    for (int j = 0; j < earIndices.Count; j++)
                    {
                        int index = earIndices[j];
                        if (index == ia || index == ib || index == ic) continue;
                        Vector2 p = localInterior[index];
                        if (Cross(b - a, p - a) * winding >= 0f
                            && Cross(c - b, p - b) * winding >= 0f
                            && Cross(a - c, p - c) * winding >= 0f)
                        { occupied = true; break; }
                    }
                    if (occupied) continue;
                    interiorTriangles.Add(ia); interiorTriangles.Add(ib); interiorTriangles.Add(ic);
                    earIndices.RemoveAt(i); cut = true; break;
                }
                if (!cut) break;
            }
            if (earIndices.Count == 3)
            { interiorTriangles.Add(earIndices[0]); interiorTriangles.Add(earIndices[1]); interiorTriangles.Add(earIndices[2]); }
            else interiorTriangles.Clear();
        }

        private static Vector2[] BuildUnitCircle()
        {
            var samples = new Vector2[ArcSegments + 1];
            for (int i = 0; i < samples.Length; i++)
            {
                float angle = i * (Mathf.PI * 2f / ArcSegments);
                samples[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            return samples;
        }

        private static bool MatricesExactlyEqual(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static float ArrivalKey(float arrival, float at116, float at120,
            float at126, float at133)
        {
            if (arrival <= ContactKey116) return at116;
            if (arrival <= ContactKey120)
                return Mathf.Lerp(at116, at120,
                    Smooth(ContactKey116, ContactKey120, arrival));
            if (arrival <= ContactKey126)
                return Mathf.Lerp(at120, at126,
                    Smooth(ContactKey120, ContactKey126, arrival));
            if (arrival <= ContactKey133)
                return Mathf.Lerp(at126, at133,
                    Smooth(ContactKey126, ContactKey133, arrival));
            return at133;
        }
        private static float Smooth(float low, float high, float value)
        { float t = Mathf.Clamp01((value - low) / Mathf.Max(high - low, GeometryEpsilon)); return t * t * (3f - 2f * t); }
        private static Color WithAlpha(Color color, float alpha) { color.a = alpha; return color; }
        private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        { float u = 1f - t; return u * u * u * a + 3f * u * u * t * b + 3f * u * t * t * c + t * t * t * d; }
        private void OnDisable() => Clear();
        private void OnDestroy()
        {
            if (filter != null && filter.sharedMesh == mesh) filter.sharedMesh = null;
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
            mesh = null;
        }
    }
}
