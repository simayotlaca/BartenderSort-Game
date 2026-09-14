using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Gameplay/Win Shelf Clear")]
    public sealed class BartenderWinShelfClear : MonoBehaviour
    {
        private const float StartDelaySeconds = 0.05f;
        private const float StaggerSeconds = 0.055f;
        private const float PopSeconds = 0.09f;
        private const float ShrinkSeconds = 0.24f;
        private const float PopScale = 1.07f;
        private const float ShrinkBack = 1.2f;
        private const float TailSeconds = 0.06f;
        private const float WaitLimitSeconds = 1f;

        private enum Phase { Idle, Waiting, Clearing }

        private sealed class Leaver
        {
            public LiquidBottle Bottle;
            public Vector3 RestPosition;
            public Vector3 RestScale;
            public Vector3 ArtCenter;
            public Vector3 WorldCenter;
            public Bounds WorldBounds;
            public float Row;
            public float Delay;
            public bool Gone;
            public DeliveryPoofGraphic Glitter;
            public Material GlitterMaterial;
            public Vector2 CanvasPoint;
            public Vector2 CanvasSize;
        }

        private readonly List<Leaver> leavers = new List<Leaver>(16);
        private readonly List<LiquidBottle> cleared = new List<LiquidBottle>(16);

        private BartenderShelfLevelView shelfView;
        private BartenderLevelController subscribed;
        private BartenderLevelController barrierOwner;
        private Phase phase;
        private double waitStarted;
        private double clearStarted;
        private float clearDuration;
        private Canvas glitterCanvas;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetHooks() => SceneManager.sceneLoaded -= HandleSceneLoaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallHooks()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            AttachToShelves();
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode) => AttachToShelves();

        private static void AttachToShelves()
        {
            BartenderShelfLevelView[] shelves = FindObjectsByType<BartenderShelfLevelView>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (BartenderShelfLevelView shelf in shelves)
                if (shelf != null && shelf.GetComponent<BartenderWinShelfClear>() == null)
                    shelf.gameObject.AddComponent<BartenderWinShelfClear>();
        }

        private void OnEnable() => Rebind();

        private void OnDisable()
        {
            Abort();
            Unsubscribe();
        }

        private void LateUpdate()
        {
            Rebind();
            if (phase == Phase.Waiting) TickWaiting();
            else if (phase == Phase.Clearing) TickClearing();
        }

        private void Rebind()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            BartenderLevelController wanted = shelfView != null ? shelfView.Controller : null;
            if (ReferenceEquals(wanted, subscribed)) return;
            Abort();
            Unsubscribe();
            cleared.Clear();
            subscribed = wanted;
            if (subscribed == null) return;
            subscribed.StateChanged += HandleStateChanged;
            subscribed.LevelLoaded += HandleLevelLoaded;
        }

        private void Unsubscribe()
        {
            if (subscribed == null) return;
            subscribed.StateChanged -= HandleStateChanged;
            subscribed.LevelLoaded -= HandleLevelLoaded;
            subscribed = null;
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            switch (state)
            {
                case BartenderLevelState.Won:
                    if (phase != Phase.Idle || subscribed == null
                        || !subscribed.AcquirePresentationBarrier(this))
                        return;
                    barrierOwner = subscribed;
                    phase = Phase.Waiting;
                    waitStarted = Time.realtimeSinceStartupAsDouble;
                    break;
                case BartenderLevelState.Playing:
                case BartenderLevelState.Paused:
                    Abort();
                    RestoreCleared();
                    break;
                case BartenderLevelState.Failed:
                case BartenderLevelState.Unloaded:
                    Abort();
                    break;
            }
        }

        // A new level reassigns the glass pool itself, so cleared glasses are simply forgotten.
        private void HandleLevelLoaded(BsLevel _)
        {
            Abort();
            cleared.Clear();
        }

        private void TickWaiting()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            bool ready = shelfView != null && shelfView.Ready;
            if (now - waitStarted < StartDelaySeconds || (!ready && now - waitStarted < WaitLimitSeconds)) return;
            BeginClearing(now);
        }

        private void BeginClearing(double now)
        {
            ClearLeavers();
            BsBoard board = subscribed != null ? subscribed.Board : null;
            if (board != null && shelfView != null)
            {
                for (int i = 0; i < board.Glasses.Count; i++)
                {
                    RtGlass glass = board.Glasses[i];
                    if (glass == null || !shelfView.TryGetBottle(glass.Id, out LiquidBottle bottle)
                        || bottle == null || !bottle.gameObject.activeInHierarchy)
                        continue;
                    leavers.Add(CaptureLeaver(bottle));
                }
            }
            if (leavers.Count == 0)
            {
                Finish();
                return;
            }

            OrderByRows();
            glitterCanvas = FindGlitterCanvas();
            float lastDelay = 0f;
            for (int i = 0; i < leavers.Count; i++)
            {
                Leaver leaver = leavers[i];
                leaver.Delay = i * StaggerSeconds;
                lastDelay = leaver.Delay;
                PrepareGlitter(leaver);
            }
            clearDuration = lastDelay + Mathf.Max(PopSeconds + ShrinkSeconds,
                DeliveryPoofGraphic.TotalSeconds) + TailSeconds;
            clearStarted = now;
            phase = Phase.Clearing;
            TickClearing();
        }

        private static Leaver CaptureLeaver(LiquidBottle bottle)
        {
            Transform glass = bottle.transform;
            Transform front = glass.Find("FrontGlass");
            Renderer art = front != null ? front.GetComponent<Renderer>() : null;
            Bounds bounds;
            if (art != null && art.enabled)
                bounds = art.bounds;
            else
            {
                Rect interior = bottle.InteriorBounds;
                bounds = new Bounds(glass.TransformPoint(interior.center), Vector3.zero);
                bounds.Encapsulate(glass.TransformPoint(interior.min));
                bounds.Encapsulate(glass.TransformPoint(interior.max));
            }
            return new Leaver
            {
                Bottle = bottle,
                RestPosition = glass.localPosition,
                RestScale = glass.localScale,
                ArtCenter = glass.InverseTransformPoint(bounds.center),
                WorldCenter = bounds.center,
                WorldBounds = bounds
            };
        }

        // Reading order: top row first, left to right. Glasses share a row when their bases sit close.
        private void OrderByRows()
        {
            leavers.Sort((a, b) => b.Bottle.transform.position.y.CompareTo(a.Bottle.transform.position.y));
            float row = 0f;
            for (int i = 0; i < leavers.Count; i++)
            {
                if (i > 0)
                {
                    float gap = leavers[i - 1].Bottle.transform.position.y - leavers[i].Bottle.transform.position.y;
                    float tolerance = 0.25f * Mathf.Min(leavers[i - 1].WorldBounds.size.y,
                                                        leavers[i].WorldBounds.size.y);
                    if (gap > tolerance) row++;
                }
                leavers[i].Row = row;
            }
            leavers.Sort((a, b) => a.Row != b.Row
                ? a.Row.CompareTo(b.Row)
                : a.WorldCenter.x.CompareTo(b.WorldCenter.x));
        }

        private void TickClearing()
        {
            float elapsed = (float)(Time.realtimeSinceStartupAsDouble - clearStarted);
            float reference = GlitterReferenceScale();
            for (int i = 0; i < leavers.Count; i++)
            {
                Leaver leaver = leavers[i];
                float time = elapsed - leaver.Delay;
                SampleGlitter(leaver, time, reference);
                if (leaver.Gone) continue;
                if (leaver.Bottle == null)
                {
                    leaver.Gone = true;
                    continue;
                }

                Transform glass = leaver.Bottle.transform;
                if (time >= PopSeconds + ShrinkSeconds)
                {
                    RestorePose(leaver);
                    leaver.Bottle.gameObject.SetActive(false);
                    cleared.Add(leaver.Bottle);
                    leaver.Gone = true;
                    continue;
                }

                float size = time <= 0f ? 1f
                    : time < PopSeconds ? Mathf.Lerp(1f, PopScale, EaseOutQuad(time / PopSeconds))
                    : PopScale * (1f - EaseInBack((time - PopSeconds) / ShrinkSeconds));
                Vector3 scale = leaver.RestScale * size;
                glass.localPosition = leaver.RestPosition + glass.localRotation
                    * Vector3.Scale(leaver.RestScale - scale, leaver.ArtCenter);
                glass.localScale = scale;
            }

            if (elapsed >= clearDuration) Finish();
        }

        private Canvas FindGlitterCanvas()
        {
            // The delivery glitter already draws above the shelf from the order cards' canvas.
            OrderStripPresenter strip = FindFirstObjectByType<OrderStripPresenter>();
            if (strip == null || strip.Cards == null) return null;
            foreach (OrderCardView card in strip.Cards)
            {
                if (card == null || card.Rt == null) continue;
                Canvas canvas = card.Rt.GetComponentInParent<Canvas>(true);
                if (canvas != null && canvas.isActiveAndEnabled) return canvas;
            }
            return null;
        }

        private void PrepareGlitter(Leaver leaver)
        {
            Camera gameCamera = Camera.main;
            if (glitterCanvas == null || gameCamera == null) return;
            Shader shader = Resources.Load<Shader>("DeliveryMist");
            if (shader == null || !shader.isSupported) return;

            var layer = new GameObject("Win Shelf Glitter", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(DeliveryPoofGraphic));
            layer.layer = glitterCanvas.gameObject.layer;
            layer.hideFlags = HideFlags.DontSave;
            RectTransform rect = (RectTransform)layer.transform;
            rect.SetParent(glitterCanvas.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            // The authored time-booster pool must remain the last child of this canvas.
            rect.SetSiblingIndex(Mathf.Max(0, glitterCanvas.transform.childCount - 2));

            DeliveryPoofGraphic graphic = layer.GetComponent<DeliveryPoofGraphic>();
            graphic.raycastTarget = false;
            graphic.maskable = false;
            graphic.color = Color.white;
            leaver.GlitterMaterial = new Material(shader)
            {
                name = "Win Shelf Glitter (Runtime)",
                hideFlags = HideFlags.HideAndDontSave
            };
            graphic.material = leaver.GlitterMaterial;
            leaver.Glitter = graphic;

            Camera uiCamera = UiCamera(gameCamera);
            Vector3 center = gameCamera.WorldToScreenPoint(leaver.WorldCenter);
            Vector3 min = gameCamera.WorldToScreenPoint(leaver.WorldBounds.min);
            Vector3 max = gameCamera.WorldToScreenPoint(leaver.WorldBounds.max);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, center, uiCamera, out leaver.CanvasPoint);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, min, uiCamera, out Vector2 a);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, max, uiCamera, out Vector2 b);
            leaver.CanvasSize = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private static void SampleGlitter(Leaver leaver, float time, float reference)
        {
            if (leaver.Glitter == null) return;
            if (time < 0f || time >= DeliveryPoofGraphic.TotalSeconds)
            {
                leaver.Glitter.Clear();
                return;
            }
            leaver.Glitter.Sample(leaver.CanvasPoint, leaver.CanvasPoint, Color.white, time, reference,
                leaver.CanvasSize, default, 1f, false);
        }

        private float GlitterReferenceScale()
        {
            Camera gameCamera = Camera.main;
            DeliveryPoofGraphic graphic = null;
            for (int i = 0; i < leavers.Count && graphic == null; i++) graphic = leavers[i].Glitter;
            if (graphic == null || glitterCanvas == null || gameCamera == null) return 1f;
            RectTransform rect = graphic.rectTransform;
            Camera uiCamera = UiCamera(gameCamera);
            Vector2 left = RectTransformUtility.WorldToScreenPoint(uiCamera,
                rect.TransformPoint(new Vector3(rect.rect.xMin, rect.rect.center.y)));
            Vector2 right = RectTransformUtility.WorldToScreenPoint(uiCamera,
                rect.TransformPoint(new Vector3(rect.rect.xMax, rect.rect.center.y)));
            float projectedWidth = Vector2.Distance(left, right);
            return projectedWidth > 0.01f
                ? Mathf.Max(0.001f, rect.rect.width / projectedWidth * gameCamera.pixelWidth / 720f)
                : 1f;
        }

        private Camera UiCamera(Camera gameCamera) =>
            glitterCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null
            : glitterCanvas.worldCamera != null ? glitterCanvas.worldCamera : gameCamera;

        private void Finish()
        {
            ClearLeavers();
            phase = Phase.Idle;
            ReleaseBarrier();
        }

        private void Abort()
        {
            if (phase == Phase.Idle && barrierOwner == null && leavers.Count == 0) return;
            ClearLeavers();
            phase = Phase.Idle;
            ReleaseBarrier();
        }

        private void ClearLeavers()
        {
            for (int i = 0; i < leavers.Count; i++)
            {
                Leaver leaver = leavers[i];
                if (!leaver.Gone && leaver.Bottle != null) RestorePose(leaver);
                if (leaver.Glitter != null) Destroy(leaver.Glitter.gameObject);
                if (leaver.GlitterMaterial != null) Destroy(leaver.GlitterMaterial);
            }
            leavers.Clear();
            glitterCanvas = null;
        }

        private void RestoreCleared()
        {
            for (int i = 0; i < cleared.Count; i++)
            {
                LiquidBottle bottle = cleared[i];
                if (bottle == null || bottle.gameObject.activeSelf) continue;
                bottle.gameObject.SetActive(true);
                bottle.Refresh();
            }
            cleared.Clear();
        }

        private void ReleaseBarrier()
        {
            BartenderLevelController owner = barrierOwner;
            barrierOwner = null;
            if (owner != null) owner.ReleasePresentationBarrier(this);
        }

        private static void RestorePose(Leaver leaver)
        {
            Transform glass = leaver.Bottle.transform;
            glass.localPosition = leaver.RestPosition;
            glass.localScale = leaver.RestScale;
        }

        private static float EaseOutQuad(float t)
        {
            t = Mathf.Clamp01(t);
            return 1f - (1f - t) * (1f - t);
        }

        private static float EaseInBack(float t)
        {
            t = Mathf.Clamp01(t);
            return (ShrinkBack + 1f) * t * t * t - ShrinkBack * t * t;
        }
    }
}
