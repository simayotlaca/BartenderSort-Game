using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Shows the four authored boosters: extra glass, undo, shuffle and time. It creates no visuals or
    /// economy HUD.
    /// </summary>
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
            shuffleSelection?.CancelSelection();
            Unsubscribe();
            if (trayInput != null) trayInput.enabled = false;
        }

        public bool ValidateBindings(out string reason)
        {
            if (controller == null)
            {
                reason = "BartenderLevelController Inspector referansı eksik.";
                return false;
            }
            if (shelfView == null)
            {
                reason = "BartenderShelfLevelView Inspector referansı eksik; booster "
                       + "sunum bariyerini okuyamaz.";
                return false;
            }

            if (trayInput == null)
            {
                reason = "Görünen dört booster düğmesinin authored input bağlantısı eksik.";
                return false;
            }
            if (!trayInput.ValidateBindings(out reason)) return false;
            if (shuffleSelection == null)
            {
                reason = "Scene-authored shuffle selection presenter bağlantısı eksik.";
                return false;
            }
            if (!shuffleSelection.ValidateBindings(out reason)) return false;
            reason = null;
            return true;
        }

        [ContextMenu("Validate Booster Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log("Booster şeridi: bağlantılar geçerli.", this);
            else
                Debug.LogError("Booster bar binding error: " + reason, this);
        }

        private void BindTrayInput()
        {
            if (trayInput == null) return;
            trayInput.Bind(this);
            trayInput.enabled = true;
        }

        public void RequestUndo()
        {
            if (!CanCommand()) return;
            bool accepted = pourInteraction != null
                ? pourInteraction.TryPurchaseAndAnimateUndo(out _)
                : controller.TryPurchaseUndo(out _);
            if (accepted)
                PlayAcceptedBoosterSound(BsSfx.BoosterUndo);
        }

        public void RequestExtraGlass()
        {
            if (!CanCommand()) return;
            if (!TryChooseExtraGlassType(out GlassType type))
                return;

            if (controller.TryPurchaseExtraGlass(type, out _, out _))
                PlayAcceptedBoosterSound(null);
        }

        /// <summary>The fixed-price glass booster adds a one-layer shot glass using one of three reserved slots.</summary>
        public bool TryChooseExtraGlassType(out GlassType type)
        {
            type = BartenderProgressTuning.PurchasedExtraGlassType;
            return shelfView != null && shelfView.HasFreeExtraShotSlot();
        }

        public void RequestAddTime()
        {
            if (!CanCommand()) return;
            if (controller.TryPurchaseTimeBoost(
                    EffectiveAddTimeSeconds, EffectiveAddTimeCoinCost,
                    out _))
                PlayAcceptedBoosterSound(null);
        }

        public void RequestShuffle()
        {
            if (IsShuffleSelectionActive)
            {
                // Leaving selection cancels shuffle. Use a normal click; play the shuffle sound only when
                // it starts.
                shuffleSelection.CancelSelection();
                PlayAcceptedBoosterSound(BsSfx.ButtonClick);
                return;
            }
            if (!CanCommand()) return;
            if (shuffleSelection == null)
                return;
            if (shuffleSelection.TryBegin(out _))
                PlayAcceptedBoosterSound(BsSfx.BoosterShuffle);
        }

        /// <summary>
        /// Plays an accepted booster's sound. Pass null for <paramref name="sfx"/> when GlassArrive or
        /// TimeBoost already plays from the view.
        /// </summary>
        private static void PlayAcceptedBoosterSound(BsSfx? sfx)
        {
            if (sfx.HasValue) BsAudio.Instance?.Play(sfx.Value, BoosterSoundVolume);
        }

        private const float BoosterSoundVolume = 0.85f;

        private bool CanCommand()
        {
            if (controller == null) return false;
            if (IsShuffleSelectionActive) return false;
            if (controller.State != BartenderLevelState.Playing) return false;
            if (controller.PresentationLocked) return false;
            if (pourInteraction != null && pourInteraction.Busy) return false;
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

        private void HandleLevelLoaded(BsLevel _) => Refresh();
        private void HandleStateChanged(BartenderLevelState _) => Refresh();
        private void HandleCoinsChanged(int _) => Refresh();
    }
}
