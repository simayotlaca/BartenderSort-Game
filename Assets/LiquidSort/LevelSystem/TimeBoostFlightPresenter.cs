using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Moves pooled sparks from the time booster to order timers. Its separate effect layer spans both
    /// hierarchies and uses only authored Images.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TimeBoostFlightPresenter : MonoBehaviour
    {
        /// <summary>Flight duration; the card reacts after this delay.</summary>
        public const float FlightSeconds = 0.42f;

        /// <summary>Delay between sparks going to different cards.</summary>
        public const float StaggerSeconds = 0.07f;

        private const float ArcHeightRatio = 0.34f;

        [Tooltip("Optional launch point. Defaults to the bottom-centre of the camera.")]
        [SerializeField] private Transform sourceAnchor;

        [Header("Authored flight pool")]
        [Tooltip("Stretched layer under the same canvas as the order cards.")]
        [SerializeField] private RectTransform layer;
        [Tooltip("Inactive spark pool with sprites, colours and sizes already set.")]
        [SerializeField] private Image[] sparkPool = new Image[0];

        private readonly List<Tween> liveFlights = new List<Tween>();
        private readonly Dictionary<Tween, int> orderByFlight =
            new Dictionary<Tween, int>();
        private Color[] sparkRestColors = new Color[0];
        private Vector3[] sparkRestScales = new Vector3[0];
        private bool poolCaptured;
        private bool hierarchyWarningIssued;
        private int launchedThisBoost;

        private void Awake()
        {
            CaptureAuthoredPool();
            ResetSparkPool();
        }

        /// <summary>Starts one time purchase. Play audio once per boost, not once per spark.</summary>
        public void BeginBoost()
        {
            launchedThisBoost = 0;
            BsAudio.Instance?.Play(BsSfx.TimeBoost, 0.85f);
        }

        /// <summary>
        /// Flies to <paramref name="card"/>'s timer and cancels if its order changes. Calls back once: true
        /// on arrival, false on cancellation.
        /// </summary>
        public bool LaunchTo(OrderCardView card, int expectedOrderIndex, float seconds,
                             Action<bool> completion)
        {
            if (card == null || expectedOrderIndex < 0
                || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f)
                return false;
            if (!TryResolveTarget(card, expectedOrderIndex, out Vector3 target)) return false;
            if (!isActiveAndEnabled) return false;

            if (!ValidateAuthoredPool(card)) return false;

            float delay = launchedThisBoost * StaggerSeconds;
            launchedThisBoost++;

            Vector3 origin = ResolveOrigin(target);
            Image image = AcquireSpark(origin);
            if (image == null) return false;
            Tween flight = BuildFlight(image, card, expectedOrderIndex, origin,
                delay, completion);
            if (flight == null)
            {
                RecycleSpark(image);
                return false;
            }

            liveFlights.Add(flight);
            orderByFlight[flight] = expectedOrderIndex;
            return true;
        }

        /// <summary>Cancels flights to a delivered card. Other sparks keep following their moving timers.</summary>
        public void CancelForOrder(int orderIndex)
        {
            if (orderIndex < 0) return;
            for (int i = liveFlights.Count - 1; i >= 0; i--)
            {
                Tween tween = liveFlights[i];
                if (tween == null || !orderByFlight.TryGetValue(tween, out int owner)
                    || owner != orderIndex)
                    continue;
                if (tween.IsActive()) tween.Kill();
            }
        }

        /// <summary>Clears all sparks on level changes or pause.</summary>
        public void CancelAll()
        {
            // Walk backward because OnKill removes its flight from this list.
            for (int i = liveFlights.Count - 1; i >= 0; i--)
            {
                Tween tween = liveFlights[i];
                if (tween != null && tween.IsActive()) tween.Kill();
            }
            liveFlights.Clear();
            orderByFlight.Clear();
            ResetSparkPool();
        }

        private void OnDisable() => CancelAll();

        // Authored pool.

        private bool ValidateAuthoredPool(OrderCardView card)
        {
            if (!poolCaptured) CaptureAuthoredPool();
            if (card == null || card.Rt == null) return false;
            Canvas canvas = card.Rt.GetComponentInParent<Canvas>();
            bool valid = canvas != null && layer != null
                         && layer.parent == canvas.transform
                         && layer.gameObject.activeSelf
                         && layer.GetSiblingIndex() == canvas.transform.childCount - 1
                         && layer.anchorMin == Vector2.zero
                         && layer.anchorMax == Vector2.one
                         && layer.offsetMin == Vector2.zero
                         && layer.offsetMax == Vector2.zero
                         && sparkPool != null && sparkPool.Length > 0;
            if (valid)
            {
                for (int i = 0; i < sparkPool.Length; i++)
                {
                    Image spark = sparkPool[i];
                    if (spark == null || !spark.transform.IsChildOf(layer)
                        || spark.sprite == null || spark.raycastTarget
                        || !spark.preserveAspect)
                    {
                        valid = false;
                        break;
                    }
                }
            }

            if (valid) return true;
            if (!hierarchyWarningIssued)
            {
                hierarchyWarningIssued = true;
                Debug.LogError(
                    "Time Boost Flight Layer Canvas'ın aktif, tam-stretch son child'ı "
                    + "değil ya da Spark Pool sprite/preserveAspect/raycast ayarı geçersiz.",
                    this);
            }
            return false;
        }

        private void CaptureAuthoredPool()
        {
            int count = sparkPool != null ? sparkPool.Length : 0;
            sparkRestColors = new Color[count];
            sparkRestScales = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Image spark = sparkPool[i];
                sparkRestColors[i] = spark != null ? spark.color : Color.white;
                sparkRestScales[i] = spark != null
                    ? spark.rectTransform.localScale
                    : Vector3.one;
            }
            poolCaptured = true;
        }

        /// <summary>Uses the anchor as the origin, or the camera's bottom centre when none is assigned.</summary>
        private Vector3 ResolveOrigin(Vector3 target)
        {
            if (sourceAnchor != null)
            {
                Vector3 from = sourceAnchor.position;
                from.z = target.z;
                return from;
            }

            // Shared tray access finds the origin across separate hierarchies.
            Transform button = BoosterTrayInput.Current != null
                ? BoosterTrayInput.Current.AddTimeAnchor
                : null;
            if (button != null)
            {
                Vector3 from = button.position;
                from.z = target.z;
                return from;
            }

            Camera camera = Camera.main;
            if (camera == null) return target + Vector3.down * 3f;

            float depth = camera.orthographic
                ? Mathf.Abs(target.z - camera.transform.position.z)
                : Vector3.Dot(target - camera.transform.position, camera.transform.forward);
            Vector3 bottomCenter =
                camera.ViewportToWorldPoint(new Vector3(0.5f, 0.06f, Mathf.Max(0.01f, depth)));
            bottomCenter.z = target.z;
            return bottomCenter;
        }

        private Image AcquireSpark(Vector3 origin)
        {
            if (sparkPool == null) return null;
            for (int i = 0; i < sparkPool.Length; i++)
            {
                Image image = sparkPool[i];
                if (image == null || image.gameObject.activeSelf) continue;

                RectTransform rect = image.rectTransform;
                rect.DOKill(false);
                rect.position = origin;
                rect.localScale = i < sparkRestScales.Length
                    ? sparkRestScales[i]
                    : Vector3.one;
                image.color = i < sparkRestColors.Length
                    ? sparkRestColors[i]
                    : Color.white;
                image.gameObject.SetActive(true);
                return image;
            }
            return null;
        }

        private void RecycleSpark(Image image)
        {
            if (image == null) return;
            int index = Array.IndexOf(sparkPool, image);
            if (index >= 0)
            {
                image.color = index < sparkRestColors.Length
                    ? sparkRestColors[index]
                    : Color.white;
                image.rectTransform.localScale = index < sparkRestScales.Length
                    ? sparkRestScales[index]
                    : Vector3.one;
            }
            image.gameObject.SetActive(false);
        }

        private void ResetSparkPool()
        {
            if (sparkPool == null) return;
            for (int i = 0; i < sparkPool.Length; i++)
                RecycleSpark(sparkPool[i]);
        }

        // Spark flight.

        private Tween BuildFlight(Image image, OrderCardView card,
                                  int expectedOrderIndex, Vector3 origin,
                                  float delay, Action<bool> completion)
        {
            if (image == null) return null;
            var rt = (RectTransform)image.transform;

            bool impactDelivered = false;
            bool callbackDelivered = false;
            Sequence sequence = null;
            sequence = DOTween.Sequence()
                .SetTarget(rt).SetUpdate(true).SetRecyclable(true);
            if (delay > 0f) sequence.AppendInterval(delay);

            sequence.Append(DOVirtual.Float(0f, 1f, FlightSeconds, t =>
                {
                    if (rt == null) return;
                    if (!TryResolveTarget(card, expectedOrderIndex, out Vector3 target))
                    {
                        if (sequence != null && sequence.IsActive()) sequence.Kill(false);
                        return;
                    }

                    // Read the timer centre again as its card moves. The order-ID check keeps the spark
                    // from following a reused card.
                    Vector3 mid = (origin + target) * 0.5f;
                    float span = Vector3.Distance(origin, target);
                    Vector3 control = mid + Vector3.up * (span * ArcHeightRatio);
                    float u = 1f - t;
                    rt.position = u * u * origin
                                  + 2f * u * t * control
                                  + t * t * target;
                })
                .SetEase(Ease.InOutQuad).SetRecyclable(true));

            // Grow at launch and squash on arrival so the timer seems to absorb it.
            sequence.Insert(delay, rt.DOScale(1f, FlightSeconds * 0.42f)
                .SetEase(Ease.OutBack).SetRecyclable(true));
            sequence.Insert(delay + FlightSeconds * 0.62f,
                rt.DOScale(0.34f, FlightSeconds * 0.38f)
                    .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.Insert(delay + FlightSeconds * 0.66f,
                image.DOFade(0f, FlightSeconds * 0.34f)
                    .SetEase(Ease.InQuad).SetRecyclable(true));

            sequence.OnComplete(() =>
            {
                impactDelivered = true;
                if (callbackDelivered) return;
                callbackDelivered = true;
                completion?.Invoke(true);
            });
            sequence.OnKill(() =>
            {
                liveFlights.Remove(sequence);
                orderByFlight.Remove(sequence);
                RecycleSpark(image);
                if (impactDelivered || callbackDelivered) return;
                callbackDelivered = true;
                completion?.Invoke(false);
            });
            return sequence;
        }

        private static bool TryResolveTarget(OrderCardView card, int expectedOrderIndex,
                                             out Vector3 target)
        {
            target = default;
            return card != null && card.Model != null
                   && card.Model.RuntimeOrderIndex == expectedOrderIndex
                   && card.TryGetTimerAnchor(out target);
        }
    }
}
