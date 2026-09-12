using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Plays the shared toast once per app launch, before menu interaction or First Shift.</summary>
    [DisallowMultipleComponent]
    internal sealed class BartenderStartupIntro : MonoBehaviour
    {
        private const string MenuScene = "SortingShelfShowcase";
        private const string ToastResource = "Ui/Result/CheersToast/ManualRig/CheersToast_Manual";
        private static readonly int ToastState = Animator.StringToHash("Base Layer.Toast One Shot");
        private static bool startupPending;
        private static BartenderStartupIntro active;

        private Animator toastAnimator;
        private CanvasGroup coverGroup;
        private RectTransform toastComposition;
        private RectTransform toastViewport;
        private IDisposable musicSuspension;

        internal static bool IsPlaying => startupPending || (active != null && active.isActiveAndEnabled);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            startupPending = true;
            active = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void TryShow()
        {
            // Resolve startup once, before Start lets the menu launch First Shift or show rewards.
            startupPending = false;
            Scene scene = SceneManager.GetActiveScene();
            // Entering gameplay directly in the Editor must not play a menu introduction.
            if (scene.name != MenuScene) return;

            GameObject root = null;
            try
            {
                GameObject prefab = Resources.Load<GameObject>(ToastResource);
                if (prefab == null)
                {
                    Debug.LogWarning("Startup toast is unavailable; continuing to the menu.");
                    return;
                }

                root = new GameObject("Bartender Startup Intro", typeof(RectTransform),
                    typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup),
                    typeof(GraphicRaycaster));
                root.layer = 5;
                SceneManager.MoveGameObjectToScene(root, scene);
                var intro = root.AddComponent<BartenderStartupIntro>();
                intro.musicSuspension = BsAudio.SuspendBackgroundMusic();
                intro.BuildCover(prefab);
                active = intro;
            }
            catch (Exception exception)
            {
                // Missing or invalid presentation assets must never block a new player's first session.
                Debug.LogException(exception);
                if (root != null)
                {
                    root.SetActive(false);
                    Destroy(root);
                }
            }
        }

        private void BuildCover(GameObject prefab)
        {
            Canvas canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32700;
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(720f, 1280f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            coverGroup = GetComponent<CanvasGroup>();
            coverGroup.interactable = false;
            coverGroup.blocksRaycasts = true;

            var backdrop = new GameObject("Startup Background", typeof(RectTransform), typeof(Image));
            backdrop.layer = 5;
            backdrop.transform.SetParent(transform, false);
            Image background = backdrop.GetComponent<Image>();
            background.rectTransform.anchorMin = Vector2.zero;
            background.rectTransform.anchorMax = Vector2.one;
            background.rectTransform.offsetMin = background.rectTransform.offsetMax = Vector2.zero;
            background.color = new Color(0.022f, 0.076f, 0.160f, 1f);
            background.raycastTarget = true;

            // Preserve the approved preview's authored anchor space and center the entire composition.
            // Only this container changes placement; the shared logo, glass and liquid curves stay intact.
            var frame = new GameObject("Toast Composition", typeof(RectTransform));
            frame.layer = 5;
            var frameRect = (RectTransform)frame.transform;
            frameRect.SetParent(transform, false);
            toastComposition = frameRect;
            toastViewport = transform as RectTransform;
            RefreshToastLayout();

            GameObject toast = Instantiate(prefab, frameRect, false);
            toast.name = "CheersToast_Manual";
            toast.SetActive(true);
            foreach (Canvas childCanvas in toast.GetComponentsInChildren<Canvas>(true))
                childCanvas.overrideSorting = false;
            // Startup has its own opaque full-screen background instead of the result popup's dimmer.
            Transform dimmer = toast.transform.Find("Dimmer");
            if (dimmer != null) dimmer.gameObject.SetActive(false);
            toastAnimator = toast.GetComponent<Animator>();
            if (toastAnimator == null || toastAnimator.runtimeAnimatorController == null)
                throw new InvalidOperationException("Startup toast needs the shared animation controller.");
            toastAnimator.enabled = true;
            toastAnimator.updateMode = AnimatorUpdateMode.UnscaledTime;
            toastAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            toastAnimator.Rebind();
            if (!toastAnimator.HasState(0, ToastState))
                throw new InvalidOperationException("Startup toast animation state is missing.");
            toastAnimator.Play(ToastState, 0, 0f);
            toastAnimator.Update(0f);
            toast.GetComponent<CheersToastRefinedAnimation>()?.Sample(0f);
            // Hold the first frame without a zero playback speed (which can give UI effects an
            // infinite state duration while they calculate their meshes).
            toastAnimator.enabled = false;
        }

        private IEnumerator Start()
        {
            try
            {
                // Every scene Awake (including BsAudio) and the initial UI layout finish first.
                yield return null;
                toastAnimator.enabled = true;
                toastAnimator.speed = 1f;
                toastAnimator.Play(ToastState, 0, 0f);
                toastAnimator.Update(0f);
                toastAnimator.GetComponent<CheersToastRefinedAnimation>()?.Sample(0f);
                BsAudio.Instance?.Play(BsSfx.StartupLogo, 0.9f);

                float elapsed = 0f;
                while (elapsed < BartenderCheersSequence.Duration)
                {
                    float fade = Mathf.InverseLerp(BartenderCheersSequence.CardRevealTime,
                        BartenderCheersSequence.Duration, elapsed);
                    coverGroup.alpha = 1f - Mathf.SmoothStep(0f, 1f, fade);
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }
            }
            finally
            {
                if (active == this) active = null;
                gameObject.SetActive(false);
                Destroy(gameObject);
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            RefreshToastLayout();
        }

        private void RefreshToastLayout()
        {
            BartenderCheersSequence.LayoutToastComposition(toastComposition, toastViewport);
        }

        private void OnDisable()
        {
            if (active == this) active = null;
            musicSuspension?.Dispose();
            musicSuspension = null;
        }
    }
}
