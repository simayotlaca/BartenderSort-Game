using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Connects round and presentation events to audio for both scenes and the portable prefab.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BartenderSession))]
    public sealed class BartenderAudioBridge : MonoBehaviour
    {
        private const float PourTargetPan = 0.055f;

        [Header("Authored rig references")]
        [Tooltip("Round flow owner on the authored gameplay rig.")]
        [SerializeField] private BartenderSession session;
        [Tooltip("Level authority used for load and delivery audio events.")]
        [SerializeField] private BartenderLevelController controller;
        [Tooltip("Authored pour input/presentation bridge.")]
        [SerializeField] private BartenderPourInteraction interaction;
        [Tooltip("Authored pour animator. Interaction.Animator remains authoritative "
               + "if the interaction is reconfigured at runtime.")]
        [SerializeField] private PourAnimator pourAnimator;

        private PourAnimator subscribedAnimator;
        private BsAudio audioService;
        private BsAudio.PourFlowLease pourFlowLease;
        private int flowOperationId;

        private void Awake()
        {
            ResolveDependencies();
            audioService = BsAudio.Instance;
        }

        private void OnEnable()
        {
            ResolveDependencies();
            audioService = BsAudio.Instance;
            Subscribe();
            if (controller != null && controller.CurrentLevel != null
                && controller.CurrentRoundState == BsRoundState.Playing)
                HandleLevelLoaded(controller.CurrentLevel);
        }

        private void OnDisable()
        {
            Unsubscribe();
            EndPourFlow();
        }

        private void Update()
        {
            RefreshAnimatorSubscription();
            TrackPourPhase();
        }

        private void ResolveDependencies()
        {
            if (session == null) session = GetComponent<BartenderSession>();
            if (controller == null && session != null) controller = session.Controller;
            if (controller == null) controller = GetComponent<BartenderLevelController>();
            if (interaction == null) interaction = GetComponent<BartenderPourInteraction>();
            if (pourAnimator == null && interaction != null)
                pourAnimator = interaction.Animator;
            if (pourAnimator == null) pourAnimator = GetComponent<PourAnimator>();
        }

        private void Subscribe()
        {
            if (controller != null)
            {
                controller.LevelLoaded -= HandleLevelLoaded;
                controller.LevelLoaded += HandleLevelLoaded;
                controller.Delivered -= HandleDelivered;
                controller.Delivered += HandleDelivered;
            }

            if (session != null)
            {
                session.FlowChanged -= HandleFlowChanged;
                session.FlowChanged += HandleFlowChanged;
                session.TerminalReady -= HandleTerminalReady;
                session.TerminalReady += HandleTerminalReady;
            }

            RefreshAnimatorSubscription();
        }

        private void Unsubscribe()
        {
            if (controller != null)
            {
                controller.LevelLoaded -= HandleLevelLoaded;
                controller.Delivered -= HandleDelivered;
            }

            if (session != null)
            {
                session.FlowChanged -= HandleFlowChanged;
                session.TerminalReady -= HandleTerminalReady;
            }

            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished -= HandlePourFinished;
            subscribedAnimator = null;
        }

        private void RefreshAnimatorSubscription()
        {
            PourAnimator wanted = interaction != null ? interaction.Animator : null;
            if (wanted == null) wanted = pourAnimator;
            if (wanted == null) wanted = GetComponent<PourAnimator>();
            if (subscribedAnimator == wanted) return;

            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished -= HandlePourFinished;
            EndPourFlow();
            subscribedAnimator = wanted;
            if (subscribedAnimator != null)
                subscribedAnimator.PourFinished += HandlePourFinished;
        }

        private void TrackPourPhase()
        {
            if (subscribedAnimator == null)
            {
                EndPourFlow();
                return;
            }

            int operationId = subscribedAnimator.ActiveOperationId;
            PourPhase phase = subscribedAnimator.Phase;
            if (phase == PourPhase.Flow && operationId != 0)
            {
                if (flowOperationId != operationId) BeginPourFlow(operationId);
                return;
            }

            if (flowOperationId == 0) return;
            EndPourFlow();
        }

        private void BeginPourFlow(int operationId)
        {
            EndPourFlow();
            flowOperationId = operationId;
            float targetPan = subscribedAnimator != null
                ? subscribedAnimator.ActiveHorizontalDirection * PourTargetPan
                : 0f;
            int amount = subscribedAnimator != null
                ? subscribedAnimator.ActiveAmount
                : 0;
            if (audioService != null
                && audioService.TryAcquirePourFlow(amount, 1f, targetPan,
                                            out BsAudio.PourFlowLease flowLease))
            {
                pourFlowLease = flowLease;
            }

            // BsAudio picks the nearest available weight clip. Stay silent here only when all five are
            // missing.
        }

        // Each weight clip already includes its fade-out, so finishing or cancelling adds no extra sound.
        private void EndPourFlow()
        {
            pourFlowLease?.Dispose();
            pourFlowLease = null;
            flowOperationId = 0;
        }

        private void HandlePourFinished(int operationId, PourOutcome _)
        {
            if (flowOperationId == operationId) EndPourFlow();
        }

        private void HandleLevelLoaded(BsLevel level)
        {
            EndPourFlow();
            audioService?.InvalidatePourFlow();
            // Timed levels get their own bed; both are 108 BPM in B minor, so the change reads as one
            // score. Each track keeps its own resume position.
            audioService?.StartBgm(HasTimedOrders(level)
                ? BsBgm.GameplayRush
                : BsBgm.Gameplay);
            audioService?.RestoreBgmAfterResult();
        }

        // The level flag alone is not enough: the board only creates deadlines when a flagged level also
        // carries an order with a time limit.
        private static bool HasTimedOrders(BsLevel level)
        {
            if (level == null || !level.AllowTimedOrders || level.Orders == null)
                return false;

            for (int i = 0; i < level.Orders.Count; i++)
            {
                OrderDef order = level.Orders[i];
                if (order != null && order.TimeLimit > 0f) return true;
            }
            return false;
        }

        private void HandleDelivered(BartenderDeliveryReceipt receipt)
        {
            if (!IsCurrentDeliveryReceipt(receipt)) return;
            audioService?.Play(BsSfx.DeliverSlide);
        }

        private void HandleTerminalReady(
            BartenderTerminalPresentationReceipt receipt)
        {
            if (session == null
                || !session.IsTerminalPresentationCurrent(receipt)) return;
            audioService?.PlayResult(receipt.Outcome == BsRoundOutcome.Won
                ? BsSfx.Win
                : BsSfx.Fail);
        }

        private bool IsCurrentDeliveryReceipt(BartenderDeliveryReceipt receipt)
        {
            return controller != null && receipt != null
                   && receipt.AttemptId.IsValid
                   && receipt.OperationId.IsValid
                   && receipt.DomainRevision > 0L
                   && receipt.BoardRevision >= 0
                   && receipt.Cause == BsRoundTransitionCause.PlayerDelivery
                   && controller.CurrentRoundStamp == new BsRoundCommandStamp(
                       receipt.AttemptId,
                       receipt.Token,
                       receipt.DomainRevision,
                       receipt.BoardRevision);
        }

        private void HandleFlowChanged(BsRoundTransition transition)
        {
            if (transition == null) return;
            if (transition.To == BsRoundState.Paused)
            {
                audioService?.PausePourFlow();
                audioService?.SetBgmPaused(true);
            }
            else if (transition.From == BsRoundState.Paused)
            {
                // Restore music on every exit from pause, including the return to menu.
                if (transition.To == BsRoundState.Playing)
                    audioService?.ResumePourFlow();
                audioService?.SetBgmPaused(false);
            }

            if (transition.To == BsRoundState.Empty)
                audioService?.StopBgm();
        }
    }
}
