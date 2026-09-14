using System;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BoosterBarPresenter : MonoBehaviour
    {
        [Header("Rig references")]
        [Tooltip("Uses a component on this object if empty.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Waits for glass seating and presentation updates.")]
        [SerializeField] private BartenderShelfLevelView shelfView;
        [Tooltip("Optional. Blocks boosters while pouring.")]
        [SerializeField] private BartenderPourInteraction pourInteraction;
        [Tooltip("Input for the world-space booster bar.")]
        [SerializeField] private BoosterTrayInput trayInput;
        [Tooltip("Target selection state for the shuffle button.")]
        [SerializeField] private BartenderShuffleSelectionPresenter shuffleSelection;

        [Header("+Time offer")]
        [SerializeField, Min(1f)] private float addTimeSeconds = 30f;
        [SerializeField, Min(1)] private int addTimeCoinCost = 900;

        private BartenderLevelController subscribedController;

        private float EffectiveAddTimeSeconds =>
            addTimeSeconds > 0f && !float.IsNaN(addTimeSeconds)
            && !float.IsInfinity(addTimeSeconds) ? addTimeSeconds : 30f;
        private int EffectiveAddTimeCoinCost => addTimeCoinCost > 0 ? addTimeCoinCost : 900;

        internal bool CanRequestUndo => CanCommand()
                                        && controller.CanPurchaseUndo(out _);
        internal bool CanRequestExtraGlass => CanCommand()
                                              && TryChooseExtraGlassType(out GlassType type)
                                              && controller.CanPurchaseExtraGlass(
                                                  type, out _);
        internal bool CanRequestAddTime => CanCommand()
                                           && controller.CanPurchaseTimeBoost(
                                               EffectiveAddTimeSeconds,
                                               EffectiveAddTimeCoinCost);
        internal bool CanRequestShuffle => IsShuffleSelectionActive
                                           || (CanCommand()
                                               && shuffleSelection != null
                                               && shuffleSelection.CanBegin(out _));
        internal bool IsShuffleSelectionActive =>
            shuffleSelection != null && shuffleSelection.Active;

        internal enum BoosterKind { Undo, ExtraGlass, Shuffle, AddTime }

        internal int CoinCostOf(BoosterKind kind) => kind switch
        {
            BoosterKind.Undo => BartenderProgressTuning.UndoBoosterCoinCost,
            BoosterKind.ExtraGlass => BartenderProgressTuning.ExtraGlassBoosterCoinCost,
            BoosterKind.Shuffle => BartenderProgressTuning.ShuffleBoosterCoinCost,
            _ => EffectiveAddTimeCoinCost,
        };

        internal bool CanOfferShop(BoosterKind kind)
        {
            if (!CanCommand() || BartenderProgressService.CanAfford(CoinCostOf(kind)))
                return false;

            return kind switch
            {
                BoosterKind.Undo => controller.CanUseUndo(out _),
                BoosterKind.ExtraGlass => TryChooseExtraGlassType(out GlassType type)
                                          && controller.CanUseExtraGlass(type, out _),
                BoosterKind.Shuffle => shuffleSelection != null
                                       && shuffleSelection.CanBegin(ignoreCoins: true, out _),
                BoosterKind.AddTime => controller.CanUseTimeBoost(
                    EffectiveAddTimeSeconds, EffectiveAddTimeCoinCost, out _),
                _ => false,
            };
        }

        private int presentationGeneration;

        private void Awake() => ResolveDependencies();

        private void OnEnable()
        {
            ResolveDependencies();
            BindTrayInput();
            Subscribe();
            Refresh();
        }

        private void OnDisable()
        {
            presentationGeneration++;
            shuffleSelection?.CancelSelection();
            Unsubscribe();
            if (trayInput != null) trayInput.enabled = false;
        }

        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "Level controller missing.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "Shelf view missing.";
                return false;
            }

            if (trayInput == null)
            {
                reason = "Booster input missing.";
                return false;
            }
            if (!trayInput.ValidateBindings(out reason)) return false;
            if (shuffleSelection == null)
            {
                reason = "Shuffle presenter missing.";
                return false;
            }
            if (!shuffleSelection.ValidateBindings(out reason)) return false;
            reason = null;
            return true;
        }

        [ContextMenu("Validate Booster Bindings")]
        private void ValidateFromContextMenu()
        {
            if (!ValidateBindings(out string reason))
                Debug.LogError("Booster bar binding error: " + reason, this);
        }

        private void BindTrayInput()
        {
            if (trayInput == null) return;
            trayInput.Bind(this);
            trayInput.enabled = true;
        }

        public async void RequestUndo()
        {
            if (!CanCommand()) return;
            trayInput?.ClearCommandFailure();
            BartenderLevelController owner = controller;
            BsRoundCommandStamp stamp = owner.CurrentRoundStamp;
            int generation = presentationGeneration;
            try
            {
                BartenderCommandResult<bool> result = pourInteraction != null
                    ? await pourInteraction.PurchaseAndAnimateUndoAsync()
                    : await owner.PurchaseUndoAsync();
                HandleCommandResult(BoosterKind.Undo, result.Succeeded, owner, stamp, generation);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                HandleCommandResult(BoosterKind.Undo, false, owner, stamp, generation);
            }
        }

        public async void RequestExtraGlass()
        {
            if (!CanCommand() || !TryChooseExtraGlassType(out GlassType type)) return;
            trayInput?.ClearCommandFailure();
            BartenderLevelController owner = controller;
            BsRoundCommandStamp stamp = owner.CurrentRoundStamp;
            int generation = presentationGeneration;
            try
            {
                BartenderCommandResult<int> result = await owner.PurchaseExtraGlassAsync(type);
                HandleCommandResult(BoosterKind.ExtraGlass, result.Succeeded, owner, stamp, generation);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                HandleCommandResult(BoosterKind.ExtraGlass, false, owner, stamp, generation);
            }
        }

        public bool TryChooseExtraGlassType(out GlassType type)
        {
            type = BartenderProgressTuning.PurchasedExtraGlassType;
            return shelfView != null && shelfView.HasFreeExtraShotSlot();
        }

        public async void RequestAddTime()
        {
            if (!CanCommand()) return;
            trayInput?.ClearCommandFailure();
            BartenderLevelController owner = controller;
            BsRoundCommandStamp stamp = owner.CurrentRoundStamp;
            int generation = presentationGeneration;
            try
            {
                BartenderCommandResult<bool> result = await owner.PurchaseTimeBoostAsync(
                    EffectiveAddTimeSeconds, EffectiveAddTimeCoinCost);
                HandleCommandResult(BoosterKind.AddTime, result.Succeeded, owner, stamp, generation);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                HandleCommandResult(BoosterKind.AddTime, false, owner, stamp, generation);
            }
        }

        private void HandleCommandResult(BoosterKind kind, bool succeeded,
            BartenderLevelController owner, BsRoundCommandStamp stamp, int generation)
        {
            if (!CanPresentCompletion(owner, stamp, generation)) return;
            if (!succeeded)
            {
                // Eligibility was checked before the request. A later failure must not open the shop.
                trayInput?.ShowCommandFailure(kind);
                return;
            }
            trayInput?.ClearCommandFailure();
            PlayAcceptedBoosterSound(kind == BoosterKind.Undo ? BsSfx.BoosterUndo : (BsSfx?)null);
        }

        private bool CanPresentCompletion(BartenderLevelController owner,
            BsRoundCommandStamp stamp, int generation)
        {
            if (this == null || !isActiveAndEnabled || presentationGeneration != generation
                || owner == null || owner.State != BartenderLevelState.Playing
                || !ReferenceEquals(controller, owner)) return false;
            BsRoundCommandStamp current = owner.CurrentRoundStamp;
            return stamp.IsValid && current.IsValid && stamp.AttemptId == current.AttemptId
                && stamp.Token == current.Token;
        }

        public void RequestShuffle()
        {
            if (IsShuffleSelectionActive)
            {
                trayInput?.ClearCommandFailure();
                shuffleSelection.CancelSelection();
                PlayAcceptedBoosterSound(BsSfx.ButtonClick);
                return;
            }
            if (!CanCommand()) return;
            if (shuffleSelection == null)
                return;
            if (shuffleSelection.TryBegin(out _))
            {
                trayInput?.ClearCommandFailure();
                PlayAcceptedBoosterSound(BsSfx.BoosterShuffle);
            }
        }

        private static void PlayAcceptedBoosterSound(BsSfx? sfx)
        {
            if (sfx.HasValue) BsAudio.Instance?.Play(sfx.Value, BoosterSoundVolume);
        }

        private const float BoosterSoundVolume = 0.85f;

        private bool CanCommand()
        {
            if (controller == null || controller.PersistencePending) return false;
            if (IsShuffleSelectionActive) return false;
            if (controller.State != BartenderLevelState.Playing) return false;
            if (controller.PresentationLocked) return false;
            if (pourInteraction != null
                && (pourInteraction.Busy || pourInteraction.BoardPresentationSettling)) return false;
            if (shelfView == null) return true;
            return shelfView.Ready && !shelfView.SeatAnimationPlaying
                   && !shelfView.SynchronizationDeferred;
        }

        public void Refresh()
        {
            BsLevel level = controller != null ? controller.CurrentLevel : null;
            trayInput?.SetAddTimeVisible(level != null && level.AllowTimedOrders);
        }

        private void ResolveDependencies()
        {
            if (shelfView == null) shelfView = GetComponent<BartenderShelfLevelView>();
            if (controller == null && shelfView != null) controller = shelfView.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (pourInteraction == null)
                pourInteraction = GetComponent<BartenderPourInteraction>();
        }

        private void Subscribe()
        {
            if (subscribedController == controller) return;
            Unsubscribe();
            subscribedController = controller;
            if (subscribedController == null) return;
            subscribedController.BoostersChanged += Refresh;
            subscribedController.LevelLoaded += HandleLevelLoaded;
            subscribedController.StateChanged += HandleStateChanged;
            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
        }

        private void Unsubscribe()
        {
            if (subscribedController != null)
            {
                subscribedController.BoostersChanged -= Refresh;
                subscribedController.LevelLoaded -= HandleLevelLoaded;
                subscribedController.StateChanged -= HandleStateChanged;
            }
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            subscribedController = null;
        }

        private void HandleLevelLoaded(BsLevel _)
        {
            trayInput?.ClearCommandFailure();
            Refresh();
        }

        private void HandleStateChanged(BartenderLevelState state)
        {
            if (state != BartenderLevelState.Playing) trayInput?.ClearCommandFailure();
            Refresh();
        }
        private void HandleCoinsChanged(int _) => Refresh();
    }
}
