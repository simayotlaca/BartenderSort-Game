using UnityEngine;

namespace LiquidSort.Levels
{
    internal sealed class DeliveryPoofPresentation
    {
        internal enum StepResult { None, Dissolving, Disappeared, Finished, Aborted }

        private DeliveryPoofGraphic graphic;
        private Canvas canvas;
        private Material magicMaterial;
        private OrderCardView target;
        private int orderIndex;
        private Vector3 originWorld;
        private Bounds originBounds;
        private Vector2 sourceSize;
        private Vector2 destinationSize;
        private Color tint;
        private float elapsed;
        private double previousTick;
        private double deadline;
        private bool active;
        private bool disappeared;
        private BartenderShelfLevelView shelf;
        private BartenderDeliveryReceipt receipt;
        private Vector2 source;
        private Vector2 destination;

        internal bool TryBegin(BartenderShelfLevelView shelf,
            BartenderDeliveryReceipt receipt, OrderCardView card)
        {
            Cancel();
            if (shelf == null || card == null || card.Rt == null
                || card.Model == null || !card.isActiveAndEnabled
                || !shelf.TryGetDeliveryOrigin(receipt, out originWorld, out tint, out originBounds))
                return false;
            Canvas destination = card.Rt.GetComponentInParent<Canvas>();
            if (destination == null || !destination.isActiveAndEnabled || Camera.main == null)
                return false;
            EnsureGraphic(destination);
            if (magicMaterial == null) return false;
            target = card;
            orderIndex = card.Model.RuntimeOrderIndex;
            card.BeginSynchronizedDelivery();
            if (!TryGetPoints(out source, out this.destination))
            {
                Cancel();
                return false;
            }
            CaptureFootprints();
            elapsed = 0f;
            disappeared = false;
            this.shelf = shelf;
            this.receipt = receipt;
            previousTick = Time.realtimeSinceStartupAsDouble;
            deadline = previousTick + 2.5d;
            active = true;
            graphic.Sample(source, this.destination, tint, 0f, ReferenceScale, sourceSize, destinationSize, 1f);
            return true;
        }

        internal StepResult Step()
        {
            if (!active) return StepResult.None;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now > deadline || shelf == null || graphic == null || !graphic.isActiveAndEnabled
                || target == null || !target.isActiveAndEnabled || target.Model == null
                || target.Model.RuntimeOrderIndex != orderIndex
                || !shelf.TryGetDeliverySample(receipt, out float glassSeconds,
                    out float glassOpacity, out bool glassFinished))
            {
                Cancel();
                return StepResult.Aborted;
            }
            if (!disappeared)
            {
                // The shelf coroutine has already posed the glass for this rendered frame. Copy its
                // actual sampled opacity, including its bounded long-frame clock and final pool release.
                elapsed = glassSeconds;
                target.SampleSynchronizedDelivery(glassOpacity, glassFinished);
            }
            else
            {
                // Both objects are already invisible. Match the queue's unscaled elapsed time so its
                // completion cannot cut a still-bright glitter tail on a slow device.
                elapsed += Mathf.Max(0f, (float)(now - previousTick));
            }
            previousTick = now;
            graphic.Sample(source, destination, tint, elapsed, ReferenceScale,
                sourceSize, destinationSize, 1f);
            if (!disappeared && glassFinished)
            {
                disappeared = true;
                return StepResult.Disappeared;
            }
            if (elapsed >= DeliveryPoofGraphic.TotalSeconds)
            {
                Cancel();
                return StepResult.Finished;
            }
            return StepResult.Dissolving;
        }

        internal void Cancel()
        {
            active = false;
            target = null;
            shelf = null;
            receipt = null;
            if (graphic != null) graphic.Clear();
        }

        internal void Dispose()
        {
            Cancel();
            if (graphic != null) Object.Destroy(graphic.gameObject);
            graphic = null;
            if (magicMaterial != null) Object.Destroy(magicMaterial);
            magicMaterial = null;
            canvas = null;
        }

        private float ReferenceScale
        {
            get
            {
                Camera gameCamera = Camera.main;
                if (graphic == null || canvas == null || gameCamera == null) return 1f;
                RectTransform rect = graphic.rectTransform;
                Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null : canvas.worldCamera != null ? canvas.worldCamera : gameCamera;
                Vector2 left = RectTransformUtility.WorldToScreenPoint(uiCamera,
                    rect.TransformPoint(new Vector3(rect.rect.xMin, rect.rect.center.y)));
                Vector2 right = RectTransformUtility.WorldToScreenPoint(uiCamera,
                    rect.TransformPoint(new Vector3(rect.rect.xMax, rect.rect.center.y)));
                float projectedWidth = Vector2.Distance(left, right);
                return projectedWidth > 0.01f
                    ? Mathf.Max(0.001f, rect.rect.width / projectedWidth * gameCamera.pixelWidth / 720f)
                    : 1f;
            }
        }

        private void EnsureGraphic(Canvas destination)
        {
            if (graphic != null && canvas == destination) return;
            Dispose();
            canvas = destination;
            var layer = new GameObject("Delivery Poof", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(DeliveryPoofGraphic));
            layer.layer = destination.gameObject.layer;
            layer.hideFlags = HideFlags.DontSave;
            RectTransform rect = (RectTransform)layer.transform;
            rect.SetParent(destination.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            // The authored time-booster pool must remain the last child of this canvas.
            rect.SetSiblingIndex(Mathf.Max(0, destination.transform.childCount - 2));
            graphic = layer.GetComponent<DeliveryPoofGraphic>();
            graphic.raycastTarget = false;
            graphic.maskable = false;
            graphic.color = Color.white;
            Shader magicShader = Resources.Load<Shader>("DeliveryMist");
            if (magicShader != null && magicShader.isSupported)
            {
                magicMaterial = new Material(magicShader)
                {
                    name = "Delivery Glitter (Runtime)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                graphic.material = magicMaterial;
            }
        }

        private void CaptureFootprints()
        {
            Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null : canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            RectTransform surface = graphic.rectTransform;
            Vector3 glassMin = Camera.main.WorldToScreenPoint(originBounds.min);
            Vector3 glassMax = Camera.main.WorldToScreenPoint(originBounds.max);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(surface, glassMin, uiCamera, out Vector2 a);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(surface, glassMax, uiCamera, out Vector2 b);
            sourceSize = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            Rect rect = target.Rt.rect;
            Vector2 cardMin = RectTransformUtility.WorldToScreenPoint(uiCamera, target.Rt.TransformPoint(rect.min));
            Vector2 cardMax = RectTransformUtility.WorldToScreenPoint(uiCamera, target.Rt.TransformPoint(rect.max));
            RectTransformUtility.ScreenPointToLocalPointInRectangle(surface, cardMin, uiCamera, out a);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(surface, cardMax, uiCamera, out b);
            destinationSize = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private bool TryGetPoints(out Vector2 from, out Vector2 to)
        {
            from = to = default;
            if (graphic == null || canvas == null || target == null
                || target.Rt == null || Camera.main == null) return false;
            Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null : canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            Vector3 originScreen = Camera.main.WorldToScreenPoint(originWorld);
            Vector2 targetScreen = RectTransformUtility.WorldToScreenPoint(uiCamera,
                target.DeliveryPoofCenter);
            return originScreen.z > 0f
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    graphic.rectTransform, originScreen, uiCamera, out from)
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    graphic.rectTransform, targetScreen, uiCamera, out to);
        }
    }
}
