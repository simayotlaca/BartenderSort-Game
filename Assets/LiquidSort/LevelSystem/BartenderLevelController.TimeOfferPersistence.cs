using System;
using System.Threading.Tasks;
using BartenderSort.Core;
using UnityEngine;

namespace LiquidSort.Levels
{
    public sealed partial class BartenderLevelController
    {
        internal bool TimeOfferDecisionPending =>
            timeOfferMachine.State == BsTimeOfferState.Accepting
            || timeOfferMachine.State == BsTimeOfferState.DeclineOutboxCommitting;

        internal event Action TimeOfferDecisionResolved;

        public Task<BsTimeOfferAcceptResult> AcceptTimeOfferAsync(BsTimeOfferId expectedOfferId) =>
            ExecuteTimeOfferAcceptAsync(expectedOfferId, true);

        private async Task<BsTimeOfferAcceptResult> ExecuteTimeOfferAcceptAsync(
            BsTimeOfferId expectedOfferId, bool asynchronous)
        {
            if (!CanBeginTimeOfferDecision(expectedOfferId, out string decisionRejection))
                return BsTimeOfferAcceptResult.Reject(expectedOfferId, decisionRejection);
            if (!timeOfferMachine.Dispatch(BsTimeOfferTrigger.BeginAccept, expectedOfferId))
                return BsTimeOfferAcceptResult.Reject(
                    expectedOfferId, "The time offer is stale or no longer open");

            try
            {
                BsTimeOfferSnapshot offer = timeOfferContext;
                try
                {
                    BartenderCommandResult<bool> purchase = await ExecuteTimeBoostAsync(
                        offer.AddedSeconds, offer.CoinCost, true, asynchronous);
                    if (!purchase.Succeeded)
                    {
                        RequireTimeOfferTransition(BsTimeOfferTrigger.AcceptRejected, expectedOfferId);
                        return BsTimeOfferAcceptResult.RejectPurchase(
                            expectedOfferId, purchase.RejectionReason);
                    }
                }
                catch (Exception exception)
                {
                    if (timeOfferMachine.State == BsTimeOfferState.Accepting
                        && timeOfferMachine.CurrentId == expectedOfferId)
                        RequireTimeOfferTransition(BsTimeOfferTrigger.AcceptRejected, expectedOfferId);
                    Debug.LogException(exception, this);
                    return BsTimeOfferAcceptResult.RejectPurchase(
                        expectedOfferId, "The time offer purchase could not be completed");
                }

                RequireTimeOfferTransition(BsTimeOfferTrigger.AcceptCommitted, expectedOfferId);
                ReleasePresentationBarrier(timeOfferBarrierOwner);
                timeOfferContext = default;
                return BsTimeOfferAcceptResult.Accept(expectedOfferId);
            }
            finally
            {
                if (this != null) InvokeSafely(TimeOfferDecisionResolved);
            }
        }

        public Task<BsTimeOfferDeclineResult> DeclineTimeOfferAsync(
            BsTimeOfferId expectedOfferId,
            BsTimeOfferDeclineDisposition disposition = BsTimeOfferDeclineDisposition.PresentFailure) =>
            ExecuteTimeOfferDeclineAsync(expectedOfferId, disposition, true);

        private async Task<BsTimeOfferDeclineResult> ExecuteTimeOfferDeclineAsync(
            BsTimeOfferId expectedOfferId, BsTimeOfferDeclineDisposition disposition, bool asynchronous)
        {
            int dispositionValue = (int)disposition;
            if (dispositionValue < (int)BsTimeOfferDeclineDisposition.PresentFailure
                || dispositionValue > (int)BsTimeOfferDeclineDisposition.ReturnToMainMenu)
                return BsTimeOfferDeclineResult.Reject(
                    expectedOfferId, "The time-offer disposition is invalid");
            if (!CanBeginTimeOfferDecision(expectedOfferId, out string decisionRejection))
                return BsTimeOfferDeclineResult.Reject(expectedOfferId, decisionRejection);
            if (!timeOfferMachine.Dispatch(BsTimeOfferTrigger.BeginDecline, expectedOfferId))
                return BsTimeOfferDeclineResult.Reject(
                    expectedOfferId, "The time offer is stale or no longer open");

            try
            {
                timeOfferSettlementContext = new BsTimeOfferSettlementSnapshot(
                    expectedOfferId, disposition, BsTimeOfferSettlementStatus.SettlementPending);
                BsRoundTransitionCause cause = disposition
                    == BsTimeOfferDeclineDisposition.ReturnToMainMenu
                        ? BsRoundTransitionCause.TimeOfferDeclinedReturnToMenu
                        : BsRoundTransitionCause.TimeOfferDeclinedPresentFailure;
                try
                {
                    BartenderCommandResult<bool> completion = await ExecuteCompletionAsync(
                        BsRoundCompletion.Failed, cause, asynchronous);
                    if (!completion.Succeeded)
                    {
                        RejectTimeOfferDeclineOutbox(expectedOfferId);
                        return BsTimeOfferDeclineResult.Reject(
                            expectedOfferId, completion.RejectionReason);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    RejectTimeOfferDeclineOutbox(expectedOfferId);
                    return BsTimeOfferDeclineResult.Reject(
                        expectedOfferId, "The time-offer decision could not be saved");
                }

                RequireTimeOfferTransition(BsTimeOfferTrigger.DeclineOutboxCommitted, expectedOfferId);
                ReleasePresentationBarrier(timeOfferBarrierOwner);
                PublishTimeOfferSettlement();
                RetryRetainedSettlement();
                if (!IsTerminalSettlementCommitted)
                {
                    RequireTimeOfferTransition(BsTimeOfferTrigger.SettlementDeferred, expectedOfferId);
                    PublishTimeOfferSettlement();
                    return BsTimeOfferDeclineResult.Defer(
                        expectedOfferId, "The round result is queued for retry");
                }

                RequireTimeOfferTransition(BsTimeOfferTrigger.SettlementCommitted, expectedOfferId);
                timeOfferContext = default;
                MarkTimeOfferSettlementCommitted();
                return BsTimeOfferDeclineResult.Commit(expectedOfferId);
            }
            finally
            {
                if (this != null) InvokeSafely(TimeOfferDecisionResolved);
            }
        }
    }
}
