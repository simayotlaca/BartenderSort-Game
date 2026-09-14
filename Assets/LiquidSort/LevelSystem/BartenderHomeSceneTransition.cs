using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    internal sealed class BartenderHomeSceneTransition : MonoBehaviour
    {
        private enum Phase { Capturing, Covered, Loading, MenuLayout, Finished }

        private const double CaptureTimeoutSeconds = 1d;
        private const double HandoffTimeoutSeconds = 30d;
        private const double MenuLayoutTimeoutSeconds = 3d;
        private const int MaximumRetainedFrameDimension = 1280;
        private static BartenderHomeSceneTransition active;

        private BartenderTerminalCommandReceipt receipt;
        private string destinationScene;
        private Phase phase;
        private CanvasGroup coverGroup;
        private RawImage frameImage;
        private RenderTexture capturedFrame;
        private Coroutine captureRoutine;
        private AsyncOperation sceneLoad;
        private int destinationSceneHandle;
        private int sceneActivatedFrame;
        private double deadline;
        private IDisposable musicSuspension;

        internal static bool IsHoldingResultFrame => active != null && active.phase != Phase.Finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => active = null;

        internal static BartenderHomeSceneTransition Begin(
            BartenderTerminalCommandReceipt expected, string destination)
        {
            if (!expected.IsValid || string.IsNullOrWhiteSpace(destination)) return null;
            if (active != null) return active.receipt == expected ? active : null;

            GameObject root = null;
            BartenderHomeSceneTransition view = null;
            try
            {
                root = new GameObject("Home scene handoff", typeof(RectTransform),
                    typeof(Canvas), typeof(CanvasGroup), typeof(GraphicRaycaster));
                DontDestroyOnLoad(root);
                view = root.AddComponent<BartenderHomeSceneTransition>();
                view.musicSuspension = BsAudio.SuspendBackgroundMusic();
                active = view;
                view.receipt = expected;
                view.destinationScene = destination;
                view.BuildCover(root);
                view.deadline = Time.realtimeSinceStartupAsDouble + CaptureTimeoutSeconds;
                SceneManager.sceneLoaded += view.HandleSceneLoaded;
                try { view.captureRoutine = view.StartCoroutine(view.CaptureAfterRender()); }
                catch (Exception exception)
                {
                    Debug.LogException(exception, view);
                    view.ShowCover();
                }
                return view;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (view != null) view.Finish();
                else if (root != null) Destroy(root);
                return null;
            }
        }

        private void BuildCover(GameObject root)
        {
            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            coverGroup = root.GetComponent<CanvasGroup>();
            coverGroup.alpha = 0f;
            coverGroup.interactable = false;
            coverGroup.blocksRaycasts = true;

            var imageObject = new GameObject("Previous frame", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(root.transform, false);
            frameImage = imageObject.GetComponent<RawImage>();
            RectTransform rect = frameImage.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            frameImage.raycastTarget = true;
            // A real opaque fallback also covers headless/unsupported capture and allocation failure.
            frameImage.color = new Color32(49, 20, 55, 255);
        }

        private IEnumerator CaptureAfterRender()
        {
            yield return new WaitForEndOfFrame();
            captureRoutine = null;
            if (!Owns(receipt) || phase != Phase.Capturing) yield break;
            try
            {
                if (Screen.width > 0 && Screen.height > 0)
                {
                    capturedFrame = CaptureResultFrame(Screen.width, Screen.height);
                    if (capturedFrame != null)
                    {
                        frameImage.texture = capturedFrame;
                        frameImage.uvRect = SystemInfo.graphicsUVStartsAtTop
                            ? new Rect(0f, 1f, 1f, -1f)
                            : new Rect(0f, 0f, 1f, 1f);
                        frameImage.color = Color.white;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Result frame capture unavailable; using the menu transition cover. "
                    + exception.Message, this);
            }
            ShowCover();
        }

        private RenderTexture CaptureResultFrame(int width, int height)
        {
            Vector2Int retainedSize = RetainedFrameSize(width, height);
            RenderTexture nativeFrame = null;
            RenderTexture retainedFrame = null;
            RenderTexture previousActive = RenderTexture.active;
            bool previousSrgbWrite = GL.sRGBWrite;
            try
            {
                // ScreenCapture does not promise resampling into a smaller target. Capture the full
                // framebuffer first so the result and overlay UI cannot be cropped.
                nativeFrame = CreateFrameTexture(width, height, "Result capture source");
                if (!nativeFrame.Create()) return null;
                ScreenCapture.CaptureScreenshotIntoRenderTexture(nativeFrame);

                if (retainedSize.x == width && retainedSize.y == height)
                {
                    RenderTexture result = nativeFrame;
                    nativeFrame = null;
                    result.name = "Result handoff frame";
                    return result;
                }

                retainedFrame = CreateFrameTexture(retainedSize.x, retainedSize.y,
                    "Result handoff frame");
                if (!retainedFrame.Create()) return null;
                GL.sRGBWrite = retainedFrame.sRGB;
                Graphics.Blit(nativeFrame, retainedFrame);
                RenderTexture boundedResult = retainedFrame;
                retainedFrame = null;
                return boundedResult;
            }
            finally
            {
                TryCleanup(() => { RenderTexture.active = previousActive; });
                TryCleanup(() => { GL.sRGBWrite = previousSrgbWrite; });
                // Only the bounded texture survives into scene loading. Capture briefly needs both
                // targets; explicitly release the native target instead of keeping it in a temp pool.
                ReleaseCaptureTexture(nativeFrame);
                ReleaseCaptureTexture(retainedFrame);
            }
        }

        private static Vector2Int RetainedFrameSize(int width, int height)
        {
            float scale = Mathf.Min(1f,
                MaximumRetainedFrameDimension / (float)Mathf.Max(width, height));
            return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(width * scale)),
                Mathf.Max(1, Mathf.RoundToInt(height * scale)));
        }

        private static RenderTexture CreateFrameTexture(int width, int height, string label) =>
            new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = label,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };

        private void ReleaseCaptureTexture(RenderTexture texture)
        {
            TryCleanup(() => { if (texture != null) texture.Release(); });
            TryCleanup(() => { if (texture != null) Destroy(texture); });
        }

        internal bool IsCovered(BartenderTerminalCommandReceipt expected) =>
            Owns(expected) && phase == Phase.Covered;

        internal void AcceptHandoff(BartenderTerminalCommandReceipt expected)
        {
            if (!IsCovered(expected)) return;
            phase = Phase.Loading;
            deadline = Time.realtimeSinceStartupAsDouble + HandoffTimeoutSeconds;
        }

        internal static void RecordSceneLoad(
            BartenderTerminalCommandReceipt expected, AsyncOperation operation)
        {
            if (active != null && active.Owns(expected)
                && active.phase == Phase.Loading && operation != null)
                active.sceneLoad = operation;
        }

        internal void CancelBeforeAcceptance(BartenderTerminalCommandReceipt expected)
        {
            if (Owns(expected) && (phase == Phase.Capturing || phase == Phase.Covered))
                Finish();
        }

        private bool Owns(BartenderTerminalCommandReceipt expected) =>
            active == this && phase != Phase.Finished && receipt == expected;

        private void ShowCover()
        {
            if (phase != Phase.Capturing) return;
            phase = Phase.Covered;
            coverGroup.alpha = 1f;
            deadline = Time.realtimeSinceStartupAsDouble + MenuLayoutTimeoutSeconds;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Owns(receipt) || phase != Phase.Loading || sceneLoad == null
                || !string.Equals(scene.name, destinationScene, StringComparison.Ordinal)) return;
            phase = Phase.MenuLayout;
            destinationSceneHandle = scene.handle;
            sceneActivatedFrame = Time.frameCount;
            deadline = Time.realtimeSinceStartupAsDouble + MenuLayoutTimeoutSeconds;
        }

        private void Update()
        {
            if (!Owns(receipt))
            {
                Finish();
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (phase == Phase.Capturing && now >= deadline)
            {
                Coroutine pendingCapture = captureRoutine;
                captureRoutine = null;
                if (pendingCapture != null) StopCoroutine(pendingCapture);
                ShowCover();
            }
            else if (phase == Phase.MenuLayout && Time.frameCount > sceneActivatedFrame)
            {
                BartenderMainMenuPresenter menu = FindFirstObjectByType<BartenderMainMenuPresenter>();
                if (menu != null && menu.gameObject.scene.handle == destinationSceneHandle
                    && menu.isActiveAndEnabled && menu.Visible)
                {
                    Canvas.ForceUpdateCanvases();
                    Finish();
                }
            }

            if ((phase == Phase.Covered || phase == Phase.Loading || phase == Phase.MenuLayout)
                && now >= deadline)
            {
                // Release only the visual cover. A started Unity load still belongs to Session and must
                // never be canceled or retried as a new command just because this view timed out.
                Debug.LogWarning("Home scene handoff cover timed out; releasing its visual input block.", this);
                Finish();
            }
        }

        private void OnDisable() => Finish();

        private void Finish()
        {
            if (phase == Phase.Finished) return;
            phase = Phase.Finished;
            if (active == this) active = null;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            Coroutine pendingCapture = captureRoutine;
            captureRoutine = null;
            RenderTexture frame = capturedFrame;
            capturedFrame = null;
            sceneLoad = null;
            TryCleanup(() =>
            {
                if (coverGroup != null)
                {
                    coverGroup.alpha = 0f;
                    coverGroup.blocksRaycasts = false;
                }
            });
            TryCleanup(() => { if (pendingCapture != null) StopCoroutine(pendingCapture); });
            TryCleanup(() => { if (frameImage != null) frameImage.texture = null; });
            TryCleanup(() => { if (frame != null) frame.Release(); });
            TryCleanup(() => { if (frame != null) Destroy(frame); });
            TryCleanup(() =>
            {
                IDisposable suspension = musicSuspension;
                musicSuspension = null;
                suspension?.Dispose();
            });
            TryCleanup(() => Destroy(gameObject));
        }

        private void TryCleanup(Action step)
        {
            try { step(); }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }
}
