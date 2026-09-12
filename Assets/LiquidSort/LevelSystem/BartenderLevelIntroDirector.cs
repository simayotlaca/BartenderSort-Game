using System;
using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Brings UI and shelves in from screen edges on every level load, using one sequence. Capture final poses
    /// after layout order 10000, before the first render.
    /// </summary>
    [DefaultExecutionOrder(11000)]
    [DisallowMultipleComponent]
    public sealed class BartenderLevelIntroDirector : MonoBehaviour
    {
        /// <summary>Screen edge the UI group enters from.</summary>
        public enum IntroEdge
        {
            Top,
            Bottom,
            Left,
            Right,
        }

        [Serializable]
        public sealed class IntroChannel
        {
            [Tooltip("Group that flies in. Its resting local/anchored position is read "
                   + "at Awake, so authoring keeps working exactly as it does today.")]
            public Transform Target;
            [Tooltip("Edge the group travels in from. Pick the one it is nearest to; a "
                   + "group crossing the whole screen reads as a swipe, not an entrance.")]
            public IntroEdge From = IntroEdge.Top;
            [Tooltip("Seconds after the intro starts before this group moves.")]
            [Min(0f)] public float Delay;
            [Tooltip("Travel time. UI groups use roughly a quarter second; the complete "
                   + "board can use a slightly longer glide.")]
            [Min(0.01f)] public float Duration = 0.26f;
            [Tooltip("Extra travel past the screen edge, as a share of the group's own "
                   + "size. Keeps a soft shadow or glow from peeking before its cue.")]
            [Range(0f, 1f)] public float Clearance = 0.25f;
            [Tooltip("Overshoot on arrival. Off gives a clean decelerating slide.")]
            public bool Overshoot = true;
        }

        [Header("Level entrance channels")]
        [Tooltip("Groups that fly in, in authored order. An empty or missing target is "
               + "skipped, so a scene that has not been rewired yet still runs.")]
        [SerializeField] private IntroChannel[] channels = Array.Empty<IntroChannel>();

        [Header("Level source")]
        [Tooltip("Publishes one cue for the initial level, every retry and every later level.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Camera used to move the world-space shelf fully beyond the screen edge.")]
        [SerializeField] private Camera worldCamera;

        // Plays at the start of every level and every retry, so it sits under the round's own sounds.
        private const float IntroCueVolume = 0.55f;

        [Header("Timing")]
        [Tooltip("Delay before the first channel moves. One rendered frame of the settled "
               + "background reads better than motion starting on frame zero.")]
        [SerializeField, Min(0f)] private float leadIn = 0.12f;
        [Tooltip("Play the intro automatically when this scene opens. Off lets a host "
               + "drive it through Play() at its own cue.")]
        [SerializeField] private bool playOnStart = true;

        private readonly List<Vector3> restingPositions = new List<Vector3>(8);
        private readonly List<Vector3> offScreenPositions = new List<Vector3>(8);
        private readonly Vector3[] cornerScratch = new Vector3[4];
        private Sequence sequence;
        private Coroutine coverWaitRoutine;
        private BartenderLevelController subscribedController;
        private BartenderLevelController barrierController;
        private BartenderShelfLevelView shelfView;
        private bool coveredPreparationPending;
        private bool posesCaptured;
        private bool levelLoadedSinceEnable;
        private bool startHasRun;
        private int playVersion;

        /// <summary>True while UI is moving to its authored pose.</summary>
        public bool Playing => coverWaitRoutine != null
                            || (sequence != null && sequence.IsActive()
                                && sequence.IsPlaying());

        /// <summary>The loading owner must keep its cover until the board has rendered at rest.</summary>
        internal bool CoveredPreparationPending => coveredPreparationPending;

        private void Awake()
        {
            ResolveDependencies();
            // Move off-screen in Awake before the first gameplay frame renders.
            PoseOffScreen();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            Subscribe();
            levelLoadedSinceEnable = false;

            // Later activations need the same blank starting pose before LevelLoaded arrives.
            if (startHasRun) PoseOffScreen();
        }

        private void Start()
        {
            startHasRun = true;
            // Handle enabling after the controller already loaded its first level.
            if (playOnStart && !levelLoadedSinceEnable) Play();
        }

        private void OnDisable()
        {
            Unsubscribe();
            CancelPlayback();
            SnapToRest();
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            levelLoadedSinceEnable = true;
            Play();
        }

        /// <summary>Moves all groups off-screen using rest poses saved once from the scene. Safe to repeat.</summary>
        public void PoseOffScreen()
        {
            if (channels == null || channels.Length == 0) return;

            if (!posesCaptured) CapturePoses();
            for (int index = 0; index < channels.Length; index++)
            {
                IntroChannel channel = channels[index];
                if (channel == null || channel.Target == null) continue;
                if (index >= offScreenPositions.Count) continue;
                SetPosition(channel.Target, offScreenPositions[index]);
            }
        }

        /// <summary>
        /// Resets the starting pose and plays the entrance, waiting for any loading cover to clear first.
        /// </summary>
        public void Play()
        {
            if (channels == null || channels.Length == 0) return;

            CancelPlayback();
            PoseOffScreen();
            AcquireBarrier();
            int version = ++playVersion;

            if (Application.isPlaying && BartenderLoadingOverlayPresenter.AnyVisible)
            {
                coveredPreparationPending = true;
                coverWaitRoutine = StartCoroutine(WaitForCover(version));
                return;
            }

            BeginSequence(version);
        }

        private IEnumerator WaitForCover(int version)
        {
            // Vessel activation is spread across frames. Render the completed board under the opaque
            // cover before moving it off-screen, so first-use drawing work does not land on the entrance.
            while (version == playVersion && BartenderLoadingOverlayPresenter.AnyVisible
                   && shelfView != null && shelfView.CoveredPresentationPending)
                yield return null;

            if (version != playVersion) yield break;
            if (BartenderLoadingOverlayPresenter.AnyVisible)
            {
                SnapToRest();
                Canvas.ForceUpdateCanvases();
                yield return null;
                if (version != playVersion) yield break;

                // Bounds now include the actual level and the resolved safe-area layout.
                CapturePoses();
            }
            PoseOffScreen();
            BeginSequence(version, true);
            coveredPreparationPending = false;

            while (version == playVersion
                   && BartenderLoadingOverlayPresenter.AnyVisible)
                yield return null;

            coverWaitRoutine = null;
            if (version == playVersion)
            {
                // Keep the first visible frame on the prepared start pose even if a layout callback ran
                // during the loading tail. A covered entrance needs no additional blank lead-in.
                PoseOffScreen();
                if (sequence != null)
                {
                    sequence.Play();
                    PlayIntroCue();
                }
            }
        }

        /// <summary>The board's entrance is otherwise silent, and no other cue claims this moment.</summary>
        private static void PlayIntroCue() =>
            BsAudio.Instance?.Play(BsSfx.LevelIntro, IntroCueVolume);

        private void BeginSequence(int version, bool covered = false)
        {
            if (version != playVersion) return;

            // I keep the intro in one sequence so it can stop together and reuse one tween tree.
            Sequence built = DOTween.Sequence().SetTarget(this)
                .SetUpdate(true).SetRecyclable(true).SetAutoKill(true);

            bool any = false;
            for (int index = 0; index < channels.Length; index++)
            {
                IntroChannel channel = channels[index];
                if (channel == null || channel.Target == null) continue;
                if (index >= restingPositions.Count) continue;

                Tween travel = CreateTravelTween(
                        channel.Target, offScreenPositions[index], restingPositions[index],
                        channel.Duration)
                    .SetEase(channel.Overshoot ? Ease.OutBack : Ease.OutCubic)
                    .SetRecyclable(true);
                built.Insert((covered ? 0f : leadIn) + channel.Delay, travel);
                any = true;
            }

            if (!any)
            {
                built.Kill();
                ReleaseBarrier();
                return;
            }

            // The cue belongs to the first frame of movement, not to a fixed moment: a covered entrance
            // skips the lead-in, so the two paths would otherwise drift apart by that much. A covered
            // sequence starts paused, and WaitForCover plays the cue when it releases it.
            if (!covered) built.InsertCallback(leadIn, PlayIntroCue);

            built.OnComplete(() => FinishSequence(built));
            built.OnKill(() => HandleSequenceKilled(built));
            sequence = built;
            if (covered)
            {
                built.Pause();
                built.ForceInit();
            }
        }

        private void CancelPlayback()
        {
            playVersion++;
            coveredPreparationPending = false;
            if (coverWaitRoutine != null)
            {
                StopCoroutine(coverWaitRoutine);
                coverWaitRoutine = null;
            }

            Sequence running = sequence;
            sequence = null;
            if (running != null && running.IsActive()) running.Kill();
            ReleaseBarrier();
        }

        private void FinishSequence(Sequence finished)
        {
            if (!ReferenceEquals(sequence, finished)) return;
            sequence = null;
            SnapToRest();
            ReleaseBarrier();
        }

        private void HandleSequenceKilled(Sequence killed)
        {
            if (!ReferenceEquals(sequence, killed)) return;
            sequence = null;
            SnapToRest();
            ReleaseBarrier();
        }

        private void SnapToRest()
        {
            if (channels == null) return;
            for (int index = 0; index < channels.Length && index < restingPositions.Count;
                 index++)
            {
                IntroChannel channel = channels[index];
                if (channel == null || channel.Target == null) continue;
                SetPosition(channel.Target, restingPositions[index]);
            }
        }

        private static Tween CreateTravelTween(Transform target, Vector3 origin, Vector3 destination,
                                               float duration)
        {
            if (target is RectTransform rect)
                return rect.DOAnchorPos(new Vector2(destination.x, destination.y), duration)
                    .From(new Vector2(origin.x, origin.y), true);
            return target.DOLocalMove(destination, duration).From(origin, true);
        }

        private static Vector3 ReadPosition(Transform target)
        {
            if (target is RectTransform rect)
            {
                Vector2 anchored = rect.anchoredPosition;
                return new Vector3(anchored.x, anchored.y, rect.localPosition.z);
            }
            return target.localPosition;
        }

        private static void SetPosition(Transform target, Vector3 position)
        {
            if (target is RectTransform rect)
            {
                rect.anchoredPosition = new Vector2(position.x, position.y);
                Vector3 local = rect.localPosition;
                local.z = position.z;
                rect.localPosition = local;
                return;
            }
            target.localPosition = position;
        }

        private static RectTransform ResolveCanvasRect(RectTransform target)
        {
            Canvas canvas = target != null ? target.GetComponentInParent<Canvas>() : null;
            if (canvas == null) return null;
            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            return root.transform as RectTransform;
        }

        private void CapturePoses()
        {
            restingPositions.Clear();
            offScreenPositions.Clear();
            for (int index = 0; index < channels.Length; index++)
            {
                IntroChannel channel = channels[index];
                if (channel == null || channel.Target == null)
                {
                    restingPositions.Add(Vector3.zero);
                    offScreenPositions.Add(Vector3.zero);
                    continue;
                }

                Vector3 rest = ReadPosition(channel.Target);
                restingPositions.Add(rest);
                offScreenPositions.Add(rest + OffScreenDelta(channel));
            }
            posesCaptured = true;
        }

        /// <summary>
        /// Measures the distance to clear the screen edge in parent anchor space. Use real corners so stretched
        /// and safe-area layouts work too.
        /// </summary>
        private Vector3 OffScreenDelta(IntroChannel channel)
        {
            if (channel.Target is RectTransform rect)
                return UiOffScreenDelta(channel, rect);
            return WorldOffScreenDelta(channel);
        }

        private Vector3 UiOffScreenDelta(IntroChannel channel, RectTransform target)
        {
            RectTransform canvasRect = ResolveCanvasRect(target);
            RectTransform reference = canvasRect != null ? canvasRect : target.parent as RectTransform;
            if (reference == null) return Vector3.zero;

            target.GetWorldCorners(cornerScratch);
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int index = 0; index < 4; index++)
            {
                Vector3 local = reference.InverseTransformPoint(cornerScratch[index]);
                minX = Mathf.Min(minX, local.x);
                maxX = Mathf.Max(maxX, local.x);
                minY = Mathf.Min(minY, local.y);
                maxY = Mathf.Max(maxY, local.y);
            }

            Rect bounds = reference.rect;
            float width = Mathf.Max(0f, maxX - minX);
            float height = Mathf.Max(0f, maxY - minY);
            Vector3 delta;
            switch (channel.From)
            {
                case IntroEdge.Bottom:
                    delta = new Vector3(0f, -((maxY - bounds.yMin) + height * channel.Clearance));
                    break;
                case IntroEdge.Left:
                    delta = new Vector3(-((maxX - bounds.xMin) + width * channel.Clearance), 0f);
                    break;
                case IntroEdge.Right:
                    delta = new Vector3((bounds.xMax - minX) + width * channel.Clearance, 0f);
                    break;
                default:
                    delta = new Vector3(0f, (bounds.yMax - minY) + height * channel.Clearance);
                    break;
            }

            // Convert canvas measurements through world space to the group's parent so nested scale and
            // rotation do not shorten travel.
            Transform parent = target.parent;
            if (parent == null || ReferenceEquals(parent, reference)) return delta;
            Vector3 world = reference.TransformVector(delta);
            return parent.InverseTransformVector(world);
        }

        private Vector3 WorldOffScreenDelta(IntroChannel channel)
        {
            Camera camera = ResolveWorldCamera();
            if (camera == null)
                return FallbackWorldDelta(channel, null);

            if (!TryGetViewportBounds(channel.Target, camera,
                                      out Rect viewportBounds, out float depth))
                return FallbackWorldDelta(channel, camera);

            float horizontalSize = Mathf.Max(0.001f, viewportBounds.width);
            float verticalSize = Mathf.Max(0.001f, viewportBounds.height);
            Vector2 viewportDelta;
            switch (channel.From)
            {
                case IntroEdge.Bottom:
                    viewportDelta = new Vector2(
                        0f, -(viewportBounds.yMax + verticalSize * channel.Clearance));
                    break;
                case IntroEdge.Left:
                    viewportDelta = new Vector2(
                        -(viewportBounds.xMax + horizontalSize * channel.Clearance), 0f);
                    break;
                case IntroEdge.Right:
                    viewportDelta = new Vector2(
                        1f - viewportBounds.xMin
                        + horizontalSize * channel.Clearance, 0f);
                    break;
                default:
                    viewportDelta = new Vector2(
                        0f, 1f - viewportBounds.yMin
                            + verticalSize * channel.Clearance);
                    break;
            }

            Vector3 viewportOrigin = camera.ViewportToWorldPoint(
                new Vector3(0.5f, 0.5f, depth));
            Vector3 viewportDestination = camera.ViewportToWorldPoint(
                new Vector3(0.5f + viewportDelta.x,
                            0.5f + viewportDelta.y, depth));
            Vector3 worldDelta = viewportDestination - viewportOrigin;
            Transform parent = channel.Target.parent;
            return parent != null ? parent.InverseTransformVector(worldDelta) : worldDelta;
        }

        private static bool TryGetViewportBounds(Transform target, Camera camera,
                                                 out Rect viewportBounds,
                                                 out float depth)
        {
            viewportBounds = default;
            depth = 0f;
            if (target == null || camera == null) return false;

            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            Bounds combined = default;
            bool found = false;
            for (int pass = 0; pass < 2 && !found; pass++)
            {
                for (int index = 0; index < renderers.Length; index++)
                {
                    Renderer renderer = renderers[index];
                    if (renderer == null || !renderer.enabled) continue;
                    if (pass == 0 && !renderer.gameObject.activeInHierarchy) continue;
                    if (!found)
                    {
                        combined = renderer.bounds;
                        found = true;
                    }
                    else
                    {
                        combined.Encapsulate(renderer.bounds);
                    }
                }
            }
            if (!found) return false;

            Vector3 center = combined.center;
            Vector3 extents = combined.extents;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = center + Vector3.Scale(
                    extents, new Vector3(x, y, z));
                Vector3 viewport = camera.WorldToViewportPoint(corner);
                if (viewport.z <= 0f) continue;
                minX = Mathf.Min(minX, viewport.x);
                maxX = Mathf.Max(maxX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxY = Mathf.Max(maxY, viewport.y);
            }
            if (minX == float.MaxValue) return false;

            depth = camera.WorldToViewportPoint(center).z;
            if (depth <= 0f) return false;
            viewportBounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private Vector3 FallbackWorldDelta(IntroChannel channel, Camera camera)
        {
            float distance = camera != null && camera.orthographic
                ? camera.orthographicSize * 2f * Mathf.Max(1f, camera.aspect)
                : 8f;
            Vector3 worldDirection;
            switch (channel.From)
            {
                case IntroEdge.Bottom:
                    worldDirection = camera != null ? -camera.transform.up : Vector3.down;
                    break;
                case IntroEdge.Left:
                    worldDirection = camera != null ? -camera.transform.right : Vector3.left;
                    break;
                case IntroEdge.Right:
                    worldDirection = camera != null ? camera.transform.right : Vector3.right;
                    break;
                default:
                    worldDirection = camera != null ? camera.transform.up : Vector3.up;
                    break;
            }

            Vector3 worldDelta = worldDirection * distance;
            Transform parent = channel.Target != null ? channel.Target.parent : null;
            return parent != null ? parent.InverseTransformVector(worldDelta) : worldDelta;
        }

        private Camera ResolveWorldCamera()
        {
            return worldCamera;
        }

        private void ResolveDependencies()
        {
            if (controller == null)
                controller = GetComponent<BartenderLevelController>();
            if (shelfView == null && controller != null)
                shelfView = controller.GetComponent<BartenderShelfLevelView>();
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController != null)
                subscribedController.LevelLoaded += HandleLevelLoaded;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
                subscribedController.LevelLoaded -= HandleLevelLoaded;
            subscribedController = null;
        }

        private void AcquireBarrier()
        {
            ReleaseBarrier();
            if (!Application.isPlaying || controller == null
                || controller.CurrentLevel == null)
                return;
            if (controller.AcquirePresentationBarrier(this))
                barrierController = controller;
        }

        private void ReleaseBarrier()
        {
            if (barrierController != null)
                barrierController.ReleasePresentationBarrier(this);
            barrierController = null;
        }
    }
}
