using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Finds the host scene camera for World Space and Screen Space - Camera canvases. The portable prefab
    /// cannot save an external camera reference, so bind it at runtime.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Canvas))]
    public sealed class WorldCanvasCameraBinder : MonoBehaviour
    {
        [Tooltip("Uses this camera, or Camera.main if empty.")]
        [SerializeField] private Camera explicitCamera;

        private Canvas canvas;

        private void OnEnable()
        {
            canvas = GetComponent<Canvas>();
            Bind();
        }

        private void Update()
        {
            // Check the reference each frame in case the scene replaces its camera.
            if (canvas == null) canvas = GetComponent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                || canvas.worldCamera != null) return;
            Bind();
        }

        private void Bind()
        {
            if (canvas == null) canvas = GetComponent<Canvas>();
            // Both camera-based canvas modes need rebinding when the prefab enters its host scene.
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return;
            Camera resolved = explicitCamera != null ? explicitCamera : Camera.main;
            if (resolved != null) canvas.worldCamera = resolved;
        }
    }
}
