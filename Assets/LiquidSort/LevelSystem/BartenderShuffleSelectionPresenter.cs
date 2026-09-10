using System;
using System.Collections.Generic;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows valid shuffle targets and consumes the next world tap. The controller checks targets and
    /// changes the board.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BartenderShuffleSelectionPresenter : MonoBehaviour,
        IBartenderInputPolicy, IBartenderAcceptedInputConsumer
    {
        [Header("Required rig references")]
        [SerializeField] private BartenderLevelController controller;
        [SerializeField] private BartenderShelfLevelView shelfView;
        [SerializeField] private BartenderPourInteraction pourInteraction;

        [Header("Authored selection state")]
        [Tooltip("Inactive scene-authored root containing the scrim and instruction.")]
        [SerializeField] private GameObject selectionVisualRoot;
        [SerializeField] private Canvas selectionCanvas;
        [SerializeField] private Image fullScreenScrim;
        [SerializeField] private TMP_Text instructionLabel;
        [Tooltip("Added to every renderer of an eligible bottle while the scrim is visible.")]
        [SerializeField, Min(1)] private int eligibleSortingOffset = 256;

        private readonly List<int> eligibleIds = new List<int>(16);
        private readonly HashSet<int> eligibleIdSet = new HashSet<int>();
        private readonly List<SortingFocus> sortingFocuses =
            new List<SortingFocus>(16);

        private BartenderLevelController subscribedController;
        private BartenderShelfLevelView subscribedView;
        private BartenderLevelController barrierController;
        private int armedBoardRevision = -1;
        private bool active;

        private sealed class SortingFocus
        {
            public LiquidBottle Bottle;
            public Renderer[] Renderers;
            public int[] SortingOrders;
        }

        public bool Active => active;
        public string LastRejection { get; private set; }

        private void Awake()
        {
            HideAuthoredState();
            if (!ValidateBindings(out string reason))
                Debug.LogError("Shuffle selection binding error: " + reason, this);
        }

        private void OnEnable()
        {
            Subscribe();
            if (!active) HideAuthoredState();
        }

        private void OnDisable()
        {
            FinishSelection();
            Unsubscribe();
        }

        private void OnDestroy() => FinishSelection();

        private void OnValidate()
        {
            eligibleSortingOffset = Mathf.Max(1, eligibleSortingOffset);
        }

        private void Update()
        {
            if (!active) return;
            if (controller == null || shelfView == null || pourInteraction == null
                || selectionVisualRoot == null
                || !selectionVisualRoot.activeInHierarchy
                || !BartenderLevelController.TryValidatePresentationRoot(
                    selectionVisualRoot, out _)
                || controller.State != BartenderLevelState.Playing
                || controller.BoardRevision != armedBoardRevision
                || !shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.SynchronizationDeferred)
                CancelSelection();
        }

        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector reference is missing.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "BartenderShelfLevelView Inspector reference is missing.";
                return false;
            }
            if (pourInteraction == null)
            {
                reason = "BartenderPourInteraction Inspector reference is missing.";
                return false;
            }
            if (selectionVisualRoot == null || selectionCanvas == null
                || fullScreenScrim == null || instructionLabel == null)
            {
                reason = "The authored shuffle canvas, scrim or instruction binding is missing.";
                return false;
            }
            if (!BartenderLevelController.TryValidatePresentationRoot(
                    selectionVisualRoot, out reason))
            {
                reason = "The authored shuffle presentation cannot be shown: " + reason;
                return false;
            }
            if (ReferenceEquals(selectionVisualRoot, gameObject))
            {
                reason = "The visual root must be separate so it can start inactive.";
                return false;
            }
            if (selectionCanvas.renderMode != RenderMode.ScreenSpaceCamera
                || selectionCanvas.worldCamera == null
                || (!selectionCanvas.isRootCanvas && !selectionCanvas.overrideSorting))
            {
                reason = "The shuffle canvas must be a camera-space sorting root with an "
                       + "explicit camera.";
                return false;
            }
            if (fullScreenScrim.raycastTarget || instructionLabel.raycastTarget)
            {
                reason = "Shuffle scrim and instruction must have Raycast Target disabled.";
                return false;
            }
            if (!fullScreenScrim.transform.IsChildOf(selectionVisualRoot.transform)
                || !instructionLabel.transform.IsChildOf(selectionVisualRoot.transform))
            {
                reason = "The scrim and instruction must belong to the authored visual root.";
                return false;
            }

            reason = null;
            return true;
        }

        public bool CanBegin(out string rejectionReason)
        {
            rejectionReason = null;
            if (active) return true;
            if (!ValidateBindings(out rejectionReason)) return false;
            if (!shelfView.Ready || shelfView.SeatAnimationPlaying
                || shelfView.SynchronizationDeferred)
            {
                rejectionReason = "The bottle presentation is busy.";
                return false;
            }
            return controller.CanPurchaseShuffle(out rejectionReason);
        }

        public bool TryBegin(out string rejectionReason)
        {
            rejectionReason = null;
            LastRejection = null;
            if (active) return true;
            if (!CanBegin(out rejectionReason))
            {
                LastRejection = rejectionReason;
                return false;
            }

            controller.CollectShuffleTargetIds(eligibleIds);
            if (eligibleIds.Count == 0)
            {
                return Reject("There is no bottle whose layers can be shuffled",
                              out rejectionReason);
            }
            if (!pourInteraction.TrySetInputPolicy(this))
            {
                return Reject("Another guided interaction currently owns bottle input",
                              out rejectionReason);
            }

            pourInteraction.ClearSelectionForModal();
            eligibleIdSet.Clear();
            sortingFocuses.Clear();
            for (int i = 0; i < eligibleIds.Count; i++)
            {
                int glassId = eligibleIds[i];
                if (!shelfView.TryGetBottle(glassId, out LiquidBottle bottle)
                    || bottle == null || !bottle.gameObject.activeInHierarchy)
                    continue;

                bottle.GetSortingSnapshot(out Renderer[] renderers, out _);
                if (renderers == null || renderers.Length == 0)
                    continue;

                var currentOrders = new int[renderers.Length];
                for (int rendererIndex = 0; rendererIndex < renderers.Length;
                     rendererIndex++)
                {
                    Renderer renderer = renderers[rendererIndex];
                    currentOrders[rendererIndex] = renderer != null
                        ? renderer.sortingOrder
                        : 0;
                }

                sortingFocuses.Add(new SortingFocus
                {
                    Bottle = bottle,
                    Renderers = (Renderer[])renderers.Clone(),
                    SortingOrders = currentOrders,
                });
                eligibleIdSet.Add(glassId);
            }

            if (eligibleIdSet.Count == 0)
            {
                pourInteraction.ClearInputPolicy(this);
                return Reject("No eligible bottle has an active authored scene binding",
                              out rejectionReason);
            }
            selectionVisualRoot.SetActive(true);
            if (!controller.AcquireVisiblePresentationBarrier(
                    this, selectionVisualRoot))
            {
                FinishSelection();
                return Reject("The level could not pause for shuffle selection",
                              out rejectionReason);
            }

            barrierController = controller;
            armedBoardRevision = controller.BoardRevision;
            active = true;
            for (int i = 0; i < sortingFocuses.Count; i++)
                sortingFocuses[i].Bottle.SetSortingOffset(eligibleSortingOffset);
            return true;
        }

        public void CancelSelection()
        {
            FinishSelection();
        }

        bool IBartenderInputPolicy.Allows(BartenderInputRequest request,
                                          out string rejectionReason)
        {
            rejectionReason = null;
            if (!active) return true;
            if (request.Intent == BartenderInputIntent.BackgroundTap
                || request.Intent == BartenderInputIntent.BottleTap)
                return true;
            rejectionReason = "Choose one of the highlighted bottles first.";
            return false;
        }

        void IBartenderInputPolicy.HandleRejected(BartenderInputRequest request,
                                                  string rejectionReason)
        {
            LastRejection = rejectionReason;
            BsAudio.Instance?.Play(BsSfx.Invalid, 0.42f, 1.14f);
            BartenderHaptics.Light();
        }

        bool IBartenderAcceptedInputConsumer.TryConsume(BartenderInputRequest request)
        {
            if (!active) return false;
            if (request.Intent == BartenderInputIntent.BackgroundTap)
            {
                FinishSelection();
                return true;
            }
            if (request.Intent != BartenderInputIntent.BottleTap) return true;

            int glassId = request.PrimaryGlassId;
            if (!eligibleIdSet.Contains(glassId)
                || !controller.CanSelectAsShuffleTarget(glassId))
            {
                LastRejection = "That bottle cannot be shuffled.";
                BsAudio.Instance?.Play(BsSfx.Invalid, 0.42f, 1.14f);
                BartenderHaptics.Light();
                return true;
            }

            int expectedRevision = armedBoardRevision;
            FinishSelection();
            if (controller.TryPurchaseShuffle(
                    glassId, expectedRevision, out string rejectionReason))
            {
                BsAudio.Instance?.Play(BsSfx.ButtonClick);
                BartenderHaptics.Light();
            }
            else
            {
                LastRejection = rejectionReason;
                BsAudio.Instance?.Play(BsSfx.Invalid, 0.42f, 1.14f);
                BartenderHaptics.Light();
            }
            return true;
        }

        private void FinishSelection()
        {
            active = false;

            for (int focusIndex = 0; focusIndex < sortingFocuses.Count; focusIndex++)
            {
                SortingFocus focus = sortingFocuses[focusIndex];
                if (focus == null) continue;
                int count = Mathf.Min(
                    focus.Renderers?.Length ?? 0, focus.SortingOrders?.Length ?? 0);
                for (int rendererIndex = 0; rendererIndex < count; rendererIndex++)
                {
                    Renderer renderer = focus.Renderers[rendererIndex];
                    if (renderer != null)
                        renderer.sortingOrder = focus.SortingOrders[rendererIndex];
                }
                if (focus.Bottle != null) focus.Bottle.InvalidateRenderers();
            }

            sortingFocuses.Clear();
            eligibleIds.Clear();
            eligibleIdSet.Clear();
            armedBoardRevision = -1;
            HideAuthoredState();
            if (pourInteraction != null)
                pourInteraction.ClearInputPolicy(this);
            if (barrierController != null)
                barrierController.ReleasePresentationBarrier(this);
            barrierController = null;
        }

        private void HideAuthoredState()
        {
            if (selectionVisualRoot != null && selectionVisualRoot != gameObject
                && selectionVisualRoot.activeSelf)
                selectionVisualRoot.SetActive(false);
        }

        private void Subscribe()
        {
            if (subscribedController != controller)
            {
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded -= HandleLevelLoaded;
                    subscribedController.StateChanged -= HandleStateChanged;
                    subscribedController.BoardCommitted -= HandleBoardCommitted;
                }
                subscribedController = controller;
                if (subscribedController != null)
                {
                    subscribedController.LevelLoaded += HandleLevelLoaded;
                    subscribedController.StateChanged += HandleStateChanged;
                    subscribedController.BoardCommitted += HandleBoardCommitted;
                }
            }

            if (subscribedView == shelfView) return;
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedView = shelfView;
            if (subscribedView != null)
                subscribedView.PresentationChanged += HandlePresentationChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
                subscribedController.BoardCommitted -= HandleBoardCommitted;
            }
            if (subscribedView != null)
                subscribedView.PresentationChanged -= HandlePresentationChanged;
            subscribedController = null;
            subscribedView = null;
        }

        private void HandleLevelLoaded(BsLevel _) => CancelSelection();

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Playing) CancelSelection();
        }

        private void HandleBoardCommitted(BartenderBoardChange _)
        {
            if (active && controller != null
                && controller.BoardRevision != armedBoardRevision)
                CancelSelection();
        }

        private void HandlePresentationChanged()
        {
            if (active) CancelSelection();
        }

        private bool Reject(string reason, out string rejectionReason)
        {
            LastRejection = string.IsNullOrEmpty(reason)
                ? "Shuffle selection was rejected."
                : reason;
            rejectionReason = LastRejection;
            return false;
        }
    }
}
