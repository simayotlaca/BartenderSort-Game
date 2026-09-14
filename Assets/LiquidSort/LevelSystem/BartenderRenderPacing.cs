using DG.Tweening;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>
    /// On phones the game keeps updating and reading touches 60 times a second, but while nothing moves
    /// only every second frame is drawn (30 fps). A touch, pour, delivery, board settling, playing tween,
    /// startup intro, loading cover or home transition switches drawing straight back to every frame.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class BartenderRenderPacing : MonoBehaviour
    {
        private const int CalmFrameInterval = 2;
        // Pours, deliveries and the order/cheers beats after them all start from a touch.
        private const float TouchTail = 3f;
        private const float MotionTail = 0.35f;

        private static float smoothUntil;
        private BartenderPourInteraction pourInteraction;
        private bool pourInteractionSearched;
        private bool pourInteractionFound;

        /// <summary>Asks for full-rate drawing for a while, for motion the automatic checks do not see.</summary>
        internal static void KeepSmooth(float seconds) =>
            smoothUntil = Mathf.Max(smoothUntil, Time.unscaledTime + Mathf.Max(0f, seconds));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            smoothUntil = 0f;
            if (Application.platform != RuntimePlatform.Android
                && Application.platform != RuntimePlatform.IPhonePlayer)
            {
                return;
            }

            var host = new GameObject("Render Pacing") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(host);
            host.AddComponent<BartenderRenderPacing>();
        }

        private void OnEnable() => SceneManager.sceneLoaded += HandleSceneLoaded;

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            OnDemandRendering.renderFrameInterval = 1;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            pourInteraction = null;
            pourInteractionSearched = false;
            KeepSmooth(MotionTail);
        }

        private void Update()
        {
            if (Input.touchCount > 0 || Input.GetMouseButton(0)) KeepSmooth(TouchTail);
            else if (IsMoving()) KeepSmooth(MotionTail);

            int interval = Time.unscaledTime < smoothUntil ? 1 : CalmFrameInterval;
            if (OnDemandRendering.renderFrameInterval != interval)
                OnDemandRendering.renderFrameInterval = interval;
        }

        private bool IsMoving()
        {
            if (DOTween.TotalPlayingTweens() > 0
                || BartenderStartupIntro.IsPlaying
                || BartenderHomeSceneTransition.IsHoldingResultFrame
                || BartenderLoadingOverlayPresenter.AnyVisible)
            {
                return true;
            }

            // One search per loaded scene (the menu has none), and again if a found one was destroyed.
            if (!pourInteractionSearched || (pourInteractionFound && pourInteraction == null))
            {
                pourInteraction = FindFirstObjectByType<BartenderPourInteraction>();
                pourInteractionSearched = true;
                pourInteractionFound = pourInteraction != null;
            }
            return pourInteraction != null
                   && (pourInteraction.Busy || pourInteraction.BoardPresentationSettling);
        }
    }
}
