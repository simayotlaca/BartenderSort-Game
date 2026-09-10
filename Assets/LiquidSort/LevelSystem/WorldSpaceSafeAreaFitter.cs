using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Fits the Inspector-linked 720x1280 composition inside the safe area as one group. The camera stays
    /// unchanged so the background remains full screen.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class WorldSpaceSafeAreaFitter : MonoBehaviour
    {
        [Header("Required scene references")]
        [Tooltip("The orthographic camera that renders the portrait game. Its viewport, "
               + "orthographic size and transform are never modified by this component. "
               + "When assigned, this always takes precedence over automatic resolution.")]
        [SerializeField] private Camera targetCamera;

        [Tooltip("When Target Camera is empty, resolve a tagged Camera.main and keep "
               + "following it if the host scene replaces its main camera.")]
        [SerializeField] private bool autoResolveMainCamera = true;

        [Tooltip("The single world-space parent containing every foreground gameplay "
               + "element that must keep the authored composition.")]
        [SerializeField] private Transform compositionRoot;

        [Header("Reference composition")]
        [Tooltip("Resolution at which the composition was authored. 720x1280 is the "
               + "project's canonical portrait framing.")]
        [SerializeField] private Vector2Int referenceResolution = new Vector2Int(720, 1280);

        [Tooltip("Orthographic size used while the reference pose was authored. Capture "
               + "Reference Pose stores the assigned camera's current value here.")]
        [SerializeField, Min(0.01f)] private float referenceOrthographicSize = 6f;

        [Tooltip("Use Screen.safeArea. Disable only for controlled screenshots that need "
               + "the entire camera viewport.")]
        [SerializeField] private bool respectSafeArea = true;

        [Tooltip("Placement inside unused fitted space: (0.5,0.5) centres; (0.5,1) "
               + "keeps a width-fitted portrait composition against the top edge.")]
        [SerializeField] private Vector2 contentAlignment = new Vector2(0.5f, 0.5f);

        [Tooltip("Apply while Unity is not in Play Mode, so Game View device/aspect changes "
               + "can be inspected without entering the game.")]
        [SerializeField] private bool previewInEditMode = true;

        [Tooltip("Draw the resolved safe-area rectangle at the composition depth while "
               + "this object is selected.")]
        [SerializeField] private bool drawSafeAreaGizmo = true;

        [Header("Validation")]
        [Tooltip("Emit a single error when required references or camera settings are invalid.")]
        [SerializeField] private bool logConfigurationErrors = true;

        // Keep position relative to the camera and scale local so camera movement and parent scaling still
        // work.
        [SerializeField, HideInInspector] private bool referencePoseCaptured;
        [SerializeField, HideInInspector] private Vector3 referenceCameraLocalPosition;
        [SerializeField, HideInInspector] private Quaternion referenceCameraRelativeRotation =
            Quaternion.identity;
        [SerializeField, HideInInspector] private Vector3 referenceLocalScale = Vector3.one;

        private string lastLoggedError;
        private Rect appliedSafeAreaPixels;
        private Rect appliedSafeAreaViewport;
        private Camera autoResolvedCamera;
        private bool hasAppliedLayoutState;
        private Camera appliedCamera;
        private Transform appliedCompositionRoot;
        private Rect observedCameraPixels;
        private Rect observedRequestedSafeArea;
        private bool observedCameraIsOrthographic;
        private float observedCameraOrthographicSize;
        private float observedCameraAspect;
        private Matrix4x4 observedCameraLocalToWorld;
        private Matrix4x4 observedRootLocalToWorld;

        public Vector2Int ReferenceResolution => referenceResolution;
        public float ReferenceOrthographicSize => referenceOrthographicSize;
        public Vector2 ContentAlignment => contentAlignment;

        /// <summary>Safe area actually used, in physical screen pixels.</summary>
        public Rect AppliedSafeAreaPixels => appliedSafeAreaPixels;

        private void OnEnable()
        {
            hasAppliedLayoutState = false;
            ApplyNow();
        }

        private void LateUpdate()
        {
            if ((Application.isPlaying || previewInEditMode) && LayoutInputsChanged())
                ApplyNow();
        }

        private void OnValidate()
        {
            referenceResolution.x = Mathf.Max(1, referenceResolution.x);
            referenceResolution.y = Mathf.Max(1, referenceResolution.y);
            referenceOrthographicSize = Mathf.Max(0.01f, referenceOrthographicSize);
            contentAlignment.x = Mathf.Clamp01(contentAlignment.x);
            contentAlignment.y = Mathf.Clamp01(contentAlignment.y);

            if (!isActiveAndEnabled)
                return;

            ApplyNow();
        }

        /// <summary>
        /// Saves the current root pose as the baseline. Call once at 720x1280 with the intended layout
        /// visible.
        /// </summary>
        [ContextMenu("Capture Current Pose As Reference")]
        public void CaptureCurrentPoseAsReference()
        {
            Camera camera = ResolveTargetCamera();
            if (camera == null || compositionRoot == null)
            {
                ReportError("Assign Composition Root and either assign Target Camera or "
                          + "provide a tagged Camera.main before capturing the reference pose.");
                return;
            }

            if (!camera.orthographic)
            {
                ReportError("WorldSpaceSafeAreaFitter requires an orthographic camera.");
                return;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.Undo.RecordObject(this, "Capture Safe Area Reference Pose");
#endif

            referenceCameraLocalPosition =
                camera.transform.InverseTransformPoint(compositionRoot.position);
            referenceCameraRelativeRotation =
                Quaternion.Inverse(camera.transform.rotation) * compositionRoot.rotation;
            referenceLocalScale = compositionRoot.localScale;
            referenceOrthographicSize = Mathf.Max(0.01f, camera.orthographicSize);
            referencePoseCaptured = true;
            lastLoggedError = null;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorUtility.SetDirty(this);
#endif

            ApplyNow();
        }

        /// <summary>Clears the baseline so the next valid Apply captures the updated layout.</summary>
        [ContextMenu("Recapture Reference On Next Apply")]
        public void RecaptureReferenceOnNextApply()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.Undo.RecordObject(this, "Reset Safe Area Reference Pose");
#endif

            referencePoseCaptured = false;
            ApplyNow();

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>Updates layout immediately; also safe for editor tools and resolution changes.</summary>
        [ContextMenu("Apply Safe Area Layout Now")]
        public bool ApplyNow()
        {
            if (!ValidateConfiguration(out Camera camera, out string error))
            {
                hasAppliedLayoutState = false;
                // The hierarchy may enable before its camera exists. Retry quietly until it is ready.
                if (camera != null || targetCamera != null || !autoResolveMainCamera)
                    ReportError(error);
                return false;
            }

            CaptureReferencePoseIfRequired(camera);

            Rect cameraPixels = camera.pixelRect;
            if (cameraPixels.width <= 0f || cameraPixels.height <= 0f)
            {
                hasAppliedLayoutState = false;
                // A resizing Game View may briefly report 0x0. Keep the last valid pose and retry next
                // frame.
                return false;
            }

            // With safe-area handling off, use the camera viewport. Off-screen render targets may differ
            // from Screen dimensions.
            Rect requestedSafeArea = respectSafeArea ? Screen.safeArea : cameraPixels;
            Rect usablePixels = Intersect(cameraPixels, requestedSafeArea);

            // Use the camera rect if the editor or simulator briefly reports an empty safe area.
            if (usablePixels.width <= 0f || usablePixels.height <= 0f)
                usablePixels = cameraPixels;

            float cameraWorldHeight = 2f * camera.orthographicSize;
            float cameraWorldWidth = cameraWorldHeight * camera.aspect;
            float safeWorldWidth = cameraWorldWidth * (usablePixels.width / cameraPixels.width);
            float safeWorldHeight = cameraWorldHeight * (usablePixels.height / cameraPixels.height);

            float referenceWorldHeight = 2f * referenceOrthographicSize;
            float referenceAspect = (float)referenceResolution.x / referenceResolution.y;
            float referenceWorldWidth = referenceWorldHeight * referenceAspect;

            float widthScale = safeWorldWidth / referenceWorldWidth;
            float heightScale = safeWorldHeight / referenceWorldHeight;
            float uniformScale = Mathf.Max(0.0001f, Mathf.Min(widthScale, heightScale));

            float safeCentreViewportX =
                (usablePixels.center.x - cameraPixels.xMin) / cameraPixels.width;
            float safeCentreViewportY =
                (usablePixels.center.y - cameraPixels.yMin) / cameraPixels.height;

            Vector3 cameraLocalPosition = referenceCameraLocalPosition;
            cameraLocalPosition.x += (safeCentreViewportX - 0.5f) * cameraWorldWidth;
            cameraLocalPosition.y += (safeCentreViewportY - 0.5f) * cameraWorldHeight;
            float unusedWorldWidth = safeWorldWidth - referenceWorldWidth * uniformScale;
            float unusedWorldHeight = safeWorldHeight - referenceWorldHeight * uniformScale;
            cameraLocalPosition.x += (contentAlignment.x - 0.5f) * unusedWorldWidth;
            cameraLocalPosition.y += (contentAlignment.y - 0.5f) * unusedWorldHeight;

            Vector3 targetWorldPosition =
                camera.transform.TransformPoint(cameraLocalPosition);
            Quaternion targetWorldRotation =
                camera.transform.rotation * referenceCameraRelativeRotation;
            Vector3 targetLocalScale = referenceLocalScale * uniformScale;

            // I only write changed poses to avoid recalculating every child each editor frame. Derive each
            // update from the baseline to avoid drift.
            if (RootPoseChanged(targetWorldPosition, targetWorldRotation, targetLocalScale))
            {
                compositionRoot.SetPositionAndRotation(targetWorldPosition, targetWorldRotation);
                compositionRoot.localScale = targetLocalScale;
            }

            appliedSafeAreaPixels = usablePixels;
            appliedSafeAreaViewport = new Rect(
                (usablePixels.xMin - cameraPixels.xMin) / cameraPixels.width,
                (usablePixels.yMin - cameraPixels.yMin) / cameraPixels.height,
                usablePixels.width / cameraPixels.width,
                usablePixels.height / cameraPixels.height);
            appliedCamera = camera;
            appliedCompositionRoot = compositionRoot;
            observedCameraPixels = cameraPixels;
            observedRequestedSafeArea = requestedSafeArea;
            observedCameraIsOrthographic = camera.orthographic;
            observedCameraOrthographicSize = camera.orthographicSize;
            observedCameraAspect = camera.aspect;
            observedCameraLocalToWorld = camera.transform.localToWorldMatrix;
            observedRootLocalToWorld = compositionRoot.localToWorldMatrix;
            hasAppliedLayoutState = true;
            lastLoggedError = null;
            return true;
        }

        private bool LayoutInputsChanged()
        {
            Camera camera = ResolveTargetCamera();
            if (!hasAppliedLayoutState)
                return true;

            if (camera == null || compositionRoot == null)
                return true;

            if (camera != appliedCamera || compositionRoot != appliedCompositionRoot)
                return true;

            // Restore the fitted pose if another component moved the root.
            if (compositionRoot.localToWorldMatrix != observedRootLocalToWorld)
                return true;

            Rect cameraPixels = camera.pixelRect;
            Rect requestedSafeArea = respectSafeArea ? Screen.safeArea : cameraPixels;

            return cameraPixels != observedCameraPixels
                   || requestedSafeArea != observedRequestedSafeArea
                   || camera.orthographic != observedCameraIsOrthographic
                   || !Mathf.Approximately(
                       camera.orthographicSize,
                       observedCameraOrthographicSize)
                   || !Mathf.Approximately(camera.aspect, observedCameraAspect)
                   || camera.transform.localToWorldMatrix
                      != observedCameraLocalToWorld;
        }

        private bool RootPoseChanged(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            const float positionToleranceSquared = 0.00000001f;
            const float scaleToleranceSquared = 0.00000001f;
            const float rotationTolerance = 0.0000001f;

            return (compositionRoot.position - position).sqrMagnitude > positionToleranceSquared
                   || (compositionRoot.localScale - scale).sqrMagnitude > scaleToleranceSquared
                   || Mathf.Abs(Quaternion.Dot(compositionRoot.rotation, rotation))
                      < 1f - rotationTolerance;
        }

        private bool ValidateConfiguration(out Camera camera, out string error)
        {
            camera = ResolveTargetCamera();
            if (camera == null)
            {
                error = autoResolveMainCamera
                    ? "Waiting for a tagged Camera.main."
                    : "Target Camera is not assigned and automatic resolution is disabled.";
                return false;
            }

            if (compositionRoot == null)
            {
                error = "Composition Root is not assigned.";
                return false;
            }

            if (!camera.orthographic)
            {
                error = "Target Camera must be orthographic.";
                return false;
            }

            if (compositionRoot == camera.transform
                || camera.transform.IsChildOf(compositionRoot))
            {
                error = "Composition Root cannot contain the Target Camera; the camera "
                      + "must remain a separate full-screen background camera.";
                return false;
            }

            if (referenceResolution.x <= 0 || referenceResolution.y <= 0)
            {
                error = "Reference Resolution must contain positive dimensions.";
                return false;
            }

            if (referenceOrthographicSize <= 0f)
            {
                error = "Reference Orthographic Size must be positive.";
                return false;
            }

            error = null;
            return true;
        }

        private void CaptureReferencePoseIfRequired(Camera camera)
        {
            if (referencePoseCaptured)
                return;

            referenceCameraLocalPosition =
                camera.transform.InverseTransformPoint(compositionRoot.position);
            referenceCameraRelativeRotation =
                Quaternion.Inverse(camera.transform.rotation) * compositionRoot.rotation;
            referenceLocalScale = compositionRoot.localScale;
            referenceOrthographicSize = Mathf.Max(0.01f, camera.orthographicSize);
            referencePoseCaptured = true;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>
        /// Keeps an explicit camera unchanged. Otherwise caches Camera.main and resolves again if that
        /// camera is destroyed, disabled or retagged.
        /// </summary>
        private Camera ResolveTargetCamera()
        {
            if (targetCamera != null)
            {
                autoResolvedCamera = null;
                return targetCamera;
            }

            if (!autoResolveMainCamera)
            {
                autoResolvedCamera = null;
                return null;
            }

            if (autoResolvedCamera == null
                || !autoResolvedCamera.isActiveAndEnabled
                || !autoResolvedCamera.CompareTag("MainCamera"))
                autoResolvedCamera = Camera.main;

            return autoResolvedCamera;
        }

        private void ReportError(string error)
        {
            if (!logConfigurationErrors || string.IsNullOrEmpty(error)
                || error == lastLoggedError)
                return;

            lastLoggedError = error;
            Debug.LogError($"[{nameof(WorldSpaceSafeAreaFitter)}] {error}", this);
        }

        private static Rect Intersect(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin);
            float yMin = Mathf.Max(a.yMin, b.yMin);
            float xMax = Mathf.Min(a.xMax, b.xMax);
            float yMax = Mathf.Min(a.yMax, b.yMax);
            return Rect.MinMaxRect(xMin, yMin, Mathf.Max(xMin, xMax), Mathf.Max(yMin, yMax));
        }

        private void OnDrawGizmosSelected()
        {
            Camera camera = ResolveTargetCamera();
            if (!drawSafeAreaGizmo || camera == null || compositionRoot == null
                || appliedSafeAreaViewport.width <= 0f
                || appliedSafeAreaViewport.height <= 0f)
                return;

            float depth = referencePoseCaptured
                ? referenceCameraLocalPosition.z
                : camera.transform.InverseTransformPoint(compositionRoot.position).z;

            Vector3 bottomLeft = camera.ViewportToWorldPoint(new Vector3(
                appliedSafeAreaViewport.xMin, appliedSafeAreaViewport.yMin, depth));
            Vector3 topLeft = camera.ViewportToWorldPoint(new Vector3(
                appliedSafeAreaViewport.xMin, appliedSafeAreaViewport.yMax, depth));
            Vector3 topRight = camera.ViewportToWorldPoint(new Vector3(
                appliedSafeAreaViewport.xMax, appliedSafeAreaViewport.yMax, depth));
            Vector3 bottomRight = camera.ViewportToWorldPoint(new Vector3(
                appliedSafeAreaViewport.xMax, appliedSafeAreaViewport.yMin, depth));

            Gizmos.color = new Color(0.15f, 1f, 0.55f, 0.9f);
            Gizmos.DrawLine(bottomLeft, topLeft);
            Gizmos.DrawLine(topLeft, topRight);
            Gizmos.DrawLine(topRight, bottomRight);
            Gizmos.DrawLine(bottomRight, bottomLeft);
        }
    }
}
