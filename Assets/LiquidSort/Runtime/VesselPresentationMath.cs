using UnityEngine;

namespace LiquidSort
{
    /// <summary>I keep three length types separate: vessel-local lengths use PlanarWorldScale; Royal-world distances use rho = current scale / reference scale. Pixel lengths use RoyalLocalUnitsPerPixel from the fixed 5.25 orthographic, 1080x1920 framing.</summary>
    public static class VesselPresentationMath
    {
        private const float MinimumScale = 0.0001f;

        /// <summary>Orthographic size of the canonical RoyalGlassLab camera.</summary>
        public const float RoyalOrthographicSize = 5.25f;

        /// <summary>Pixel height RoyalGlassLab's presentation was framed against.</summary>
        public const float RoyalFramePixelHeight = 1920f;

        /// <summary>World units per Royal reference pixel, independent of the current device.</summary>
        public const float RoyalWorldUnitsPerPixel =
            2f * RoyalOrthographicSize / RoyalFramePixelHeight;

        /// <summary>I use the square root of transformed XY area for planar world scale, preserving rotation and nested uniform scaling.</summary>
        public static float PlanarWorldScale(Transform vessel)
        {
            if (vessel == null) return 1f;

            Vector3 worldRight = vessel.TransformVector(Vector3.right);
            Vector3 worldUp = vessel.TransformVector(Vector3.up);
            float worldArea = Vector3.Cross(worldRight, worldUp).magnitude;
            return Mathf.Max(MinimumScale, Mathf.Sqrt(worldArea));
        }

        /// <summary>I find the root position that maps an asset-local point to a world point without changing local poses.</summary>
        public static Vector3 RootPositionForAnchoredPoint(
            Transform movingRoot, Transform assetSpace, Vector3 assetLocalPoint,
            Vector3 desiredWorldPoint)
        {
            if (movingRoot == null || assetSpace == null) return desiredWorldPoint;
            Vector3 rootToAnchor = assetSpace.TransformPoint(assetLocalPoint)
                                 - movingRoot.position;
            return desiredWorldPoint - rootToAnchor;
        }

        /// <summary>Board scale ratio: rho = current world scale / profile reference scale; RoyalGlassLab is 1.</summary>
        public static float RelativeToRoyalReference(Transform vessel, VesselProfile profile)
        {
            if (vessel == null) return 1f;
            float referenceScale = profile != null ? profile.ShelfReferenceScale : 1f;
            return Mathf.Max(MinimumScale,
                PlanarWorldScale(vessel) / referenceScale);
        }

        /// <summary>I scale a RoyalGlassLab world distance for the current board.</summary>
        public static float ReferenceDistance(float royalWorldDistance,
                                              float relativeScale) =>
            royalWorldDistance * Mathf.Max(MinimumScale, relativeScale);

        /// <summary>I convert a Royal reference pixel to vessel-local units so insets keep the same share of the glass at any screen or board size.</summary>
        public static float RoyalLocalUnitsPerPixel(VesselProfile profile) =>
            RoyalWorldUnitsPerPixel
            / Mathf.Max(MinimumScale,
                profile != null ? profile.ShelfReferenceScale : 1f);

        /// <summary>An authored pixel length converted to Royal-proportional vessel-local units.</summary>
        public static float RoyalPixelsToLocal(float pixels, VesselProfile profile) =>
            pixels * RoyalLocalUnitsPerPixel(profile);

        /// <summary>I scale a distance from its original board scale so it keeps the same share of the vessel on another board.</summary>
        public static float RescaleAuthoredLength(float authoredLength,
                                                  float authoredAtScale,
                                                  float currentScale) =>
            authoredLength * Mathf.Max(MinimumScale, currentScale)
                           / Mathf.Max(MinimumScale, authoredAtScale);
    }
}
