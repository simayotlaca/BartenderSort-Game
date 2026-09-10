using System;
using System.Collections;
using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Keeps one loading cover alive until the destination has built its first board.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    internal sealed class BartenderLoadingSceneHandoff : MonoBehaviour
    {
        private const double StartupTimeoutSeconds = 6d;

        private BartenderLoadingOverlayPresenter cover;
        private AsyncOperation operation;
        private BartenderLevelController destinationController;
        private BartenderShelfLevelView destinationShelf;
        private BartenderLevelIntroDirector destinationIntro;
        private int presentationVersion;
        private double activationStartedAt;
        private double destinationLoadedAt;
        private bool destinationLoaded;
        private bool ownsRoot;
        private bool settled;

        internal static bool TryBegin(BartenderLoadingOverlayPresenter loadingCover,
            AsyncOperation sceneOperation, int version)
        {
            if (!Application.isPlaying || loadingCover == null || sceneOperation == null
                || sceneOperation.isDone || !loadingCover.IsCurrentPresentation(version)
                || !loadingCover.gameObject.activeInHierarchy
                || loadingCover.GetComponent<BartenderLoadingSceneHandoff>() != null)
                return false;

            Canvas canvas = loadingCover.GetComponent<Canvas>();
            RectTransform rect = loadingCover.transform as RectTransform;
            if (canvas == null || rect == null
                || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                return false;

            BartenderLoadingSceneHandoff handoff = null;
            try
            {
                Canvas sourceCanvas = canvas.rootCanvas;
                CanvasScaler sourceScaler = sourceCanvas.GetComponent<CanvasScaler>();
                CanvasScaler destinationScaler = canvas.GetComponent<CanvasScaler>();
                if (destinationScaler == null)
                    destinationScaler = canvas.gameObject.AddComponent<CanvasScaler>();
                if (sourceScaler != null && sourceScaler != destinationScaler)
                    CopyScaleSettings(sourceScaler, destinationScaler);

                handoff = loadingCover.gameObject.AddComponent<BartenderLoadingSceneHandoff>();
                handoff.cover = loadingCover;
                handoff.operation = sceneOperation;
                handoff.presentationVersion = version;
                handoff.activationStartedAt = Time.realtimeSinceStartupAsDouble;
                SceneManager.sceneLoaded += handoff.HandleSceneLoaded;

                // A nested Canvas previously inherited the menu's scale. Its own scaler takes over now.
                rect.SetParent(null, false);
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.anchoredPosition3D = Vector3.zero;
                rect.sizeDelta = Vector2.zero;
                handoff.ownsRoot = true;
                DontDestroyOnLoad(loadingCover.gameObject);
                Canvas.ForceUpdateCanvases();
                handoff.StartCoroutine(handoff.RunGuarded());
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, loadingCover);
                if (handoff != null)
                {
                    handoff.Cleanup();
                    if (!handoff.ownsRoot) Destroy(handoff);
                }
                return false;
            }
        }

        private static void CopyScaleSettings(CanvasScaler source, CanvasScaler destination)
        {
            destination.uiScaleMode = source.uiScaleMode;
            destination.referencePixelsPerUnit = source.referencePixelsPerUnit;
            destination.scaleFactor = source.scaleFactor;
            destination.referenceResolution = source.referenceResolution;
            destination.screenMatchMode = source.screenMatchMode;
            destination.matchWidthOrHeight = source.matchWidthOrHeight;
            destination.physicalUnit = source.physicalUnit;
            destination.fallbackScreenDPI = source.fallbackScreenDPI;
            destination.defaultSpriteDPI = source.defaultSpriteDPI;
            destination.dynamicPixelsPerUnit = source.dynamicPixelsPerUnit;
        }

        private bool OwnsCover => !settled && cover != null
            && cover.IsCurrentPresentation(presentationVersion);

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!OwnsCover || destinationLoaded || mode != LoadSceneMode.Single) return;
            destinationLoaded = true;
            destinationLoadedAt = Time.realtimeSinceStartupAsDouble;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            try
            {
                // sceneLoaded follows Awake/OnEnable and precedes Start, including First Shift's startup.
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    BartenderLevelController candidate =
                        root.GetComponentInChildren<BartenderLevelController>();
                    if (candidate == null || !candidate.isActiveAndEnabled) continue;
                    destinationController = candidate;
                    destinationShelf = candidate.GetComponent<BartenderShelfLevelView>();
                    destinationIntro = candidate.GetComponent<BartenderLevelIntroDirector>();
                    // A barrier before Start would reject TryStartSavedCampaign/TryStartStandalone.
                    destinationController.LevelLoaded += HandleLevelLoaded;
                    if (destinationShelf != null)
                        destinationShelf.ArmNextCoveredLoad(cover);
                    break;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                Cleanup();
            }
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            if (OwnsCover && destinationController != null)
                destinationController.AcquirePresentationBarrier(this);
        }

        private IEnumerator PresentDestination()
        {
            while (OwnsCover && !operation.isDone)
            {
                if (Time.realtimeSinceStartupAsDouble - activationStartedAt
                    >= BartenderMainMenuPresenter.SceneLoadWaitTimeoutSeconds)
                    throw new TimeoutException("The loading cover timed out waiting for scene activation.");
                yield return null;
            }
            if (!OwnsCover) yield break;

            // All destination Start methods must run before readiness is considered. First Shift itself
            // yields once before loading, so CurrentLevel is also required below.
            yield return null;
            if (!OwnsCover) yield break;
            if (!destinationLoaded || destinationController == null)
                throw new InvalidOperationException("The activated scene has no active gameplay controller.");

            while (OwnsCover && destinationController != null
                && (destinationController.CurrentLevel == null
                    || (destinationShelf != null && destinationShelf.CoveredPresentationPending)
                    || (destinationIntro != null && destinationIntro.CoveredPreparationPending)))
            {
                if (Time.realtimeSinceStartupAsDouble - destinationLoadedAt >= StartupTimeoutSeconds)
                    throw new TimeoutException("The first gameplay board did not finish preparing under its cover.");
                // A level boundary may have cleared presentation owners while installing the board.
                if (destinationController.CurrentLevel != null
                    && !destinationController.IsPresentationBarrierOwnedBy(this))
                    destinationController.AcquirePresentationBarrier(this);
                yield return null;
            }
            if (!OwnsCover || destinationController == null) yield break;
            if (!destinationController.IsPresentationBarrierOwnedBy(this))
                destinationController.AcquirePresentationBarrier(this);

            // The intro has rendered its prepared board and restored the off-screen start pose.
            // Submit that pose before hiding; waiting for the animation itself would deadlock.
            yield return null;
            if (!OwnsCover) yield break;
            yield return cover.FillToCompletion(presentationVersion, true);
        }

        private IEnumerator RunGuarded()
        {
            var pending = new Stack<IEnumerator>();
            pending.Push(PresentDestination());
            try
            {
                while (pending.Count > 0 && OwnsCover)
                {
                    IEnumerator current = pending.Peek();
                    object next = null;
                    bool moved = false;
                    Exception failure = null;
                    try
                    {
                        moved = current.MoveNext();
                        if (moved) next = current.Current;
                    }
                    catch (Exception exception) { failure = exception; }
                    if (failure != null)
                    {
                        Debug.LogException(failure, this);
                        yield break;
                    }
                    if (!moved)
                    {
                        DisposeIterator(pending.Pop());
                        continue;
                    }
                    if (next is IEnumerator nested && !(next is CustomYieldInstruction))
                        pending.Push(nested);
                    else
                        yield return next;
                }
            }
            finally
            {
                while (pending.Count > 0) DisposeIterator(pending.Pop());
                Cleanup();
            }
        }

        private void DisposeIterator(IEnumerator iterator)
        {
            try { (iterator as IDisposable)?.Dispose(); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private void Cleanup()
        {
            if (settled) return;
            settled = true;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            // External HideImmediate changes the version before disabling us. A hidden cover still
            // belongs to this handoff; an active replacement presentation belongs to its newer caller.
            bool releaseCover = cover == null || !cover.Visible
                || cover.PresentationVersion == presentationVersion;
            try
            {
                if (releaseCover && destinationShelf != null && cover != null)
                    destinationShelf.DisarmCoveredLoad(cover);
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            try
            {
                if (destinationController != null)
                {
                    destinationController.LevelLoaded -= HandleLevelLoaded;
                    destinationController.ReleasePresentationBarrier(this);
                }
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            try
            {
                if (ownsRoot && cover != null && cover.IsCurrentPresentation(presentationVersion))
                    cover.HideImmediate();
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            finally
            {
                if (ownsRoot)
                {
                    if (releaseCover) Destroy(gameObject);
                    else Destroy(this);
                }
            }
        }

        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();

    }

    /// <summary>
    /// Transfers music ownership across an uncancellable scene load, including launches with no cover.
    /// The visible loading/home cover keeps its own suspension until its final frame is gone.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    internal sealed class BartenderSceneMusicHandoff : MonoBehaviour
    {
        private const double DestinationReadyTimeoutSeconds = 6d;
        private IDisposable musicSuspension;
        private AsyncOperation operation;

        internal static void TakeOver(IDisposable suspension, AsyncOperation sceneOperation)
        {
            if (suspension == null) return;
            if (!Application.isPlaying || sceneOperation == null)
            {
                suspension.Dispose();
                return;
            }

            GameObject root = null;
            try
            {
                root = new GameObject("Scene music handoff");
                DontDestroyOnLoad(root);
                var handoff = root.AddComponent<BartenderSceneMusicHandoff>();
                handoff.musicSuspension = suspension;
                handoff.operation = sceneOperation;
                handoff.StartCoroutine(handoff.WaitForDestination());
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                suspension.Dispose();
                if (root != null) Destroy(root);
            }
        }

        private IEnumerator WaitForDestination()
        {
            try
            {
                // A slow load remains silent for its entire duration. Unity owns this operation even
                // after the source menu is disabled or its launch reports cancellation.
                yield return operation;
                yield return null;

                Scene destination = SceneManager.GetActiveScene();
                BartenderLevelController controller = null;
                BartenderMainMenuPresenter menu = null;
                foreach (GameObject root in destination.GetRootGameObjects())
                {
                    if (controller == null)
                        controller = root.GetComponentInChildren<BartenderLevelController>();
                    if (menu == null)
                        menu = root.GetComponentInChildren<BartenderMainMenuPresenter>();
                }

                double deadline = Time.realtimeSinceStartupAsDouble + DestinationReadyTimeoutSeconds;
                // First Shift yields before installing its board. A menu intentionally has no level.
                while (!(menu != null && menu.isActiveAndEnabled && menu.Visible)
                    && controller != null && controller.CurrentLevel == null
                    && Time.realtimeSinceStartupAsDouble < deadline)
                    yield return null;

                // Let the destination submit its first ready frame before restoring its selected track.
                yield return null;
            }
            finally
            {
                ReleaseMusic();
                Destroy(gameObject);
            }
        }

        private void ReleaseMusic()
        {
            IDisposable suspension = musicSuspension;
            musicSuspension = null;
            suspension?.Dispose();
        }

        private void OnDisable() => ReleaseMusic();
        private void OnDestroy() => ReleaseMusic();
    }
}
