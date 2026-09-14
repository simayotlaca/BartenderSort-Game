using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class TimeBoostFlightPresenter : MonoBehaviour
    {
        public const float FlightSeconds = 0.40f;

        public const float StaggerSeconds = 0.05f;

        private const float TokenPopSeconds = 0.10f;
        private const float TokenAbsorbSeconds = 0.08f;
        private const float GlowSeconds = 0.32f;
        private const float TrailFirstSeconds = 0.03f;
        private const float TrailIntervalSeconds = 0.025f;
        private const float TrailLifeSeconds = 0.30f;
        private const float RingSeconds = 0.38f;
        private const float BurstSeconds = 0.46f;
        private const float LabelDelaySeconds = 0.03f;
        private const float LabelSeconds = 0.90f;

        private const float LabelAmountSeconds = 30f;

        private const float SourceLifetimeSeconds = 10f;

        private const int PrewarmUnits = 3;

        private const string FlightFxResourcePath = "Ui/TimeBoost/TimeBoostFlightFx";

        [Tooltip("Optional launch point. Defaults to the time button face, then the bottom-centre of the camera.")]
        [SerializeField] private Transform sourceAnchor;

        [Header("Authored flight layer")]
        [Tooltip("Stretched layer under the same canvas as the order cards.")]
        [SerializeField] private RectTransform layer;
        [Tooltip("One flight's parts. Empty uses Resources/Ui/TimeBoost/TimeBoostFlightFx.")]
        [SerializeField] private TimeBoostFlightFxView flightFxPrefab;
        [Tooltip("Old white star sparks. Kept hidden; the flight unit prefab replaced them.")]
        [SerializeField] private Image[] sparkPool = new Image[0];

        private sealed class Flight
        {
            public OrderCardView Card;
            public int OrderIndex;
            public Vector2 Origin;
            public float StartTime;
            public Action<bool> Completion;
            public bool Arrived;
            public bool CallbackDelivered;
            public bool ShowGlow;
            public bool ShowLabel;
            public TimeBoostFlightFxView Fx;
            public Vector4[] TrailJitter;
            public Vector3[] BurstShape;
        }

        private readonly List<Flight> flights = new List<Flight>();
        private readonly List<Flight> arrivedScratch = new List<Flight>();
        private readonly List<Flight> endedScratch = new List<Flight>();
        private readonly Stack<TimeBoostFlightFxView> freeUnits = new Stack<TimeBoostFlightFxView>();

        private TimeBoostFlightFxView resolvedPrefab;
        private bool prefabWarningIssued;
        private bool hierarchyWarningIssued;

        private int launchedThisBoost;
        private bool boostOriginResolved;
        private Vector2 boostOrigin;
        private float boostDelay;

        private Transform pendingSource;
        private float pendingSourceDelay;
        private float pendingSourceTime;

        private void Awake()
        {
            HideLegacySparks();
        }

        private void Start()
        {
            if (layer == null || ResolvePrefab() == null) return;
            for (int i = 0; i < PrewarmUnits; i++)
            {
                TimeBoostFlightFxView unit = CreateUnit();
                if (unit != null) ReleaseUnit(unit);
            }
        }

        public void SetNextBoostSource(Transform source, float launchDelay)
        {
            pendingSource = source;
            pendingSourceDelay = Mathf.Max(0f, launchDelay);
            pendingSourceTime = Time.unscaledTime;
        }

        public void ClearNextBoostSource() => pendingSource = null;

        public void BeginBoost()
        {
            launchedThisBoost = 0;
            BsAudio.Instance?.Play(BsSfx.TimeBoost, 0.85f);
            ResolveBoostOrigin();
        }

        public bool LaunchTo(OrderCardView card, int expectedOrderIndex, float seconds,
                             Action<bool> completion)
        {
            if (card == null || expectedOrderIndex < 0
                || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
                return false;
            if (!isActiveAndEnabled || !EnsureLayerReady()) return false;
            if (!TryResolveLocalTarget(card, expectedOrderIndex, out _)) return false;
            if (!boostOriginResolved) ResolveBoostOrigin();
            if (!boostOriginResolved) return false;

            TimeBoostFlightFxView fx = TakeUnit();
            if (fx == null) return false;

            var flight = new Flight
            {
                Card = card,
                OrderIndex = expectedOrderIndex,
                Origin = boostOrigin,
                StartTime = Time.unscaledTime + boostDelay + launchedThisBoost * StaggerSeconds,
                Completion = completion,
                ShowGlow = launchedThisBoost == 0,
                ShowLabel = Mathf.Approximately(seconds, LabelAmountSeconds),
                Fx = fx,
                TrailJitter = new Vector4[fx.Trail.Count],
                BurstShape = new Vector3[fx.Burst.Count]
            };
            for (int k = 0; k < flight.TrailJitter.Length; k++)
                flight.TrailJitter[k] = new Vector4(
                    UnityEngine.Random.value - 0.5f, UnityEngine.Random.value - 0.5f,
                    UnityEngine.Random.Range(0.7f, 1.15f), UnityEngine.Random.Range(0f, 90f));
            for (int j = 0; j < flight.BurstShape.Length; j++)
                flight.BurstShape[j] = new Vector3(
                    j / (float)flight.BurstShape.Length * Mathf.PI * 2f + Mathf.PI * 0.5f
                    + UnityEngine.Random.Range(-0.25f, 0.25f),
                    UnityEngine.Random.Range(108f, 154f),
                    UnityEngine.Random.Range(0.7f, 1.1f));

            launchedThisBoost++;
            flights.Add(flight);
            return true;
        }

        public void CancelForOrder(int orderIndex)
        {
            if (orderIndex < 0) return;
            List<Flight> ended = null;
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                Flight flight = flights[i];
                if (flight.OrderIndex != orderIndex) continue;
                flights.RemoveAt(i);
                ReleaseUnit(flight.Fx);
                if (ended == null) ended = new List<Flight>();
                ended.Add(flight);
            }
            if (ended != null) NotifyEnded(ended.ToArray());
        }

        public void CancelAll()
        {
            Flight[] ended = flights.ToArray();
            flights.Clear();
            for (int i = 0; i < ended.Length; i++) ReleaseUnit(ended[i].Fx);
            boostOriginResolved = false;
            pendingSource = null;
            HideLegacySparks();
            NotifyEnded(ended);
        }

        private void OnDisable() => CancelAll();

        private void LateUpdate()
        {
            if (flights.Count == 0) return;
            float now = Time.unscaledTime;

            arrivedScratch.Clear();
            endedScratch.Clear();
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                Flight flight = flights[i];
                bool wasArrived = flight.Arrived;
                bool alive = UpdateFlight(flight, now);
                if (!wasArrived && flight.Arrived) arrivedScratch.Add(flight);
                if (alive) continue;
                flights.RemoveAt(i);
                ReleaseUnit(flight.Fx);
                endedScratch.Add(flight);
            }

            if (arrivedScratch.Count == 0 && endedScratch.Count == 0) return;

            // Callbacks run last, from copies, because they can cancel other flights.
            Flight[] arrived = arrivedScratch.ToArray();
            Flight[] ended = endedScratch.ToArray();
            arrivedScratch.Clear();
            endedScratch.Clear();
            for (int i = arrived.Length - 1; i >= 0; i--)
            {
                if (arrived[i].CallbackDelivered) continue;
                arrived[i].CallbackDelivered = true;
                arrived[i].Completion?.Invoke(true);
            }
            NotifyEnded(ended);
        }

        private static void NotifyEnded(Flight[] ended)
        {
            for (int i = 0; i < ended.Length; i++)
            {
                if (ended[i].CallbackDelivered) continue;
                ended[i].CallbackDelivered = true;
                ended[i].Completion?.Invoke(ended[i].Arrived);
            }
        }

        private bool UpdateFlight(Flight flight, float now)
        {
            float elapsed = now - flight.StartTime;
            if (elapsed < 0f) return true;
            if (!TryResolveLocalTarget(flight.Card, flight.OrderIndex, out Vector2 target))
                return false;

            TimeBoostFlightFxView fx = flight.Fx;
            Vector2 origin = flight.Origin;
            float span = Vector2.Distance(origin, target);
            Vector2 control = (origin + target) * 0.5f
                              + new Vector2((target.x - origin.x) * 0.22f - 41f, span * 0.10f);

            if (flight.ShowGlow && elapsed < GlowSeconds)
            {
                float u = elapsed / GlowSeconds;
                fx.Place(fx.Glow, origin, Mathf.Lerp(0.6f, 1.35f, OutCubic(u)), 0f,
                    0.95f * (1f - InQuad(u)));
            }
            else TimeBoostFlightFxView.Hide(fx.Glow);

            if (elapsed < FlightSeconds)
            {
                float u = elapsed / FlightSeconds;
                float scale = elapsed < TokenPopSeconds
                    ? Mathf.LerpUnclamped(0.25f, 1f, OutBack(elapsed / TokenPopSeconds))
                    : Mathf.Lerp(1f, 0.8f, InQuad(Mathf.Clamp01((u - 0.6f) / 0.4f)));
                fx.Place(fx.Token, Bezier(origin, control, target, InOutCubic(u)), scale,
                    14f * Mathf.Sin(u * Mathf.PI * 2.2f), 1f);
            }
            else if (elapsed < FlightSeconds + TokenAbsorbSeconds)
            {
                float v = (elapsed - FlightSeconds) / TokenAbsorbSeconds;
                fx.Place(fx.Token, target, Mathf.Lerp(0.8f, 0.3f, v), 0f, 1f - v);
            }
            else TimeBoostFlightFxView.Hide(fx.Token);

            for (int k = 0; k < flight.TrailJitter.Length; k++)
            {
                Image part = fx.Trail[k];
                float born = TrailFirstSeconds + k * TrailIntervalSeconds;
                float age = elapsed - born;
                if (born > FlightSeconds - 0.03f || age < 0f || age > TrailLifeSeconds)
                {
                    TimeBoostFlightFxView.Hide(part);
                    continue;
                }
                Vector4 jitter = flight.TrailJitter[k];
                float v = age / TrailLifeSeconds;
                Vector2 at = Bezier(origin, control, target, InOutCubic(born / FlightSeconds));
                fx.Place(part, at + new Vector2(jitter.x * 35f, jitter.y * 35f - 16.5f * v),
                    Mathf.Lerp(0.8f, 0f, InQuad(v)) * jitter.z, jitter.w + 120f * v,
                    0.95f * (1f - v));
            }

            if (elapsed >= FlightSeconds) flight.Arrived = true;
            float impact = elapsed - FlightSeconds;

            if (impact >= 0f && impact < RingSeconds)
            {
                float u = impact / RingSeconds;
                fx.Place(fx.Ring, target, Mathf.Lerp(0.7f, 1.9f, OutCubic(u)), 0f,
                    0.9f * Mathf.Pow(1f - u, 1.6f));
            }
            else TimeBoostFlightFxView.Hide(fx.Ring);

            for (int j = 0; j < flight.BurstShape.Length; j++)
            {
                Image part = fx.Burst[j];
                float u = impact / BurstSeconds;
                if (impact < 0f || u >= 1f)
                {
                    TimeBoostFlightFxView.Hide(part);
                    continue;
                }
                Vector3 shape = flight.BurstShape[j];
                float distance = Mathf.Lerp(37f, shape.y, OutCubic(u));
                Vector2 offset = new Vector2(Mathf.Cos(shape.x) * distance * 1.3f,
                    Mathf.Sin(shape.x) * distance * 0.85f);
                fx.Place(part, target + offset, Mathf.Lerp(shape.z, 0f, InQuad(u)), 150f * u,
                    u < 0.6f ? 1f : 1f - (u - 0.6f) / 0.4f);
            }

            float label = impact - LabelDelaySeconds;
            if (flight.ShowLabel && label >= 0f && label <= LabelSeconds)
            {
                float scale = label < 0.12f
                    ? Mathf.Lerp(0.3f, 1.18f, OutQuad(label / 0.12f))
                    : label < 0.24f
                        ? Mathf.Lerp(1.18f, 1f, InOutQuad((label - 0.12f) / 0.12f))
                        : 1f;
                float alpha = label < 0.55f ? 1f : 1f - InQuad((label - 0.55f) / 0.35f);
                fx.Place(fx.Label, target + new Vector2(0f, 74f + 82f * OutCubic(label / LabelSeconds)),
                    scale, 0f, alpha);
            }
            else TimeBoostFlightFxView.Hide(fx.Label);

            return elapsed < FlightSeconds + LabelDelaySeconds + LabelSeconds;
        }

        private static Vector2 Bezier(Vector2 a, Vector2 control, Vector2 b, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * control + t * t * b;
        }

        private static float InQuad(float u) => u * u;
        private static float OutQuad(float u) => 1f - (1f - u) * (1f - u);
        private static float InOutQuad(float u) =>
            u < 0.5f ? 2f * u * u : 1f - Mathf.Pow(-2f * u + 2f, 2f) / 2f;
        private static float OutCubic(float u) => 1f - Mathf.Pow(1f - u, 3f);
        private static float InOutCubic(float u) =>
            u < 0.5f ? 4f * u * u * u : 1f - Mathf.Pow(-2f * u + 2f, 3f) / 2f;
        private static float OutBack(float u) =>
            1f + 2.70158f * Mathf.Pow(u - 1f, 3f) + 1.70158f * Mathf.Pow(u - 1f, 2f);

        // Flight unit pool.

        private TimeBoostFlightFxView ResolvePrefab()
        {
            if (resolvedPrefab == null)
                resolvedPrefab = flightFxPrefab != null
                    ? flightFxPrefab
                    : Resources.Load<TimeBoostFlightFxView>(FlightFxResourcePath);
            if (resolvedPrefab != null && resolvedPrefab.IsReady) return resolvedPrefab;
            if (!prefabWarningIssued)
            {
                prefabWarningIssued = true;
                Debug.LogError(
                    "Time boost flight prefab missing.", this);
            }
            return null;
        }

        private TimeBoostFlightFxView CreateUnit()
        {
            TimeBoostFlightFxView prefab = ResolvePrefab();
            if (prefab == null || layer == null) return null;
            TimeBoostFlightFxView unit = Instantiate(prefab, layer, false);
            unit.name = prefab.name;
            unit.HideAll();
            return unit;
        }

        private TimeBoostFlightFxView TakeUnit()
        {
            TimeBoostFlightFxView unit = null;
            while (unit == null && freeUnits.Count > 0) unit = freeUnits.Pop();
            if (unit == null) unit = CreateUnit();
            if (unit == null) return null;

            unit.transform.SetAsLastSibling();
            unit.gameObject.SetActive(true);
            SortAboveCardGlass(unit);
            unit.HideAll();
            return unit;
        }

        private void SortAboveCardGlass(TimeBoostFlightFxView unit)
        {
            Canvas unitCanvas = unit.GetComponent<Canvas>();
            Canvas cardCanvas = layer != null ? layer.GetComponentInParent<Canvas>() : null;
            if (unitCanvas == null || cardCanvas == null) return;
            Canvas root = cardCanvas.rootCanvas;
            unitCanvas.overrideSorting = true;
            unitCanvas.sortingLayerID = root.sortingLayerID;
            unitCanvas.sortingOrder = root.sortingOrder + OrderCardVesselView.SortingOrderAboveCanvas + 1;
        }

        private void ReleaseUnit(TimeBoostFlightFxView unit)
        {
            if (unit == null) return;
            unit.HideAll();
            unit.gameObject.SetActive(false);
            freeUnits.Push(unit);
        }

        private void HideLegacySparks()
        {
            if (sparkPool == null) return;
            for (int i = 0; i < sparkPool.Length; i++)
                if (sparkPool[i] != null) sparkPool[i].gameObject.SetActive(false);
        }

        private bool EnsureLayerReady()
        {
            Canvas canvas = layer != null && layer.parent != null
                ? layer.parent.GetComponent<Canvas>()
                : null;
            bool valid = canvas != null
                         && layer.gameObject.activeSelf
                         && layer.GetSiblingIndex() == canvas.transform.childCount - 1
                         && layer.anchorMin == Vector2.zero
                         && layer.anchorMax == Vector2.one
                         && layer.offsetMin == Vector2.zero
                         && layer.offsetMax == Vector2.zero;
            if (valid) return true;
            if (!hierarchyWarningIssued)
            {
                hierarchyWarningIssued = true;
                Debug.LogError(
                    "Time boost flight layer invalid.", this);
            }
            return false;
        }

        private void ResolveBoostOrigin()
        {
            boostOriginResolved = false;
            boostDelay = 0f;
            Transform source = pendingSource;
            float sourceDelay = pendingSourceDelay;
            bool sourceFresh = source != null
                               && Time.unscaledTime - pendingSourceTime <= SourceLifetimeSeconds;
            pendingSource = null;
            if (!EnsureLayerReady()) return;

            Vector2 local;
            if (sourceFresh && TryProjectToLayer(source, out local))
            {
                boostDelay = sourceDelay;
            }
            else
            {
                Transform anchor = sourceAnchor != null
                    ? sourceAnchor
                    : BoosterTrayInput.Current != null
                        ? BoosterTrayInput.Current.AddTimeHighlightAnchor
                        : null;
                if (anchor == null || !TryProjectToLayer(anchor, out local))
                {
                    Vector2 bottomCentre = new Vector2(Screen.width * 0.5f, Screen.height * 0.06f);
                    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            layer, bottomCentre, LayerCamera(), out local))
                        return;
                }
            }

            boostOrigin = local;
            boostOriginResolved = true;
        }

        private Camera LayerCamera()
        {
            Canvas canvas = layer != null ? layer.GetComponentInParent<Canvas>() : null;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            return canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
        }

        private bool TryProjectToLayer(Transform source, out Vector2 local)
        {
            local = default;
            if (source == null || layer == null || !source.gameObject.activeInHierarchy) return false;

            Vector3 world;
            if (source is RectTransform rect) world = rect.TransformPoint(rect.rect.center);
            else
            {
                Renderer renderer = source.GetComponent<Renderer>();
                world = renderer != null ? renderer.bounds.center : source.position;
            }

            Camera sourceCamera;
            Canvas sourceCanvas = source.GetComponentInParent<Canvas>();
            if (sourceCanvas != null)
                sourceCamera = sourceCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : sourceCanvas.worldCamera != null ? sourceCanvas.worldCamera : Camera.main;
            else
            {
                sourceCamera = LayerCamera();
                if (sourceCamera == null) return false;
            }

            Vector2 screen = RectTransformUtility.WorldToScreenPoint(sourceCamera, world);
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                layer, screen, LayerCamera(), out local);
        }

        private bool TryResolveLocalTarget(OrderCardView card, int expectedOrderIndex,
                                           out Vector2 local)
        {
            local = default;
            if (layer == null || card == null || card.Model == null
                || card.Model.RuntimeOrderIndex != expectedOrderIndex
                || !card.TryGetTimerAnchor(out Vector3 world))
                return false;
            local = layer.InverseTransformPoint(world);
            return true;
        }
    }
}
