using UnityEngine;

namespace LiquidSort
{
    public static class VesselPresentationMath
    {
        private const float MinimumScale = 0.0001f;

        public const float RoyalOrthographicSize = 5.25f;

        public const float RoyalFramePixelHeight = 1920f;

        public const float RoyalWorldUnitsPerPixel =
            2f * RoyalOrthographicSize / RoyalFramePixelHeight;

        public static float PlanarWorldScale(Transform vessel)
        {
            if (vessel == null) return 1f;

            Vector3 worldRight = vessel.TransformVector(Vector3.right);
            Vector3 worldUp = vessel.TransformVector(Vector3.up);
            float worldArea = Vector3.Cross(worldRight, worldUp).magnitude;
            return Mathf.Max(MinimumScale, Mathf.Sqrt(worldArea));
        }

        public static Vector3 RootPositionForAnchoredPoint(
            Transform movingRoot, Transform assetSpace, Vector3 assetLocalPoint,
            Vector3 desiredWorldPoint)
        {
            if (movingRoot == null || assetSpace == null) return desiredWorldPoint;
            Vector3 rootToAnchor = assetSpace.TransformPoint(assetLocalPoint)
                                 - movingRoot.position;
            return desiredWorldPoint - rootToAnchor;
        }

        public static float RelativeToRoyalReference(Transform vessel, VesselProfile profile)
        {
            if (vessel == null) return 1f;
            float referenceScale = profile != null ? profile.ShelfReferenceScale : 1f;
            return Mathf.Max(MinimumScale,
                PlanarWorldScale(vessel) / referenceScale);
        }

        public static float ReferenceDistance(float royalWorldDistance,
                                              float relativeScale) =>
            royalWorldDistance * Mathf.Max(MinimumScale, relativeScale);

        public static float RoyalLocalUnitsPerPixel(VesselProfile profile) =>
            RoyalWorldUnitsPerPixel
            / Mathf.Max(MinimumScale,
                profile != null ? profile.ShelfReferenceScale : 1f);

        public static float RoyalPixelsToLocal(float pixels, VesselProfile profile) =>
            pixels * RoyalLocalUnitsPerPixel(profile);

        public static float RescaleAuthoredLength(float authoredLength,
                                                  float authoredAtScale,
                                                  float currentScale) =>
            authoredLength * Mathf.Max(MinimumScale, currentScale)
                           / Mathf.Max(MinimumScale, authoredAtScale);
    }
}
