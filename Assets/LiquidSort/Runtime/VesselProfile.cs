using UnityEngine;

namespace LiquidSort
{
    [CreateAssetMenu(menuName = "LiquidSort/Vessel Profile", fileName = "VesselProfile")]
    public sealed class VesselProfile : ScriptableObject
    {
        public const int LiquidFloorSampleCount = 32;

        [Header("Art")]
        [Tooltip("The drawing the interior is traced from, and the glass drawn in front of the liquid.")]
        public Sprite front;
        [Tooltip("Optional source for baked vessel geometry.")]
        public Sprite traceSource;
        [Header("Floating garnish")]
        [Tooltip("Optional garnish that follows liquid.")]
        public Sprite floatingGarnish;
        [Tooltip("Optional second floating garnish sprite.")]
        public Sprite floatingGarnishSecondary;
        [Tooltip("Optional shared material for liquid-aware presentation, such as tinting the submerged half of ice.")]
        public Material floatingGarnishMaterial;
        [Tooltip("Maximum garnish width share.")]
        [Range(0.10f, 1f)] public float floatingGarnishWidthShare = 0.60f;
        [Tooltip("Maximum surface width used by garnish.")]
        [Range(0.50f, 1f)] public float floatingGarnishChordShare = 0.80f;
        [Tooltip("Horizontal offset from the live surface centre as a share of interior width.")]
        [Range(-0.25f, 0.25f)] public float floatingGarnishHorizontalOffsetShare = -0.02f;
        [Tooltip("How far the garnish centre sits above the liquid line, as a share of its rendered height.")]
        [Range(-0.30f, 0.40f)] public float floatingGarnishSurfaceLiftShare = 0.18f;
        [Tooltip("Settled opacity. Translucent ice should stay below one so the drink colour reads through it.")]
        [Range(0f, 1f)] public float floatingGarnishOpacity = 0.74f;
        [Tooltip("Idle vertical drift as a share of the vessel interior width. Zero disables the motion.")]
        [Range(0f, 0.03f)] public float floatingGarnishIdleBobShare;
        [Tooltip("Idle horizontal sweep across the vessel.")]
        [Range(0f, 0.05f)] public float floatingGarnishIdleDriftShare;
        [Tooltip("Seconds for one complete idle down-and-up cycle.")]
        [Range(0.75f, 4f)] public float floatingGarnishIdleBobPeriod = 1.75f;
        [Tooltip("Very small idle roll paired with the vertical drift. Zero keeps the garnish level.")]
        [Range(0f, 2f)] public float floatingGarnishIdleRockDegrees;
        [Tooltip("Liquid-only garnish reflection strength.")]
        [Range(0f, 0.30f)] public float floatingGarnishLiquidLightStrength = 0.14f;
        [Tooltip("Draw order between Liquid (1) and FrontGlass (5).")]
        public int floatingGarnishSortingOrder = 2;
        [Header("Liquid idle detail")]
        [Tooltip("Bubble strength inside settled liquid.")]
        [Range(0f, 1f)] public float ambientRisingBubbleStrength;
        [Header("Cold glass condensation")]
        [Tooltip("Optional transparent droplet cluster drawn on the outside/front of an iced glass.")]
        public Sprite condensationGarnish;
        [Tooltip("Optional shared material used to remove low-alpha glow and guarantee pale, non-black droplet edges.")]
        public Material condensationGarnishMaterial;
        [Tooltip("Droplet-cluster width as a share of the vessel interior width.")]
        [Range(0.05f, 0.35f)] public float condensationWidthShare = 0.18f;
        [Tooltip("Cluster anchor from the interior centre, measured as shares of interior width and height.")]
        public Vector2 condensationPositionShare = new Vector2(0.27f, 0.16f);
        [Tooltip("Settled condensation opacity. It follows the ice visibility during a pour.")]
        [Range(0f, 1f)] public float condensationOpacity = 0.42f;
        [Tooltip("Draw order above FrontGlass (5) so droplets read on the outside surface.")]
        public int condensationSortingOrder = 6;
        [Tooltip("Shared liquid material for this profile.")]
        public Material liquidMaterial;
        [Tooltip("Shared glass contour material.")]
        public Material contourMaterial;
        [Tooltip("Optional additive glass-light material.")]
        public Material thinGlassFxMaterial;
        [Tooltip("Edge-width multiplier for authored front.")]
        [Min(0.1f)] public float authoredFrontEdgeWidthScale = 1f;
        [Tooltip("Extra front-glass width in design pixels.")]
        [Range(0f, 16f)] public float authoredFrontWidthExpansion;
        [Tooltip("Reduces broad front reflections.")]
        [Range(0f, 1f)] public float authoredReflectionTrim;
        [Header("Glass-part reflections")]
        [Tooltip("Outer-glass highlight strength.")]
        [Range(0f, 1f)] public float handleGlassLight;
        [Tooltip("Artist-authored highlight strength below the liquid cavity, such as a cocktail stem and foot.")]
        [Range(0f, 1f)] public float stemFootGlassLight;
        [Tooltip("Restores solid stem and foot opacity.")]
        [Range(0f, 1f)] public float stemFootGlassBacking;
        [Tooltip("Applies toy-style shading below the bowl.")]
        [Range(0f, 1f)] public float stemFootToonStrength;
        [Tooltip("Softness of the cavity-to-accessory boundary as a share of interior width.")]
        [Range(0.005f, 0.10f)] public float accessoryGlassLightFeather = 0.025f;
        [Tooltip("Lower-glass key/fill highlight.")]
        [Range(0f, 1f)] public float bottomRimGlassLight;
        [Tooltip("Restores heavy-base opacity.")]
        [Range(0f, 1f)] public float bottomGlassBacking;
        [Tooltip("Liquid reflection share for this vessel.")]
        [Range(0f, 1f)] public float liquidBounceScale = 1f;
        [Header("Baked art")]
        [Tooltip("Interior coverage, baked. The liquid shader clips against this.")]
        public Texture2D interiorMask;

        [Header("Interior correction")]
        [Tooltip("Clips liquid cavity at the right wall.")]
        public bool clipRightInterior;
        [Tooltip("Right interior-wall x at local y=0.")]
        public float rightInteriorXAtY0;
        [Tooltip("How much the right interior-wall limit moves in x for one local unit of y.")]
        public float rightInteriorSlope;

        [Header("Pour pose")]
        [Tooltip("Only pose belongs to the vessel. Timing stays shared so every transfer keeps the same rhythm.")]
        public PourPose pourPose = PourPose.Default;

        [Header("Contents")]
        [Range(1, 8)] public int capacity = 2;

        [Header("Baked geometry, in vessel local units")]
        public Vector2[] interiorPolygon;
        public Rect interiorBounds;
        public float polygonArea;
        [Tooltip("Purely visual inner wall. The artwork draws two contours; liquid belongs "
               + "inside the second one, so this is where the drawn edge stops - sides, "
               + "floor and rim as a single closed mould. It feeds the interior mask and "
               + "nothing else. Capacity, band waterlines, the top ellipse, spill angles "
               + "and both baked tables all keep measuring against interiorPolygon, which "
               + "still runs wide under the stroke, so correcting the look here cannot "
               + "move a liquid height the player has already seen. Empty falls back to "
               + "interiorPolygon, which is the old behaviour exactly.")]
        public Vector2[] interiorRenderPolygon;
        [Tooltip("Where liquid leaves the vessel. x is 0 for a narrow neck.")]
        public Vector2 mouthLocal;
        [Tooltip("Half width of an open rim. Zero means a single centred mouth.")]
        public float mouthHalfWidth;
        [Tooltip("Baked contact point where this vessel's visible artwork stands on a shelf. "
               + "It belongs to the asset, so scene placement can never redefine the foot.")]
        public Vector2 supportLocal;
        [Tooltip("True when supportLocal was measured from the front sprite's visible alpha.")]
        public bool hasSupportLocal;
        [Header("Shelf presentation")]
        [Tooltip("Canonical uniform scale used by RoyalGlassLab before a level applies its "
               + "board-wide fit. This belongs to the vessel asset, never to a scene clone.")]
        [Min(0.01f)] public float shelfReferenceScale = 1f;
        [Tooltip("Vertical presentation multiplier. Extends the artwork and liquid together "
               + "without widening the vessel or changing its capacity.")]
        [Min(0.01f)] public float shelfReferenceHeightScale = 1f;
        [Tooltip("Canonical upright 2D pose used both in RoyalGlassLab and on gameplay "
               + "shelves. Levels may move a vessel, but cannot redefine its own pose.")]
        [Range(-180f, 180f)] public float shelfReferenceRotationDegrees;
        [Tooltip("Lowest broadly visible row of the cavity. The curved liquid floor below "
               + "is the authoritative render boundary when it has been baked.")]
        public float visibleBottomLocal;
        [Tooltip("Authoring opt-in for vessels whose artwork has a distinct inner-bottom "
               + "contour. Only opted-in profiles bake and use the curved liquid floor.")]
        public bool useLiquidFloorCurve;
        [Tooltip("True when the inner of the artwork's two bottom contours was sampled "
               + "column by column. Liquid is clipped above this curve, so the outer "
               + "glass base can never become part of the contents.")]
        public bool hasLiquidFloorCurve;
        [Tooltip("Local-x span covered by the baked inner-bottom contour.")]
        public Vector2 liquidFloorXRange;
        [Tooltip("Local-y samples of the inner-bottom contour, left to right.")]
        public float[] liquidFloorSamples;
        [Tooltip("Purely visual erosion of the baked cavity's lower edge, authored in "
               + "Royal reference pixels. It never changes the interior polygon, capacity, "
               + "band waterlines, top ellipse or pour geometry.")]
        [Range(0f, 8f)] public float bottomInteriorInsetPixels;
        [Tooltip("True after the baker produced the automatic optical-height table for this artwork.")]
        public bool hasVisibleLiquidFloor;
        [Tooltip("Start of baked optical-height map.")]
        public float visibleLiquidFloor;

        [Header("Look")]
        [Tooltip("Top face ellipse half depth as a fraction of the liquid's current span.")]
        [Range(0.02f, 0.20f)] public float surfaceBulge = 0.135f;
        [Range(0.01f, 0.30f)] public float maxCapDepth = 0.075f;
        [Tooltip("Empty interior share above a full vessel.")]
        [Range(0f, 0.50f)] public float brimHeadroom = 0.34f;
        [Tooltip("Surface-to-brim gap in face depths.")]
        [Range(0f, 8f)] public float brimGapCaps = 3.2f;
        [HideInInspector, Tooltip("Derives ceiling from baked geometry.")]
        public bool deriveSurfaceCeilingAtRuntime;
        [Range(0.50f, 1f)] public float maxFillFraction = 1f;
        [Range(0f, 1f)] public float surfaceAllowance = 0.8f;
        [Tooltip("Unit height versus volume balance.")]
        [Range(0f, 1f)] public float evenBandHeights = 1f;
        [Tooltip("Shared boundary curvature.")]
        [Range(0f, 1f)] public float innerJunctionCurve = 1f;
        [Range(0f, 0.25f)] public float innerJunctionDepth = 0.098f;

        [Header("Baked tables")]
        public TiltTable tilted;
        public UprightTable upright;

        public Rect QuadRect
        {
            get
            {
                float pad = 0.02f + interiorBounds.height * maxCapDepth
                            * Mathf.Clamp01(surfaceAllowance) * 1.15f;
                return new Rect(interiorBounds.xMin - pad, interiorBounds.yMin - pad,
                    interiorBounds.width + pad * 2f, interiorBounds.height + pad * 2f);
            }
        }

        public float DrawnFloorLocal
        {
            get
            {
                Vector2[] drawn = InteriorRenderPolygon;
                if (drawn == null || drawn.Length < 3) return interiorBounds.yMin;
                float floor = drawn[0].y;
                for (int i = 1; i < drawn.Length; i++) floor = Mathf.Min(floor, drawn[i].y);
                return floor;
            }
        }

        public Vector2[] InteriorRenderPolygon =>
            interiorRenderPolygon != null && interiorRenderPolygon.Length >= 3
                ? interiorRenderPolygon
                : interiorPolygon;

        public bool IsBaked => interiorPolygon != null && interiorPolygon.Length >= 3
                               && tilted != null && tilted.IsValid
                               && upright != null && upright.IsValid;

        public bool HasLiquidFloorCurve => useLiquidFloorCurve
            && hasLiquidFloorCurve
            && liquidFloorSamples != null
            && liquidFloorSamples.Length == LiquidFloorSampleCount
            && liquidFloorXRange.y > liquidFloorXRange.x + 1e-5f;

        public float LiquidFloorMinLocal
        {
            get
            {
                if (!HasLiquidFloorCurve) return visibleBottomLocal;
                float result = liquidFloorSamples[0];
                for (int i = 1; i < liquidFloorSamples.Length; i++)
                    result = Mathf.Min(result, liquidFloorSamples[i]);
                return result;
            }
        }

        public Vector2 SupportLocal
        {
            get
            {
                if (hasSupportLocal) return supportLocal;
                float bottom = front != null ? front.bounds.min.y : interiorBounds.yMin;
                return new Vector2(mouthLocal.x, bottom);
            }
        }

        public float ShelfReferenceScale => Mathf.Max(0.01f, shelfReferenceScale);

        public float ShelfReferenceHeightScale =>
            shelfReferenceHeightScale > 0f ? shelfReferenceHeightScale : 1f;

        public float AuthoredFrontEdgeWidthScale =>
            authoredFrontEdgeWidthScale > 0f ? authoredFrontEdgeWidthScale : 1f;

        public float AuthoredFrontWidthExpansion =>
            Mathf.Clamp(authoredFrontWidthExpansion, 0f, 16f);

        public float AuthoredReflectionTrim =>
            Mathf.Clamp01(authoredReflectionTrim);

        public Vector3 ShelfReferenceLocalScale =>
            new Vector3(1f, ShelfReferenceHeightScale, 1f) * ShelfReferenceScale;

        public Quaternion ShelfReferenceLocalRotation =>
            Quaternion.Euler(0f, 0f, shelfReferenceRotationDegrees);

        [System.Serializable]
        public struct PourPose
        {
            [Tooltip("Use these values instead of PourAnimator's neutral fallback.")]
            public bool enabled;
            [Tooltip("Vertical gap between this vessel's mouth and an incoming source mouth.")]
            [Range(0.05f, 1.20f)] public float receiveClearance;
            [Tooltip("Height of the carry arc above a direct trip to the target.")]
            [Range(0.02f, 0.50f)] public float carryArc;
            [Tooltip("Degrees beyond the first geometrically valid spill angle.")]
            [Range(0f, 30f)] public float extraTilt;
            [Tooltip("Visual cap for the last part of the pour.")]
            [Range(70f, 120f)] public float maximumTilt;
            [Tooltip("Main falling column width.")]
            [Range(0.02f, 0.20f)] public float streamWidth;
            [Tooltip("Width at the leading edge and final drop.")]
            [Range(0.01f, 0.16f)] public float streamTipWidth;

            public static PourPose Default => new PourPose
            {
                enabled = false,
                receiveClearance = 0.81f,
                carryArc = 0.20f,
                extraTilt = 8f,
                maximumTilt = 96f,
                streamWidth = 0.085f,
                streamTipWidth = 0.055f
            };
        }

        [System.Serializable]
        public sealed class TiltTable
        {
            public int angleSteps;
            public int fillSteps;
            public float maxAngle;
            public float[] level;
            public float[] centreX;
            public float[] halfChord;
            [Tooltip("Maximum fill at each tilt.")]
            public float[] ceilingFill;

            public bool IsValid =>
                angleSteps > 1 && fillSteps > 1 && level != null &&
                level.Length == angleSteps * fillSteps &&
                centreX != null && centreX.Length == level.Length &&
                halfChord != null && halfChord.Length == level.Length &&
                ceilingFill != null && ceilingFill.Length == angleSteps;

            public void Sample(float angle, float fill, out float outLevel, out float outCentre, out float outHalf)
            {
                float a = Mathf.InverseLerp(-maxAngle, maxAngle, Mathf.Clamp(angle, -maxAngle, maxAngle))
                          * (angleSteps - 1);
                float f = Mathf.Clamp01(fill) * (fillSteps - 1);

                int a0 = Mathf.Clamp((int)a, 0, angleSteps - 1);
                int a1 = Mathf.Min(a0 + 1, angleSteps - 1);
                int f0 = Mathf.Clamp((int)f, 0, fillSteps - 1);
                int f1 = Mathf.Min(f0 + 1, fillSteps - 1);
                float ta = a - a0;
                float tf = f - f0;

                int i00 = a0 * fillSteps + f0, i01 = a0 * fillSteps + f1;
                int i10 = a1 * fillSteps + f0, i11 = a1 * fillSteps + f1;

                outLevel = Blend(level, i00, i01, i10, i11, ta, tf);
                outCentre = Blend(centreX, i00, i01, i10, i11, ta, tf);
                outHalf = Blend(halfChord, i00, i01, i10, i11, ta, tf);
            }

            public float CeilingFillAt(float angle)
            {
                float a = Mathf.InverseLerp(-maxAngle, maxAngle, Mathf.Clamp(angle, -maxAngle, maxAngle))
                          * (angleSteps - 1);
                int a0 = Mathf.Clamp((int)a, 0, angleSteps - 1);
                int a1 = Mathf.Min(a0 + 1, angleSteps - 1);
                return Mathf.Lerp(ceilingFill[a0], ceilingFill[a1], a - a0);
            }

            private static float Blend(float[] table, int i00, int i01, int i10, int i11, float ta, float tf) =>
                Mathf.Lerp(Mathf.Lerp(table[i00], table[i01], tf),
                           Mathf.Lerp(table[i10], table[i11], tf), ta);
        }

        [System.Serializable]
        public sealed class UprightTable
        {
            public int steps;
            public float minY;
            public float maxY;
            [Tooltip("Lowest level the liquid still reads at. Below this the vessel's own outline covers it.")]
            public float floorY;
            [Tooltip("Highest waterline a full vessel is allowed, with the brim headroom already applied.")]
            public float ceilingY;
            public float[] areaFraction;
            public float[] capHalfDepth;
            [Tooltip("Tilt at which a vessel of this fill first reaches its mouth, per fill step.")]
            public float[] spillAngle;
            [Tooltip("Baked visible height per upright level.")]
            public float[] visibleHeight;
            [Tooltip("Inverse baked visible-height table.")]
            public float[] levelAtVisibleFraction;
            public float totalVisibleHeight;

            public bool IsValid =>
                steps > 1 && areaFraction != null && areaFraction.Length == steps
                && capHalfDepth != null && capHalfDepth.Length == steps
                && spillAngle != null && spillAngle.Length == steps;

            public bool HasVisibleHeightMap =>
                steps > 1 && totalVisibleHeight > 1e-5f
                && visibleHeight != null && visibleHeight.Length == steps
                && levelAtVisibleFraction != null && levelAtVisibleFraction.Length == steps;

            public float AreaFractionAt(float level) => Read(areaFraction, level);

            public float CapHalfDepthAt(float level) => Read(capHalfDepth, level);

            public float VisibleHeightAt(float level)
            {
                if (!HasVisibleHeightMap)
                    return Mathf.Clamp(level - minY, 0f, Mathf.Max(0f, maxY - minY));
                return Read(visibleHeight, level);
            }

            public float LevelAtVisibleHeight(float height)
            {
                if (!HasVisibleHeightMap)
                    return Mathf.Clamp(minY + height, minY, maxY);

                float t = Mathf.Clamp01(height / totalVisibleHeight) * (steps - 1);
                int i0 = Mathf.Clamp((int)t, 0, steps - 1);
                int i1 = Mathf.Min(i0 + 1, steps - 1);
                return Mathf.Lerp(levelAtVisibleFraction[i0],
                    levelAtVisibleFraction[i1], t - i0);
            }

            public float SpillAngleFor(float fill)
            {
                float t = Mathf.Clamp01(fill) * (steps - 1);
                int i0 = Mathf.Clamp((int)t, 0, steps - 1);
                int i1 = Mathf.Min(i0 + 1, steps - 1);
                return Mathf.Lerp(spillAngle[i0], spillAngle[i1], t - i0);
            }

            private float Read(float[] table, float level)
            {
                float t = Mathf.InverseLerp(minY, maxY, level) * (steps - 1);
                t = Mathf.Clamp(t, 0f, steps - 1);
                int i0 = (int)t;
                int i1 = Mathf.Min(i0 + 1, steps - 1);
                return Mathf.Lerp(table[i0], table[i1], t - i0);
            }
        }
    }
}
