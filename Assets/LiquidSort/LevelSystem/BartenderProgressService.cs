using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    internal readonly struct BartenderSaveResult
    {
        internal readonly bool Succeeded;
        internal readonly string RejectionReason;
        internal readonly BsSettlementReceipt Receipt;

        internal BartenderSaveResult(bool succeeded, string rejectionReason,
                                     BsSettlementReceipt receipt = default)
        {
            Succeeded = succeeded;
            RejectionReason = rejectionReason;
            Receipt = succeeded ? receipt : default;
        }
    }

    internal enum BartenderSettlementKind
    {
        Won = 1,
        Failed = 2,
        Abandoned = 3,
    }

    public static class BartenderProgressService
    {
        public const int DefaultStartingCoins = BartenderProgressTuning.StartingCoins;
        public const int MaxLives = BartenderProgressTuning.MaximumLives;
        public const int WinCoinReward = BartenderProgressTuning.CoinsPerWin;
        public const int FailureContinueCoinCost =
            BartenderProgressTuning.PaidContinueCoinCost;
        public const int FullLifeRefillCoinCost =
            BartenderProgressTuning.FullLifeRefillCoinCost;
        public static readonly TimeSpan LifeRegenerationInterval = TimeSpan.FromMinutes(10d);

        private const int CurrentVersion = 6;
        private const int ActiveRoundVersion = 2;
        private const int SettlementHistoryLimit = 64;
        private const int DailyActivityFenceLimit = 256;
        private static readonly long RefreshRetryIntervalTicks = TimeSpan.FromSeconds(5d).Ticks;
        private const string ProductionSaveFileName = "bartender_progress_v1.json";
        private const string EditorTestSaveFilePrefix =
            "bartender_progress_editor_test_v";
        private const string LegacyCoinsKey = "LiquidSort.Bartender.Coins";
        private const string LegacyProgressKey =
            "LiquidSort.Bartender.NextLevelSlot";

        [Serializable]
        private sealed class SettlementRecord
        {
            public string AttemptId;
            public int Kind;
            public int CampaignSlot;
            public int NextUnlockedOnWin = -1;
            public long OperationId;
            public long Revision;
            public int BoardRevision;
            public int Cause;
            public long TimeOfferId;
            public int TimeOfferDisposition;
            public string PersistenceReceiptId = string.Empty;
        }

        [Serializable]
        private sealed class SettlementOutboxRecord
        {
            public int State;
            public long OperationId;
            public string AttemptId = string.Empty;
            public int CampaignSlot = -1;
            public long Revision;
            public int BoardRevision;
            public int Completion;
            public int Cause;
            public int NextUnlockedOnWin = -1;
            public long TimeOfferId;
            public int TimeOfferDisposition;
            public string PersistenceReceiptId = string.Empty;
            public string ActiveRoundJson = string.Empty;
        }

        [Serializable]
        private sealed class DailyOrdersRecord
        {
            public long UtcDayKey;
            public int DeliveredOrders;
            public int WonLevels;
            public int ServedUnits;
            public bool RewardClaimed;
        }

        [Serializable]
        private sealed class DailyActivityFenceRecord
        {
            public string AttemptId = string.Empty;
            public long OperationId;
            public long DomainRevision;
            public int BoardRevision;
            public int Kind;
            public int Amount;
            public string RecipeKey = string.Empty;
            public int OrderIndex = -1;
        }

        [Serializable]
        private sealed class ActiveRoundHeader
        {
            public int Version;
            public string LevelSignature;
            public string AttemptId;
            public long DomainRevision;
            public long LastOperationId;
            public int BoardRevision;
        }

        [Serializable]
        private sealed class ProgressFileHeader
        {
            public int Version;
            public string ActiveAttemptId;
            public string ActiveRoundJson;
            public List<SettlementRecord> Settlements;
        }

        [Serializable]
        private sealed class LegacyProgressData
        {
            public int Version;
            public int Coins;
            public int Lives;
            public int NextUnlockedCampaignSlot;
            public long NextLifeUtcTicks;
            public string ActiveAttemptId;
            public int ActiveAttemptCampaignSlot = -1;
            public List<LegacySettlementRecord> Settlements;
        }

        [Serializable]
        private sealed class LegacySettlementRecord
        {
            public string AttemptId;
            public int Kind;
            public int CampaignSlot;
            public int NextUnlockedOnWin = -1;
        }

        [Serializable]
        private sealed class ProgressData
        {
            public int Version = CurrentVersion;
            public int Coins = DefaultStartingCoins;
            public int Lives = MaxLives;
            public int NextUnlockedCampaignSlot;
            public int SelectedReplayCampaignSlot = -1;
            public int ReplayCursor;
            public long DailyClockUtcTicks;
            public long DailyClockDeviceUtcTicks;
            public long NextLifeUtcTicks;
            public string ActiveAttemptId = string.Empty;
            public int ActiveAttemptCampaignSlot = -1;
            public string ActiveRoundJson = string.Empty;
            public List<SettlementRecord> Settlements = new List<SettlementRecord>();
            public long LastSettlementOperationId;
            public SettlementOutboxRecord SettlementOutbox;
            public DailyOrdersRecord DailyOrders = new DailyOrdersRecord();
            public List<DailyActivityFenceRecord> DailyActivityFences =
                new List<DailyActivityFenceRecord>();
            public List<string> UnlockedRecipes = new List<string>();
        }

        private static ProgressData data;
        private static bool loaded;
        private static bool mutationInProgress;
        private static long lastPublishedTimerSeconds = long.MinValue;
        private static long nextRefreshPersistenceRetryUtcTicks;
        private static bool persistenceDirty;
        private static bool progressLoadPending;
        private static bool interruptedAttemptSettlementPending;
        private static string pendingInterruptedAttemptId = string.Empty;
        private static int pendingInterruptedCampaignSlot = -1;
        private static PendingWrite pendingWrite;
        private static Task<BartenderSaveResult> writeCompletion;
        private static bool clockCheckpointPending;

        private sealed class PreparedWrite
        {
            internal ProgressData Next;
            internal BsSettlementReceipt Receipt;
            internal string RejectionReason;
            internal Exception Exception;
            internal bool Succeeded;
            internal bool ProgressChanged;
        }

        private sealed class PendingWrite
        {
            internal Task<PreparedWrite> Worker;
            internal Action<BartenderSaveResult> BeforePublish;
            internal readonly TaskCompletionSource<BartenderSaveResult> Completion =
                new TaskCompletionSource<BartenderSaveResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal static bool HasPendingWrite => pendingWrite != null;

        internal static Task<BartenderSaveResult> PendingWriteCompletion => writeCompletion;

        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                return !progressLoadPending;
            }
        }

        public static int Coins
        {
            get
            {
                EnsureLoaded();
                return data.Coins;
            }
        }

        public static int Lives
        {
            get
            {
                Refresh();
                return data.Lives;
            }
        }

        public static bool IsLifeFull => Lives >= MaxLives;

        public static int NextUnlockedCampaignSlot
        {
            get
            {
                EnsureLoaded();
                return data.NextUnlockedCampaignSlot;
            }
        }

        public static TimeSpan LifeTimer
        {
            get
            {
                Refresh();
                return RemainingLifeTime(data, DateTime.UtcNow.Ticks);
            }
        }

        public static BartenderDailyOrdersSnapshot DailyOrders
        {
            get
            {
                Refresh();
                return CaptureDailyOrders(data);
            }
        }

        public static long DailyUtcNowTicks
        {
            get { EnsureLoaded(); return BartenderDailyClock.UtcNowTicks; }
        }

        internal static int SelectedReplayCampaignSlot
        {
            get { EnsureLoaded(); return data.SelectedReplayCampaignSlot; }
        }

        internal static int ReplayCursor
        {
            get { EnsureLoaded(); return data.ReplayCursor; }
        }

        internal static bool TryGetResumableAttempt(out string attemptId,
                                                    out int campaignSlot) =>
            TryGetResumableAttempt(
                out attemptId, out campaignSlot, out _);

        internal static bool TryGetResumableAttempt(
            out string attemptId,
            out int campaignSlot,
            out string activeRoundJson)
        {
            EnsureLoaded();
            if (HasActiveAttempt(data) && IsValidActiveRoundJson(data.ActiveRoundJson))
            {
                attemptId = data.ActiveAttemptId;
                campaignSlot = data.ActiveAttemptCampaignSlot;
                activeRoundJson = data.ActiveRoundJson;
                return true;
            }
            attemptId = null;
            campaignSlot = -1;
            activeRoundJson = null;
            return false;
        }

        public static event Action<int> CoinsChanged;
        public static event Action<int> LivesChanged;
        public static event Action<TimeSpan> LifeTimerChanged;
        public static event Action<int> ProgressChanged;
        public static event Action<BartenderDailyOrdersSnapshot> DailyOrdersChanged;

        public static bool CanAfford(int cost) => cost > 0 && Coins >= cost;

        public static bool TryClaimDailyOrdersReward(out string rejectionReason) =>
            TryClaimDailyOrdersReward(DailyOrders.UtcDayKey, out rejectionReason);

        public static bool TryClaimDailyOrdersReward(long expectedDayKey, out string rejectionReason)
        {
            Refresh(false);
            if (!CanMutate(out rejectionReason)) return false;
            return CommitPreparedSynchronously(PrepareDailyClaim(data, expectedDayKey,
                BartenderDailyClock.UtcNowTicks, DateTime.UtcNow.Ticks), out rejectionReason);
        }

        internal static Task<BartenderSaveResult> ClaimDailyOrdersRewardAsync(long expectedDayKey) =>
            BeginAsyncWrite((source, dailyTicks, deviceTicks) =>
                PrepareDailyClaim(source, expectedDayKey, dailyTicks, deviceTicks));

        private static PreparedWrite PrepareDailyClaim(ProgressData source, long expectedDayKey,
            long dailyTicks, long deviceTicks)
        {
            ProgressData next = CloneForCurrentClocks(source, dailyTicks, deviceTicks);
            BartenderDailyOrdersSnapshot daily = CaptureDailyOrders(next);
            if (daily.UtcDayKey != expectedDayKey)
                return RejectWrite("Daily orders have renewed; check today's tasks");
            if (daily.RewardClaimed) return RejectWrite("The daily reward was already claimed");
            if (!daily.IsComplete) return RejectWrite("Complete all daily orders first");
            CreditCoinsSaturating(next, BartenderDailyOrdersTuning.TotalRewardCoins);
            next.DailyOrders.RewardClaimed = true;
            return new PreparedWrite { Succeeded = true, Next = next };
        }

        public static bool TrySpendCoins(int cost, out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (cost <= 0)
            {
                rejectionReason = "Invalid booster price";
                return false;
            }
            if (data.Coins < cost)
            {
                rejectionReason = $"Not enough coins: {data.Coins}/{cost}";
                return false;
            }

            ProgressData next = Clone(data);
            next.Coins -= cost;
            return Commit(next, true, false, false, out rejectionReason);
        }

        internal static Task<BartenderSaveResult> RefillLivesToMaximumAsync(int coinCost) =>
            BeginAsyncWrite((source, dailyTicks, deviceTicks) =>
                PrepareLifeRefill(source, coinCost, dailyTicks, deviceTicks));

        private static PreparedWrite PrepareLifeRefill(ProgressData source, int coinCost,
            long dailyTicks, long deviceTicks)
        {
            ProgressData next = CloneForCurrentClocks(source, dailyTicks, deviceTicks);
            if (coinCost <= 0) return RejectWrite("Invalid life refill cost");
            if (!string.IsNullOrEmpty(next.ActiveAttemptId))
                return RejectWrite("Lives cannot be refilled during an active round");
            if (next.Lives >= MaxLives) return RejectWrite("Lives are already full");
            if (next.Coins < coinCost)
                return RejectWrite($"Not enough coins: {next.Coins}/{coinCost}");
            next.Coins -= coinCost;
            next.Lives = MaxLives;
            next.NextLifeUtcTicks = 0L;
            return new PreparedWrite { Succeeded = true, Next = next };
        }

        internal static bool TryPurchaseLifeAndBeginAttempt(
            int campaignSlot, int coinCost, string initialRoundJson,
            out string attemptId, out string activeRoundJson,
            out string rejectionReason) =>
            TryPurchaseLifeAndBeginAttempt(
                campaignSlot, coinCost, null, initialRoundJson,
                out attemptId, out activeRoundJson, out rejectionReason);

        internal static bool TryPurchaseLifeAndBeginAttempt(
            int campaignSlot, int coinCost, BsAttemptId requestedAttemptId,
            string initialRoundJson, out string attemptId,
            out string activeRoundJson, out string rejectionReason)
        {
            if (!requestedAttemptId.IsValid)
            {
                attemptId = null;
                activeRoundJson = null;
                rejectionReason = "The requested attempt id is invalid";
                return false;
            }
            return TryPurchaseLifeAndBeginAttempt(
                campaignSlot, coinCost, (BsAttemptId?)requestedAttemptId,
                initialRoundJson,
                out attemptId, out activeRoundJson, out rejectionReason);
        }

        private static bool TryPurchaseLifeAndBeginAttempt(
            int campaignSlot, int coinCost, BsAttemptId? requestedAttemptId,
            string initialRoundJson, out string attemptId,
            out string activeRoundJson, out string rejectionReason)
        {
            Refresh(false);
            attemptId = null;
            activeRoundJson = null;
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (campaignSlot < 0)
            {
                rejectionReason = "Invalid level identifier";
                return false;
            }
            if (coinCost <= 0)
            {
                rejectionReason = "Invalid continue cost";
                return false;
            }
            if (!string.IsNullOrEmpty(data.ActiveAttemptId))
            {
                rejectionReason = "Another active round is waiting to be settled";
                return false;
            }
            if (HasOwnedSettlementOutbox(data.SettlementOutbox))
            {
                rejectionReason = "A terminal settlement is waiting to be resolved";
                return false;
            }
            if (data.Lives >= MaxLives)
            {
                rejectionReason = "Lives are already full";
                return false;
            }
            if (data.Coins < coinCost)
            {
                rejectionReason = $"Not enough coins: {data.Coins}/{coinCost}";
                return false;
            }

            string nextAttemptId = requestedAttemptId.HasValue
                ? requestedAttemptId.Value.Value
                : Guid.NewGuid().ToString("N");
            if (FindSettlement(data, nextAttemptId) != null)
            {
                rejectionReason = "The requested attempt id was already settled";
                return false;
            }

            if (!string.IsNullOrEmpty(initialRoundJson)
                && !IsValidActiveRoundJson(initialRoundJson))
            {
                rejectionReason = "The initial round snapshot is invalid";
                return false;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            ProgressData next = Clone(data);
            next.Coins -= coinCost;
            next.Lives++;
            next.NextLifeUtcTicks = next.Lives >= MaxLives
                ? 0L
                : next.NextLifeUtcTicks > nowTicks
                    ? next.NextLifeUtcTicks
                    : SafeAddTicks(nowTicks, LifeRegenerationInterval.Ticks);
            next.ActiveAttemptId = nextAttemptId;
            next.ActiveAttemptCampaignSlot = campaignSlot;
            next.ActiveRoundJson = initialRoundJson;
            next.SelectedReplayCampaignSlot = -1;
            if (!Commit(next, true, true, false, out rejectionReason)) return false;
            attemptId = data.ActiveAttemptId;
            activeRoundJson = data.ActiveRoundJson;
            return true;
        }

        internal static bool TryBeginAttempt(
            int campaignSlot, string initialRoundJson, out string attemptId,
            out string activeRoundJson, out string rejectionReason) =>
            TryBeginAttempt(
                campaignSlot, null, initialRoundJson,
                out attemptId, out activeRoundJson, out rejectionReason);

        internal static bool TryBeginAttempt(
            int campaignSlot, BsAttemptId requestedAttemptId,
            string initialRoundJson, out string attemptId,
            out string activeRoundJson, out string rejectionReason)
        {
            if (!requestedAttemptId.IsValid)
            {
                attemptId = null;
                activeRoundJson = null;
                rejectionReason = "The requested attempt id is invalid";
                return false;
            }
            return TryBeginAttempt(
                campaignSlot, (BsAttemptId?)requestedAttemptId, initialRoundJson,
                out attemptId, out activeRoundJson, out rejectionReason);
        }

        private static bool TryBeginAttempt(
            int campaignSlot, BsAttemptId? requestedAttemptId,
            string initialRoundJson, out string attemptId,
            out string activeRoundJson, out string rejectionReason)
        {
            Refresh(false);
            attemptId = null;
            activeRoundJson = null;
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (campaignSlot < 0)
            {
                rejectionReason = "Invalid level identifier";
                return false;
            }
            if (!string.IsNullOrEmpty(data.ActiveAttemptId)
                && data.ActiveAttemptCampaignSlot == campaignSlot)
            {
                if (string.IsNullOrWhiteSpace(data.ActiveRoundJson)
                    && IsValidActiveRoundJson(initialRoundJson))
                {
                    ProgressData attached = Clone(data);
                    attached.ActiveRoundJson = initialRoundJson;
                    if (!Commit(attached, false, false, false,
                            out rejectionReason))
                        return false;
                }
                attemptId = data.ActiveAttemptId;
                activeRoundJson = data.ActiveRoundJson;
                return true;
            }
            if (HasOwnedSettlementOutbox(data.SettlementOutbox))
            {
                rejectionReason = "A terminal settlement is waiting to be resolved";
                return false;
            }
            if (data.Lives <= 0)
            {
                rejectionReason = "Wait for a life to refill";
                return false;
            }
            if (!string.IsNullOrEmpty(data.ActiveAttemptId))
            {
                rejectionReason = "Another active round is waiting to be settled";
                return false;
            }

            string nextAttemptId = requestedAttemptId.HasValue
                ? requestedAttemptId.Value.Value
                : Guid.NewGuid().ToString("N");
            if (FindSettlement(data, nextAttemptId) != null)
            {
                rejectionReason = "The requested attempt id was already settled";
                return false;
            }

            if (!string.IsNullOrEmpty(initialRoundJson)
                && !IsValidActiveRoundJson(initialRoundJson))
            {
                rejectionReason = "The initial round snapshot is invalid";
                return false;
            }

            ProgressData next = Clone(data);
            next.ActiveAttemptId = nextAttemptId;
            next.ActiveAttemptCampaignSlot = campaignSlot;
            next.ActiveRoundJson = initialRoundJson ?? string.Empty;
            next.SelectedReplayCampaignSlot = -1;
            if (!Commit(next, false, false, false, out rejectionReason)) return false;
            attemptId = data.ActiveAttemptId;
            activeRoundJson = data.ActiveRoundJson;
            return true;
        }

        internal static bool TryCommitActiveRound(
            string attemptId, int campaignSlot, string activeRoundJson, int coinCost,
            out string rejectionReason) =>
            TryCommitActiveRound(
                attemptId, campaignSlot, activeRoundJson, coinCost, default,
                out rejectionReason);

        internal static bool TryCommitActiveRound(
            string attemptId, int campaignSlot, string activeRoundJson, int coinCost,
            BartenderDailyActivityReceipt dailyActivity,
            out string rejectionReason)
        {
            EnsureLoaded();
            if (!CanMutate(out rejectionReason)) return false;
            if (!TryPrepareActiveRound(data, attemptId, campaignSlot,
                    activeRoundJson, coinCost, dailyActivity,
                    BartenderDailyClock.UtcNowTicks, out ProgressData next,
                    out rejectionReason)) return false;
            return Commit(next, coinCost > 0, false, false, out rejectionReason);
        }

        internal static Task<BartenderSaveResult> CommitActiveRoundAsync(
            string attemptId, int campaignSlot, Func<string> serializeActiveRound,
            int coinCost, BartenderDailyActivityReceipt activity = default,
            Action<BartenderSaveResult> beforePublish = null) =>
            BeginAsyncWrite((source, dailyUtcTicks, _) =>
            {
                if (serializeActiveRound == null)
                    return new PreparedWrite { RejectionReason = "The round serializer is missing" };
                string json = serializeActiveRound();
                bool prepared = TryPrepareActiveRound(source, attemptId, campaignSlot,
                    json, coinCost, activity, dailyUtcTicks,
                    out ProgressData next, out string rejectionReason);
                return new PreparedWrite
                {
                    Succeeded = prepared,
                    Next = prepared ? next : null,
                    RejectionReason = rejectionReason,
                };
            }, beforePublish);

        private static bool TryPrepareActiveRound(
            ProgressData source, string attemptId, int campaignSlot,
            string activeRoundJson, int coinCost,
            BartenderDailyActivityReceipt dailyActivity, long dailyUtcTicks,
            out ProgressData next, out string rejectionReason)
        {
            next = null;
            rejectionReason = null;
            if (string.IsNullOrWhiteSpace(attemptId) || campaignSlot < 0
                || !TryReadActiveRoundHeader(activeRoundJson, out ActiveRoundHeader roundHeader))
            {
                rejectionReason = "The active round snapshot is invalid";
                return false;
            }
            if (!string.Equals(source.ActiveAttemptId, attemptId,
                    StringComparison.Ordinal)
                || source.ActiveAttemptCampaignSlot != campaignSlot)
            {
                rejectionReason = "The round is no longer active";
                return false;
            }
            if (coinCost < 0)
            {
                rejectionReason = "Invalid booster price";
                return false;
            }
            if (source.Coins < coinCost)
            {
                rejectionReason = $"Not enough coins: {source.Coins}/{coinCost}";
                return false;
            }
            if (!TryValidateDailyActivity(
                    roundHeader, dailyActivity, out rejectionReason))
                return false;

            next = Clone(source);
            ReconcileDailyOrders(next, dailyUtcTicks);
            if (!TryApplyDailyActivity(
                    next, dailyActivity, source.ActiveRoundJson,
                    out rejectionReason))
                return false;
            next.ActiveRoundJson = activeRoundJson;
            next.Coins -= coinCost;
            return true;
        }

        internal static long SettlementOperationFloor
        {
            get
            {
                EnsureLoaded();
                return Math.Max(0L, data.LastSettlementOperationId);
            }
        }

        internal static bool TryStageSettlement(
            BsSettlementDraft draft,
            string activeRoundJson,
            int coinCost,
            out BsSettlementReceipt receipt,
            out string rejectionReason) =>
            TryStageSettlement(
                draft, activeRoundJson, coinCost, default,
                out receipt, out rejectionReason);

        internal static bool TryStageSettlement(
            BsSettlementDraft draft,
            string activeRoundJson,
            int coinCost,
            BartenderDailyActivityReceipt dailyActivity,
            out BsSettlementReceipt receipt,
            out string rejectionReason)
        {
            EnsureLoaded();
            receipt = default;
            if (!CanMutate(out rejectionReason)) return false;
            if (!TryPrepareSettlementStage(data, draft, activeRoundJson,
                    coinCost, dailyActivity, BartenderDailyClock.UtcNowTicks,
                    out ProgressData next, out receipt, out rejectionReason)) return false;
            if (next == null || Commit(next, coinCost > 0, false, false,
                    out rejectionReason)) return true;
            receipt = default;
            return false;
        }

        internal static Task<BartenderSaveResult> StageSettlementAsync(
            BsSettlementDraft draft, Func<string> serializeActiveRound,
            int coinCost, BartenderDailyActivityReceipt activity = default,
            Action<BartenderSaveResult> beforePublish = null) =>
            BeginAsyncWrite((source, dailyUtcTicks, _) =>
            {
                if (serializeActiveRound == null)
                    return new PreparedWrite { RejectionReason = "The round serializer is missing" };
                string json = serializeActiveRound();
                bool prepared = TryPrepareSettlementStage(source, draft, json,
                    coinCost, activity, dailyUtcTicks, out ProgressData next,
                    out BsSettlementReceipt receipt, out string rejectionReason);
                return new PreparedWrite
                {
                    Succeeded = prepared,
                    Next = prepared ? next : null,
                    Receipt = receipt,
                    RejectionReason = rejectionReason,
                };
            }, beforePublish);

        private static bool TryPrepareSettlementStage(
            ProgressData source, BsSettlementDraft draft, string activeRoundJson,
            int coinCost, BartenderDailyActivityReceipt dailyActivity, long dailyUtcTicks,
            out ProgressData next, out BsSettlementReceipt receipt,
            out string rejectionReason)
        {
            next = null;
            receipt = default;
            rejectionReason = null;
            if (!draft.IsValid)
            {
                rejectionReason = "The settlement request is invalid";
                return false;
            }
            if (dailyActivity.IsValid
                && (dailyActivity.AttemptId != draft.AttemptId
                    || dailyActivity.OperationId != draft.OperationId
                    || dailyActivity.DomainRevision != draft.Revision
                    || dailyActivity.BoardRevision != draft.BoardRevision))
            {
                rejectionReason = "The daily activity does not match the settlement";
                return false;
            }
            if (!TryValidateDailyActivity(
                    activeRoundJson, dailyActivity, out ActiveRoundHeader roundHeader,
                    out rejectionReason))
                return false;

            if (HasOwnedSettlementOutbox(source.SettlementOutbox))
            {
                if (!TryDecodeSettlementOutbox(source, out BsSettlementOutboxSnapshot owned,
                        out _, out rejectionReason)
                    || owned.Draft != draft
                    || !string.Equals(owned.ActiveRoundJson, activeRoundJson,
                        StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(rejectionReason))
                        rejectionReason = "Another settlement operation owns the outbox";
                    return false;
                }

                receipt = owned.Receipt;
                return true;
            }

            if (!string.Equals(source.ActiveAttemptId, draft.AttemptId.Value,
                    StringComparison.Ordinal)
                || source.ActiveAttemptCampaignSlot != draft.CampaignSlot)
            {
                rejectionReason = "The settlement attempt is no longer active";
                return false;
            }
            if (coinCost < 0)
            {
                rejectionReason = "Invalid settlement coin cost";
                return false;
            }
            if (source.Coins < coinCost)
            {
                rejectionReason = $"Not enough coins: {source.Coins}/{coinCost}";
                return false;
            }
            if ((roundHeader == null && !TryReadActiveRoundHeader(activeRoundJson,
                    out roundHeader))
                || !string.Equals(roundHeader.AttemptId, draft.AttemptId.Value,
                    StringComparison.Ordinal)
                || roundHeader.DomainRevision != draft.Revision
                || roundHeader.LastOperationId != draft.OperationId.Value
                || roundHeader.BoardRevision != draft.BoardRevision)
            {
                rejectionReason = "The settlement board snapshot is stale or invalid";
                return false;
            }

            var machine = new BsSettlementOutboxStateMachine();
            var empty = new BsSettlementOutboxSnapshot(
                BsSettlementOutboxState.Empty,
                default,
                default,
                Math.Max(0L, source.LastSettlementOperationId));
            receipt = BsSettlementReceipt.Durable(
                draft.Request, BuildSettlementPersistenceReceiptId(draft.Request));
            if (!machine.TryRestore(empty, BsSettlementRestoreEvidence.None)
                || !machine.TryStage(draft, receipt, activeRoundJson))
            {
                receipt = default;
                rejectionReason = "The settlement operation id is stale";
                return false;
            }

            next = Clone(source);
            ReconcileDailyOrders(next, dailyUtcTicks);
            if (!TryApplyDailyActivity(
                    next, dailyActivity, source.ActiveRoundJson,
                    out rejectionReason))
            {
                receipt = default;
                return false;
            }
            next.ActiveRoundJson = activeRoundJson;
            next.Coins -= coinCost;
            next.LastSettlementOperationId = machine.LastObservedOperationId;
            next.SettlementOutbox = CaptureSettlementOutbox(machine.Capture());
            return true;
        }

        internal static bool TryCommitSettlement(
            BsSettlementReceipt expected,
            out string rejectionReason)
        {
            EnsureLoaded();
            if (!CanMutate(out rejectionReason)) return false;
            if (!TryPrepareSettlementCommit(data, expected, BartenderDailyClock.UtcNowTicks,
                    DateTime.UtcNow.Ticks, out ProgressData next,
                    out rejectionReason)) return false;
            if (next == null || Commit(next, next.Coins != data.Coins,
                    next.Lives != data.Lives,
                    next.NextUnlockedCampaignSlot != data.NextUnlockedCampaignSlot,
                    out rejectionReason)) return true;

            // Pending remains authoritative on a failed write; the same durable receipt can retry.
            string commitFailure = rejectionReason;
            TryPersistSettlementCommitRetry(expected);
            rejectionReason = commitFailure;
            return false;
        }

        internal static Task<BartenderSaveResult> CommitSettlementAsync(
            BsSettlementReceipt expected) =>
            BeginAsyncWrite((source, dailyUtcTicks, nowTicks) =>
            {
                bool prepared = TryPrepareSettlementCommit(source, expected,
                    dailyUtcTicks, nowTicks, out ProgressData next,
                    out string rejectionReason);
                return new PreparedWrite
                {
                    Succeeded = prepared,
                    Next = prepared ? next : null,
                    Receipt = expected,
                    RejectionReason = rejectionReason,
                };
            });

        private static bool TryPrepareSettlementCommit(
            ProgressData source, BsSettlementReceipt expected, long dailyUtcTicks,
            long nowTicks, out ProgressData next, out string rejectionReason)
        {
            next = null;
            rejectionReason = null;
            if (!expected.IsValid)
            {
                rejectionReason = "The settlement receipt is invalid";
                return false;
            }

            SettlementRecord sameAttempt = FindSettlement(
                source, expected.Request.AttemptId.Value);
            SettlementRecord receiptMatch = FindSettlementReceiptMatch(
                source, expected);
            if (!HasOwnedSettlementOutbox(source.SettlementOutbox))
            {
                if (receiptMatch != null) return true;
                rejectionReason = sameAttempt != null
                    ? "The round receipt was settled by another operation"
                    : "The settlement outbox is missing";
                return false;
            }
            if (!TryDecodeSettlementOutbox(source, out BsSettlementOutboxSnapshot snapshot,
                    out BsSettlementRestoreEvidence evidence, out rejectionReason)
                || snapshot.Receipt != expected)
            {
                if (string.IsNullOrEmpty(rejectionReason))
                    rejectionReason = "The settlement receipt is stale";
                return false;
            }
            SettlementRecord existing = FindSettlementMatch(
                source, snapshot.Draft, expected);
            if (snapshot.State == BsSettlementOutboxState.Committed)
                return existing != null;
            if (existing == null && sameAttempt != null)
            {
                rejectionReason =
                    "The round receipt was already settled with a different operation";
                return false;
            }
            BsSettlementDraft draft = snapshot.Draft;
            if (!string.Equals(source.ActiveAttemptId, draft.AttemptId.Value,
                    StringComparison.Ordinal)
                || source.ActiveAttemptCampaignSlot != draft.CampaignSlot)
            {
                rejectionReason = "The settlement attempt is no longer active";
                return false;
            }

            var machine = new BsSettlementOutboxStateMachine();
            if (!machine.TryRestore(snapshot, evidence)
                || !machine.TryBeginCommit(expected))
            {
                rejectionReason = "The settlement cannot begin from its current state";
                return false;
            }

            next = Clone(source);
            if (existing == null)
            {
                ReconcileLives(next, nowTicks);
                ReconcileDailyOrders(next, dailyUtcTicks);
                switch (draft.Completion)
                {
                    case BsRoundCompletion.Won:
                        CreditCoinsSaturating(next, WinCoinReward);
                        if (draft.CampaignSlot < next.NextUnlockedCampaignSlot)
                            next.ReplayCursor = draft.CampaignSlot + 1;
                        int unlocked = Math.Max(
                            next.NextUnlockedCampaignSlot,
                            draft.NextUnlockedOnWin);
                        next.NextUnlockedCampaignSlot = unlocked;
                        next.DailyOrders.WonLevels = Math.Min(
                            BartenderDailyOrdersTuning.WonLevelTarget,
                            next.DailyOrders.WonLevels + 1);
                        break;

                    case BsRoundCompletion.Failed:
                    case BsRoundCompletion.Quit:
                        if (next.Lives <= 0)
                        {
                            rejectionReason = "No lives remain to spend";
                            return false;
                        }
                        ConsumeLife(next, nowTicks);
                        break;

                    default:
                        rejectionReason = "The settlement outcome is invalid";
                        return false;
                }
                next.Settlements.Add(CaptureSettlementRecord(draft, expected));
                TrimSettlementHistory(next.Settlements, draft.AttemptId.Value);
            }

            next.ActiveAttemptId = string.Empty;
            next.ActiveAttemptCampaignSlot = -1;
            next.ActiveRoundJson = string.Empty;
            if (!machine.TryConfirmCommitted(expected))
            {
                rejectionReason = "The settlement could not be committed";
                return false;
            }
            next.LastSettlementOperationId = Math.Max(
                next.LastSettlementOperationId, machine.LastObservedOperationId);
            next.SettlementOutbox = CaptureSettlementOutbox(machine.Capture());
            return true;
        }

        internal static bool TryAcknowledgeSettlement(
            BsSettlementReceipt expected,
            out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (!expected.IsValid)
            {
                rejectionReason = "The settlement receipt is invalid";
                return false;
            }

            SettlementRecord sameAttempt = FindSettlement(
                expected.Request.AttemptId.Value);
            SettlementRecord receiptMatch = FindSettlementReceiptMatch(
                data, expected);
            if (!HasOwnedSettlementOutbox(data.SettlementOutbox))
            {
                if (receiptMatch != null) return true;
                rejectionReason = sameAttempt != null
                    ? "The round receipt was settled by another operation"
                    : "The committed settlement receipt is missing";
                return false;
            }
            if (!TryDecodeSettlementOutbox(data, out BsSettlementOutboxSnapshot snapshot,
                    out BsSettlementRestoreEvidence evidence, out rejectionReason)
                || snapshot.Receipt != expected
                || evidence != BsSettlementRestoreEvidence.MatchingCommittedRecord)
            {
                if (string.IsNullOrEmpty(rejectionReason))
                    rejectionReason = "The committed settlement receipt is stale";
                return false;
            }

            var machine = new BsSettlementOutboxStateMachine();
            if (!machine.TryRestore(snapshot, evidence)
                || !machine.TryAcknowledge(expected))
            {
                rejectionReason = "The settlement is not ready for acknowledgement";
                return false;
            }

            ProgressData next = Clone(data);
            next.SettlementOutbox = null;
            return Commit(next, false, false, false, out rejectionReason);
        }

        internal static Task<BartenderSaveResult> AcknowledgeSettlementAndBeginAttemptAsync(
            BsSettlementReceipt expected, BsAttemptId newAttemptId, int campaignSlot,
            string initialRoundJson, int paidLifeCoinCost,
            Action<BartenderSaveResult> beforePublish = null) =>
            BeginAsyncWrite((source, dailyTicks, deviceTicks) => PrepareReplacementAttempt(
                source, expected, newAttemptId, campaignSlot, initialRoundJson, paidLifeCoinCost,
                dailyTicks, deviceTicks), beforePublish);

        private static PreparedWrite PrepareReplacementAttempt(ProgressData source,
            BsSettlementReceipt expected, BsAttemptId newAttemptId, int campaignSlot,
            string initialRoundJson, int paidLifeCoinCost, long dailyTicks, long deviceTicks)
        {
            bool succeeded = TryPrepareReplacementAttempt(source, expected, newAttemptId,
                campaignSlot, initialRoundJson, paidLifeCoinCost, dailyTicks, deviceTicks,
                out ProgressData next, out string reason);
            return new PreparedWrite { Succeeded = succeeded, Next = next, RejectionReason = reason };
        }

        private static bool TryPrepareReplacementAttempt(ProgressData source,
            BsSettlementReceipt expected, BsAttemptId newAttemptId, int campaignSlot,
            string initialRoundJson, int paidLifeCoinCost, long dailyTicks, long nowTicks,
            out ProgressData prepared, out string rejectionReason)
        {
            prepared = null;
            rejectionReason = null;
            if (!expected.IsValid || !expected.IsDurable)
            {
                rejectionReason = "The settlement receipt is invalid";
                return false;
            }
            if (!newAttemptId.IsValid || campaignSlot < 0
                || !IsValidActiveRoundJson(initialRoundJson))
            {
                rejectionReason = "The replacement round snapshot is invalid";
                return false;
            }
            if (paidLifeCoinCost < 0)
            {
                rejectionReason = "Invalid continue cost";
                return false;
            }
            if (paidLifeCoinCost > 0
                && expected.Request.Completion == BsRoundCompletion.Won)
            {
                rejectionReason = "A won round cannot purchase a retry life";
                return false;
            }
            if (!string.IsNullOrEmpty(source.ActiveAttemptId))
            {
                rejectionReason = "Another active round is waiting to be settled";
                return false;
            }
            if (FindSettlement(source, newAttemptId.Value) != null)
            {
                rejectionReason = "The replacement attempt id is stale";
                return false;
            }

            if (!HasOwnedSettlementOutbox(source.SettlementOutbox)
                || !TryDecodeSettlementOutbox(
                    source, out BsSettlementOutboxSnapshot snapshot,
                    out BsSettlementRestoreEvidence evidence,
                    out rejectionReason)
                || snapshot.Receipt != expected
                || evidence != BsSettlementRestoreEvidence.MatchingCommittedRecord
                || FindSettlementMatch(source, snapshot.Draft, expected) == null)
            {
                if (string.IsNullOrEmpty(rejectionReason))
                    rejectionReason = "The committed settlement receipt is stale";
                return false;
            }

            var machine = new BsSettlementOutboxStateMachine();
            if (!machine.TryRestore(snapshot, evidence)
                || !machine.TryAcknowledge(expected))
            {
                rejectionReason =
                    "The settlement is not ready for replacement attempt";
                return false;
            }

            ProgressData next = CloneForCurrentClocks(source, dailyTicks, nowTicks);
            if (paidLifeCoinCost > 0)
            {
                if (next.Lives >= MaxLives)
                {
                    rejectionReason = "Lives are already full";
                    return false;
                }
                if (next.Coins < paidLifeCoinCost)
                {
                    rejectionReason =
                        $"Not enough coins: {next.Coins}/{paidLifeCoinCost}";
                    return false;
                }

                next.Coins -= paidLifeCoinCost;
                next.Lives++;
                next.NextLifeUtcTicks = next.Lives >= MaxLives
                    ? 0L
                    : next.NextLifeUtcTicks > nowTicks
                        ? next.NextLifeUtcTicks
                        : SafeAddTicks(
                            nowTicks, LifeRegenerationInterval.Ticks);
            }
            else if (next.Lives <= 0)
            {
                rejectionReason = "Wait for a life to refill";
                return false;
            }

            next.SettlementOutbox = null;
            next.ActiveAttemptId = newAttemptId.Value;
            next.ActiveAttemptCampaignSlot = campaignSlot;
            next.ActiveRoundJson = initialRoundJson;
            prepared = next;
            return true;
        }

        internal static bool TryGetSettlementOutbox(
            out BsSettlementOutboxSnapshot snapshot)
        {
            EnsureLoaded();
            if (HasOwnedSettlementOutbox(data.SettlementOutbox)
                && TryDecodeSettlementOutbox(data, out snapshot, out _, out _))
                return true;
            snapshot = default;
            return false;
        }

        internal static bool TryQuarantineInvalidSettlementOutbox(
            out bool quarantined,
            out string rejectionReason)
        {
            EnsureLoaded();
            quarantined = false;
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (!HasOwnedSettlementOutbox(data.SettlementOutbox)) return true;
            if (TryDecodeSettlementOutbox(data, out _, out _, out _)) return true;

            SettlementOutboxRecord damaged = data.SettlementOutbox;
            bool ownsActiveAttempt = HasActiveAttempt(data)
                && !string.IsNullOrWhiteSpace(damaged.AttemptId)
                && string.Equals(data.ActiveAttemptId, damaged.AttemptId,
                    StringComparison.Ordinal)
                && data.ActiveAttemptCampaignSlot == damaged.CampaignSlot
                && string.Equals(data.ActiveRoundJson, damaged.ActiveRoundJson,
                    StringComparison.Ordinal);

            ProgressData recovered;
            if (ownsActiveAttempt)
            {
                if (!TryCreateRecoveryQuarantine(
                        data,
                        data.ActiveAttemptId,
                        data.ActiveAttemptCampaignSlot,
                        data.ActiveRoundJson,
                        true,
                        out recovered))
                {
                    rejectionReason =
                        "The damaged settlement no longer owns the active round";
                    return false;
                }
            }
            else
            {
                recovered = Clone(data);
                recovered.SettlementOutbox = null;
            }

            if (!Commit(recovered, false, false, false, out rejectionReason))
                return false;
            quarantined = true;
            return true;
        }

        internal static bool TryResolveUnrestorableSettlementOutbox(
            BsSettlementReceipt expected,
            string expectedActiveRoundJson,
            out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!expected.IsValid || !expected.IsDurable
                || string.IsNullOrWhiteSpace(expectedActiveRoundJson))
            {
                rejectionReason = "The settlement recovery receipt is invalid";
                return false;
            }

            if (!HasOwnedSettlementOutbox(data.SettlementOutbox))
                return TryAcknowledgeSettlement(expected, out rejectionReason);
            if (!TryDecodeSettlementOutbox(
                    data,
                    out BsSettlementOutboxSnapshot snapshot,
                    out BsSettlementRestoreEvidence evidence,
                    out rejectionReason)
                || snapshot.Receipt != expected
                || !string.Equals(snapshot.ActiveRoundJson,
                    expectedActiveRoundJson, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(rejectionReason))
                    rejectionReason = "The settlement recovery receipt is stale";
                return false;
            }

            if (evidence == BsSettlementRestoreEvidence.MatchingActiveAttempt
                && !TryCommitSettlement(expected, out rejectionReason))
                return false;
            if (evidence != BsSettlementRestoreEvidence.MatchingActiveAttempt
                && evidence != BsSettlementRestoreEvidence.MatchingCommittedRecord)
            {
                rejectionReason = "The settlement recovery has no durable owner";
                return false;
            }

            return TryAcknowledgeSettlement(expected, out rejectionReason);
        }

        internal static bool TryQuarantineUnrestorableActiveAttempt(
            string expectedAttemptId,
            int expectedCampaignSlot,
            string expectedActiveRoundJson,
            out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (string.IsNullOrWhiteSpace(expectedAttemptId)
                || expectedCampaignSlot < 0
                || string.IsNullOrWhiteSpace(expectedActiveRoundJson))
            {
                rejectionReason = "The active-round recovery receipt is invalid";
                return false;
            }
            if (!HasActiveAttempt(data))
            {
                if (FindRecoveryTombstone(
                        data, expectedAttemptId, expectedCampaignSlot) != null)
                    return true;
                rejectionReason = "The active round is no longer recoverable";
                return false;
            }
            if (HasOwnedSettlementOutbox(data.SettlementOutbox))
            {
                rejectionReason = "A terminal settlement owns the active round";
                return false;
            }
            if (!CanMutate(out rejectionReason)) return false;
            if (!TryCreateRecoveryQuarantine(
                    data,
                    expectedAttemptId,
                    expectedCampaignSlot,
                    expectedActiveRoundJson,
                    false,
                    out ProgressData recovered))
            {
                rejectionReason = "The active-round recovery receipt is stale";
                return false;
            }
            return Commit(recovered, false, false, false, out rejectionReason);
        }

        private static void TryPersistSettlementCommitRetry(
            BsSettlementReceipt expected)
        {
            if (!TryDecodeSettlementOutbox(data,
                    out BsSettlementOutboxSnapshot current,
                    out BsSettlementRestoreEvidence evidence, out _)
                || current.Receipt != expected)
                return;

            var retryMachine = new BsSettlementOutboxStateMachine();
            if (!retryMachine.TryRestore(current, evidence)) return;
            if (retryMachine.State != BsSettlementOutboxState.CommitRetryScheduled
                && (!retryMachine.TryBeginCommit(expected)
                    || !retryMachine.TryDeferCommit(expected)))
                return;

            ProgressData retry = Clone(data);
            retry.SettlementOutbox = CaptureSettlementOutbox(
                retryMachine.Capture());
            if (retry.SettlementOutbox == null) return;

            // The storage error was already logged. Keep the saved Pending state without logging it again.
            Commit(retry, false, false, false, out _, logFailure: false);
        }

        internal static void SynchronizeDailyClock(bool suspended)
        {
            CompletePendingWriteForLifecycle();
            EnsureLoaded();
            if (progressLoadPending) return;
            bool changed = suspended ? BartenderDailyClock.Suspend() : BartenderDailyClock.Resume();
            clockCheckpointPending |= changed;
            // A lifecycle boundary must try its checkpoint even if an earlier background refresh failed.
            if (changed) nextRefreshPersistenceRetryUtcTicks = 0L;
            Refresh(false);
        }

        public static void Refresh() => Refresh(true);

        private static void Refresh(bool asynchronous)
        {
            EnsureLoaded();
            if (mutationInProgress) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (progressLoadPending)
            {
                if (BartenderDailyClock.UtcNowTicks < nextRefreshPersistenceRetryUtcTicks) return;
                loaded = false;
                EnsureLoaded();
                if (progressLoadPending) return;

                // Publish one consistent recovery state so UI callbacks cannot purchase halfway through its
                // events.
                mutationInProgress = true;
                try
                {
                    InvokeSafely(CoinsChanged, data.Coins);
                    InvokeSafely(LivesChanged, data.Lives);
                    InvokeSafely(ProgressChanged, data.NextUnlockedCampaignSlot);
                    InvokeSafely(DailyOrdersChanged, CaptureDailyOrders(data));
                    PublishLifeTimerIfChanged(nowTicks);
                }
                finally
                {
                    mutationInProgress = false;
                }
                return;
            }
            if (interruptedAttemptSettlementPending)
            {
                RetryInterruptedAttemptSettlement(nowTicks);
                if (interruptedAttemptSettlementPending)
                    PublishLifeTimerIfChanged(nowTicks);
                return;
            }
            if (!persistenceDirty && !clockCheckpointPending
                && !NeedsLifeReconcile(data, nowTicks)
                && !NeedsDailyOrdersReconcile(data))
            {
                PublishLifeTimerIfChanged(nowTicks);
                return;
            }
            if (BartenderDailyClock.UtcNowTicks < nextRefreshPersistenceRetryUtcTicks)
            {
                PublishLifeTimerIfChanged(nowTicks);
                return;
            }

            ProgressData next = Clone(data);
            bool livesChanged = ReconcileLives(next, nowTicks);
            bool dailyChanged = ReconcileDailyOrders(next);
            bool durableChange = persistenceDirty || clockCheckpointPending || livesChanged
                              || dailyChanged
                              || next.NextLifeUtcTicks != data.NextLifeUtcTicks;

            if (durableChange)
            {
                if (asynchronous)
                {
                    nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(
                        BartenderDailyClock.UtcNowTicks, RefreshRetryIntervalTicks);
                    _ = BeginAsyncWrite((_, __, ___) => new PreparedWrite { Succeeded = true, Next = next },
                        _ => clockCheckpointPending = false);
                }
                else if (!Commit(next, false, livesChanged, false, out _))
                    nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(BartenderDailyClock.UtcNowTicks,
                        RefreshRetryIntervalTicks);
                else
                    clockCheckpointPending = false;
                return;
            }

            PublishLifeTimerIfChanged(nowTicks);
        }

#if UNITY_EDITOR
        internal static bool EditorTryRetargetActiveAttempt(
            string expectedAttemptId, int expectedCampaignSlot, int targetCampaignSlot,
            out string rejectionReason)
        {
            Refresh(false);
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (string.IsNullOrWhiteSpace(expectedAttemptId)
                || expectedCampaignSlot < 0 || targetCampaignSlot < 0)
            {
                rejectionReason = "Invalid editor attempt ID.";
                return false;
            }
            if (!string.Equals(data.ActiveAttemptId, expectedAttemptId,
                    StringComparison.Ordinal)
                || data.ActiveAttemptCampaignSlot != expectedCampaignSlot)
            {
                rejectionReason = "Editor attempt inactive.";
                return false;
            }
            if (data.ActiveAttemptCampaignSlot == targetCampaignSlot) return true;

            ProgressData next = Clone(data);
            next.ActiveAttemptCampaignSlot = targetCampaignSlot;
            next.ActiveRoundJson = string.Empty;
            return Commit(next, false, false, false, out rejectionReason);
        }

        internal static bool EditorTryDiscardActiveAttempt(
            string expectedAttemptId, int expectedCampaignSlot,
            out string rejectionReason)
        {
            Refresh(false);
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;
            if (string.IsNullOrWhiteSpace(expectedAttemptId)
                || expectedCampaignSlot < 0)
            {
                rejectionReason = "Invalid editor attempt ID.";
                return false;
            }
            if (!string.Equals(data.ActiveAttemptId, expectedAttemptId,
                    StringComparison.Ordinal)
                || data.ActiveAttemptCampaignSlot != expectedCampaignSlot)
            {
                SettlementRecord settled = FindSettlement(expectedAttemptId);
                if (settled != null && settled.CampaignSlot == expectedCampaignSlot)
                    return true;
                rejectionReason = "Editor attempt inactive.";
                return false;
            }

            ProgressData next = Clone(data);
            next.ActiveAttemptId = string.Empty;
            next.ActiveAttemptCampaignSlot = -1;
            next.ActiveRoundJson = string.Empty;
            return Commit(next, false, false, false, out rejectionReason);
        }

        internal static bool EditorTryGetActiveAttempt(out string attemptId,
                                                       out int campaignSlot)
        {
            Refresh(false);
            if (HasActiveAttempt(data))
            {
                attemptId = data.ActiveAttemptId;
                campaignSlot = data.ActiveAttemptCampaignSlot;
                return true;
            }
            attemptId = null;
            campaignSlot = -1;
            return false;
        }

        public static bool EditorSetLives(int value, out string rejectionReason)
        {
            Refresh(false);
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;

            int target = Mathf.Clamp(value, 0, MaxLives);
            long nowTicks = DateTime.UtcNow.Ticks;
            ProgressData next = Clone(data);
            next.Lives = target;
            next.NextLifeUtcTicks = target >= MaxLives
                ? 0L
                : SafeAddTicks(nowTicks, LifeRegenerationInterval.Ticks);

            bool livesChanged = next.Lives != data.Lives;
            if (!livesChanged && next.NextLifeUtcTicks == data.NextLifeUtcTicks)
                return true;
            return Commit(next, false, livesChanged, false, out rejectionReason);
        }

        public static bool EditorRefillLives(out string rejectionReason) =>
            EditorSetLives(MaxLives, out rejectionReason);

        public const int EditorMaxCoins = int.MaxValue - WinCoinReward;

        public static bool EditorSetCoins(int value, out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;

            int target = Mathf.Clamp(value, 0, EditorMaxCoins);
            if (target == data.Coins) return true;
            ProgressData next = Clone(data);
            next.Coins = target;
            return Commit(next, true, false, false, out rejectionReason);
        }

        public static bool EditorAddCoins(int amount, out string rejectionReason)
        {
            EnsureLoaded();
            long target = (long)data.Coins + amount;
            if (target < 0L) target = 0L;
            if (target > EditorMaxCoins) target = EditorMaxCoins;
            return EditorSetCoins((int)target, out rejectionReason);
        }

        public static bool EditorResetDailyOrders(out string rejectionReason)
        {
            if (!EditorSetDailyOrders(false, false, out rejectionReason)) return false;
            BartenderDailyOrdersPresentationStore.EditorReset();
            return true;
        }

        public static bool EditorCompleteDailyOrders(out string rejectionReason) =>
            EditorSetDailyOrders(true, false, out rejectionReason);

        public static bool EditorLockDailyOrders(out string rejectionReason) =>
            EditorSetDailyOrders(true, true, out rejectionReason);

        private static bool EditorSetDailyOrders(bool completed, bool rewardClaimed,
                                                out string rejectionReason)
        {
            EnsureLoaded();
            rejectionReason = null;
            if (!CanMutate(out rejectionReason)) return false;

            ProgressData next = Clone(data);
            next.DailyOrders = new DailyOrdersRecord
            {
                UtcDayKey = UtcDayKey(BartenderDailyClock.UtcNowTicks),
                DeliveredOrders = completed ? BartenderDailyOrdersTuning.DeliveredOrderTarget : 0,
                WonLevels = completed ? BartenderDailyOrdersTuning.WonLevelTarget : 0,
                ServedUnits = completed ? BartenderDailyOrdersTuning.ServedUnitTarget : 0,
                RewardClaimed = rewardClaimed,
            };
            return Commit(next, false, false, false, out rejectionReason);
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            // Domain reload may be disabled in the editor. Never let a previous play session keep writing
            // this profile after the new session has begun loading it.
            if (pendingWrite != null) FinishPendingWrite(pendingWrite, true);
            data = null;
            loaded = false;
            mutationInProgress = false;
            lastPublishedTimerSeconds = long.MinValue;
            nextRefreshPersistenceRetryUtcTicks = 0L;
            persistenceDirty = false;
            progressLoadPending = false;
            interruptedAttemptSettlementPending = false;
            pendingInterruptedAttemptId = string.Empty;
            pendingInterruptedCampaignSlot = -1;
            pendingWrite = null;
            writeCompletion = null;
            clockCheckpointPending = false;
            CoinsChanged = null;
            LivesChanged = null;
            LifeTimerChanged = null;
            ProgressChanged = null;
            DailyOrdersChanged = null;
            BartenderDailyClock.Reset();
            BartenderProgressRuntimeDriver.ResetRegistration();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadBeforeFirstScene() => EnsureLoaded();

        private static bool ReconcileLives(ProgressData target, long nowTicks)
        {
            int previousLives = target.Lives;
            if (target.Lives >= MaxLives)
            {
                target.Lives = MaxLives;
                target.NextLifeUtcTicks = 0L;
                return previousLives != target.Lives;
            }

            long interval = LifeRegenerationInterval.Ticks;
            if (target.NextLifeUtcTicks <= 0L
                || target.NextLifeUtcTicks > SafeAddTicks(nowTicks, interval))
                target.NextLifeUtcTicks = SafeAddTicks(nowTicks, interval);

            if (nowTicks < target.NextLifeUtcTicks) return previousLives != target.Lives;

            long elapsed = nowTicks - target.NextLifeUtcTicks;
            long earned = 1L + elapsed / interval;
            int room = MaxLives - target.Lives;
            int applied = (int)Math.Min(room, earned);
            target.Lives += applied;
            target.NextLifeUtcTicks = target.Lives >= MaxLives
                ? 0L
                : SafeAddTicks(target.NextLifeUtcTicks, applied * interval);
            return previousLives != target.Lives;
        }

        private static bool NeedsLifeReconcile(ProgressData source, long nowTicks)
        {
            if (source.Lives >= MaxLives) return source.NextLifeUtcTicks != 0L;
            long latestValidDeadline = SafeAddTicks(nowTicks,
                LifeRegenerationInterval.Ticks);
            return source.NextLifeUtcTicks <= 0L
                || source.NextLifeUtcTicks > latestValidDeadline
                || nowTicks >= source.NextLifeUtcTicks;
        }

        private static long UtcDayKey(long utcTicks) =>
            Math.Max(0L, utcTicks) / TimeSpan.TicksPerDay;

        private static long MaximumUtcDayKey =>
            DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay;

        private static bool ReconcileDailyOrders(ProgressData target)
            => ReconcileDailyOrders(target, BartenderDailyClock.UtcNowTicks);

        private static bool ReconcileDailyOrders(ProgressData target, long nowTicks)
        {
            if (target == null) return false;
            long today = UtcDayKey(nowTicks);
            if (target.DailyOrders == null)
            {
                target.DailyOrders = new DailyOrdersRecord { UtcDayKey = today };
                return true;
            }

            DailyOrdersRecord daily = target.DailyOrders;
            long previousDay = daily.UtcDayKey;
            int previousDelivered = daily.DeliveredOrders;
            int previousWins = daily.WonLevels;
            int previousServed = daily.ServedUnits;
            bool previousClaimed = daily.RewardClaimed;

            if (daily.UtcDayKey <= 0L || today > daily.UtcDayKey)
            {
                daily.UtcDayKey = today;
                daily.DeliveredOrders = 0;
                daily.WonLevels = 0;
                daily.ServedUnits = 0;
                daily.RewardClaimed = false;
            }
            else
            {
                daily.UtcDayKey = Math.Min(MaximumUtcDayKey, daily.UtcDayKey);
                daily.DeliveredOrders = Mathf.Clamp(
                    daily.DeliveredOrders, 0,
                    BartenderDailyOrdersTuning.DeliveredOrderTarget);
                daily.WonLevels = Mathf.Clamp(
                    daily.WonLevels, 0,
                    BartenderDailyOrdersTuning.WonLevelTarget);
                daily.ServedUnits = Mathf.Clamp(
                    daily.ServedUnits, 0,
                    BartenderDailyOrdersTuning.ServedUnitTarget);
                // Never clear a saved claim because a damaged counter was clamped below the target.
            }

            return previousDay != daily.UtcDayKey
                   || previousDelivered != daily.DeliveredOrders
                   || previousWins != daily.WonLevels
                   || previousServed != daily.ServedUnits
                   || previousClaimed != daily.RewardClaimed;
        }

        private static bool NeedsDailyOrdersReconcile(ProgressData source)
        {
            long nowTicks = BartenderDailyClock.UtcNowTicks;
            DailyOrdersRecord daily = source?.DailyOrders;
            if (daily == null || daily.UtcDayKey <= 0L
                || daily.UtcDayKey > MaximumUtcDayKey)
                return true;
            if (UtcDayKey(nowTicks) > daily.UtcDayKey) return true;
            return daily.DeliveredOrders < 0
                   || daily.DeliveredOrders
                       > BartenderDailyOrdersTuning.DeliveredOrderTarget
                   || daily.WonLevels < 0
                   || daily.WonLevels > BartenderDailyOrdersTuning.WonLevelTarget
                   || daily.ServedUnits < 0
                   || daily.ServedUnits > BartenderDailyOrdersTuning.ServedUnitTarget;
        }

        private static BartenderDailyOrdersSnapshot CaptureDailyOrders(
            ProgressData source)
        {
            DailyOrdersRecord daily = source?.DailyOrders;
            return daily == null
                ? new BartenderDailyOrdersSnapshot(
                    UtcDayKey(BartenderDailyClock.UtcNowTicks), 0, 0, 0, false)
                : new BartenderDailyOrdersSnapshot(
                    daily.UtcDayKey,
                    daily.DeliveredOrders,
                    daily.WonLevels,
                    daily.ServedUnits,
                    daily.RewardClaimed);
        }

        private static bool TryValidateDailyActivity(
            string activeRoundJson,
            BartenderDailyActivityReceipt activity,
            out ActiveRoundHeader header,
            out string rejectionReason)
        {
            header = null;
            // A no-activity settlement still parses its header at the existing ownership check below.
            // Valid activity shares this parsed header with that check instead of scanning the full JSON twice.
            if (activity.Kind != BartenderDailyActivityKind.None && activity.IsValid)
                TryReadActiveRoundHeader(activeRoundJson, out header);
            return TryValidateDailyActivity(header, activity, out rejectionReason);
        }

        private static bool TryValidateDailyActivity(
            ActiveRoundHeader header,
            BartenderDailyActivityReceipt activity,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (activity.Kind == BartenderDailyActivityKind.None) return true;
            if (!activity.IsValid)
            {
                rejectionReason = "The daily activity receipt is invalid";
                return false;
            }
            if (header == null || header.Version != ActiveRoundVersion
                || string.IsNullOrWhiteSpace(header.LevelSignature)
                || !string.Equals(
                    header.AttemptId, activity.AttemptId.Value,
                    StringComparison.Ordinal)
                || header.DomainRevision != activity.DomainRevision
                || header.LastOperationId != activity.OperationId.Value
                || header.BoardRevision != activity.BoardRevision)
            {
                rejectionReason =
                    "The daily activity does not match the active round snapshot";
                return false;
            }
            return true;
        }

        private static bool TryApplyDailyActivity(
            ProgressData target,
            BartenderDailyActivityReceipt activity,
            string previousActiveRoundJson,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (activity.Kind == BartenderDailyActivityKind.None) return true;
            if (!activity.IsValid)
            {
                rejectionReason = "The daily activity receipt is invalid";
                return false;
            }

            target.DailyActivityFences = target.DailyActivityFences
                ?? new List<DailyActivityFenceRecord>();
            for (int i = target.DailyActivityFences.Count - 1; i >= 0; i--)
            {
                DailyActivityFenceRecord fence = target.DailyActivityFences[i];
                if (fence == null
                    || !string.Equals(
                        fence.AttemptId, activity.AttemptId.Value,
                        StringComparison.Ordinal)
                    || fence.OperationId != activity.OperationId.Value)
                    continue;

                if (fence.DomainRevision == activity.DomainRevision
                    && fence.BoardRevision == activity.BoardRevision
                    && fence.Kind == (int)activity.Kind
                    && fence.Amount == activity.Amount
                    && fence.OrderIndex == activity.OrderIndex
                    && string.Equals(fence.RecipeKey, activity.RecipeKey,
                        StringComparison.Ordinal))
                    return true;

                rejectionReason = "The daily activity receipt conflicts with saved data";
                return false;
            }

            // Undo may give the same order a new operation id. Credit it only once per attempt, even across
            // midnight.
            foreach (DailyActivityFenceRecord fence in target.DailyActivityFences)
            {
                if (fence == null
                    || fence.Kind != (int)BartenderDailyActivityKind.DeliveredRecipe
                    || !string.Equals(fence.AttemptId, activity.AttemptId.Value,
                        StringComparison.Ordinal)
                    || fence.OrderIndex != activity.OrderIndex) continue;
                if (fence.Amount == activity.Amount
                    && string.Equals(fence.RecipeKey, activity.RecipeKey,
                        StringComparison.Ordinal)) return true;
                rejectionReason = "The delivered order conflicts with saved data";
                return false;
            }

            if (TryReadActiveRoundHeader(
                    previousActiveRoundJson, out ActiveRoundHeader previous)
                && string.Equals(
                    previous.AttemptId, activity.AttemptId.Value,
                    StringComparison.Ordinal)
                && (activity.OperationId.Value <= previous.LastOperationId
                    || activity.DomainRevision <= previous.DomainRevision
                    || activity.BoardRevision <= previous.BoardRevision))
            {
                rejectionReason = "The daily activity receipt is stale";
                return false;
            }

            target.DailyOrders.ServedUnits = (int)Math.Min(
                BartenderDailyOrdersTuning.ServedUnitTarget,
                (long)target.DailyOrders.ServedUnits + activity.Amount);
            target.DailyOrders.DeliveredOrders = Math.Min(
                BartenderDailyOrdersTuning.DeliveredOrderTarget,
                target.DailyOrders.DeliveredOrders + 1);
            if (!target.UnlockedRecipes.Contains(activity.RecipeKey))
                target.UnlockedRecipes.Add(activity.RecipeKey);

            target.DailyActivityFences.Add(new DailyActivityFenceRecord
            {
                AttemptId = activity.AttemptId.Value,
                OperationId = activity.OperationId.Value,
                DomainRevision = activity.DomainRevision,
                BoardRevision = activity.BoardRevision,
                Kind = (int)activity.Kind,
                Amount = activity.Amount,
                RecipeKey = activity.RecipeKey,
                OrderIndex = activity.OrderIndex,
            });
            TrimDailyActivityFences(target.DailyActivityFences, target.ActiveAttemptId);
            return true;
        }

        private static TimeSpan RemainingLifeTime(ProgressData source, long nowTicks)
        {
            if (source == null || source.Lives >= MaxLives
                || source.NextLifeUtcTicks <= nowTicks)
                return TimeSpan.Zero;
            return TimeSpan.FromTicks(source.NextLifeUtcTicks - nowTicks);
        }

        private static void PublishLifeTimerIfChanged(long nowTicks)
        {
            TimeSpan remaining = RemainingLifeTime(data, nowTicks);
            long seconds = remaining <= TimeSpan.Zero
                ? 0L
                : (long)Math.Ceiling(remaining.TotalSeconds);
            if (seconds == lastPublishedTimerSeconds) return;
            lastPublishedTimerSeconds = seconds;
            InvokeSafely(LifeTimerChanged, TimeSpan.FromSeconds(seconds));
        }

        private static bool CanMutate(out string rejectionReason)
        {
            if (progressLoadPending)
            {
                rejectionReason = "Player progress could not be loaded; try again";
                return false;
            }
            if (mutationInProgress)
            {
                rejectionReason = "Another save operation is in progress";
                return false;
            }
            if (interruptedAttemptSettlementPending)
            {
                rejectionReason = "The previous round is closing; try again";
                return false;
            }
            rejectionReason = null;
            return true;
        }

        private static Task<BartenderSaveResult> BeginAsyncWrite(
            Func<ProgressData, long, long, PreparedWrite> prepare,
            Action<BartenderSaveResult> beforePublish = null)
        {
            EnsureLoaded();
            if (!CanMutate(out string rejectionReason))
                return Task.FromResult(new BartenderSaveResult(false, rejectionReason));

            // Never queue a draft behind another writer: each transaction must begin from the last
            // adopted durable state. All values needed by the worker are detached or immutable.
            mutationInProgress = true;
            var pending = new PendingWrite { BeforePublish = beforePublish };
            writeCompletion = pending.Completion.Task;
            try
            {
                // Published ProgressData is read-only. The reservation prevents every other mutation and
                // reset drains this worker. Preparation makes the single owned mutable draft on the worker;
                // copying its collections here too would duplicate allocations on every move.
                ProgressData source = data;
                string path = SavePath;
                long dailyUtcTicks = BartenderDailyClock.UtcNowTicks;
                long deviceUtcTicks = DateTime.UtcNow.Ticks;
                pendingWrite = pending;
                pending.Worker = Task.Run(() => ExecutePreparedWrite(
                    prepare, source, path, dailyUtcTicks, deviceUtcTicks));
                return pending.Completion.Task;
            }
            catch (Exception exception)
            {
                pendingWrite = null;
                writeCompletion = null;
                mutationInProgress = false;
                Debug.LogException(exception);
                return Task.FromResult(new BartenderSaveResult(false,
                    "Player progress could not be saved"));
            }
        }

        private static PreparedWrite ExecutePreparedWrite(
            Func<ProgressData, long, long, PreparedWrite> prepare,
            ProgressData source, string path, long dailyUtcTicks, long deviceUtcTicks)
        {
            try
            {
                PreparedWrite prepared = prepare(source, dailyUtcTicks, deviceUtcTicks);
                if (!prepared.Succeeded || prepared.Next == null) return prepared;
                prepared.Next.DailyClockUtcTicks = dailyUtcTicks;
                prepared.Next.DailyClockDeviceUtcTicks = deviceUtcTicks;
                PersistPrepared(prepared.Next, path);
                return prepared;
            }
            catch (Exception exception)
            {
                return new PreparedWrite
                {
                    RejectionReason = "Player progress could not be saved",
                    Exception = exception,
                };
            }
        }

        internal static void PumpPendingWrite()
        {
            PendingWrite pending = pendingWrite;
            if (pending == null || !pending.Worker.IsCompleted) return;
            FinishPendingWrite(pending, false);
        }

        internal static void CompletePendingWriteForLifecycle()
        {
            PendingWrite pending = pendingWrite;
            if (pending != null) FinishPendingWrite(pending, false);
        }

        private static void FinishPendingWrite(PendingWrite pending, bool endingSession)
        {
            if (!ReferenceEquals(pendingWrite, pending)) return;
            PreparedWrite prepared;
            try
            {
                prepared = pending.Worker.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                prepared = new PreparedWrite
                {
                    RejectionReason = "Player progress could not be saved",
                    Exception = exception,
                };
            }

            pendingWrite = null;
            var result = new BartenderSaveResult(prepared.Succeeded,
                prepared.RejectionReason, prepared.Receipt);
            try
            {
                if (!endingSession && prepared.Succeeded)
                {
                    Action beforePublish = pending.BeforePublish == null
                        ? (Action)null : () => pending.BeforePublish(result);
                    if (prepared.Next != null)
                    {
                        ProgressData next = prepared.Next;
                        AdoptCommittedData(next, next.Coins != data.Coins,
                            next.Lives != data.Lives,
                            prepared.ProgressChanged
                                || next.NextUnlockedCampaignSlot != data.NextUnlockedCampaignSlot,
                            !DailyOrdersRecordEquals(data.DailyOrders, next.DailyOrders),
                            beforePublish);
                    }
                    else
                    {
                        InvokeBeforePublish(beforePublish);
                    }
                }
                if (prepared.Exception != null) Debug.LogException(prepared.Exception);
            }
            finally
            {
                mutationInProgress = false;
                if (ReferenceEquals(writeCompletion, pending.Completion.Task))
                    writeCompletion = null;
                // Static reset drains the disk worker but must not authorize old-scene continuations to publish.
                pending.Completion.TrySetResult(endingSession
                    ? new BartenderSaveResult(false, "The progress session has ended") : result);
            }
        }

        private static void InvokeBeforePublish(Action beforePublish)
        {
            if (beforePublish == null) return;
            try { beforePublish(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private static ProgressData CloneForCurrentClocks(ProgressData source,
            long dailyTicks, long deviceTicks)
        {
            ProgressData next = Clone(source);
            ReconcileLives(next, deviceTicks);
            ReconcileDailyOrders(next, dailyTicks);
            return next;
        }

        private static PreparedWrite RejectWrite(string reason) =>
            new PreparedWrite { RejectionReason = reason };

        private static bool CommitPreparedSynchronously(PreparedWrite prepared,
            out string rejectionReason)
        {
            rejectionReason = prepared.RejectionReason;
            if (!prepared.Succeeded) return false;
            if (prepared.Next == null) return true;
            ProgressData next = prepared.Next;
            return Commit(next, next.Coins != data.Coins, next.Lives != data.Lives,
                prepared.ProgressChanged || next.NextUnlockedCampaignSlot != data.NextUnlockedCampaignSlot,
                out rejectionReason);
        }

        private static bool Commit(ProgressData next, bool coinsChanged,
                                   bool livesChanged, bool progressChanged,
                                   out string rejectionReason,
                                   bool logFailure = true)
        {
            rejectionReason = null;
            if (progressLoadPending)
            {
                rejectionReason = "Player progress could not be loaded; try again";
                return false;
            }
            if (mutationInProgress)
            {
                rejectionReason = "Another save operation is in progress";
                return false;
            }

            bool dailyOrdersChanged = !DailyOrdersRecordEquals(
                data?.DailyOrders, next?.DailyOrders);
            mutationInProgress = true;
            try
            {
                Persist(next);
                AdoptCommittedData(
                    next, coinsChanged, livesChanged, progressChanged,
                    dailyOrdersChanged);
                return true;
            }
            catch (Exception exception)
            {
                // Cleanup can fail after a successful file rename. An exact saved match means the commit
                // succeeded; do not charge its life again.
                ProgressData persisted = TryLoad(SavePath);
                if (ProgressDataEquals(persisted, next))
                {
                    AdoptCommittedData(persisted, coinsChanged, livesChanged,
                        progressChanged, dailyOrdersChanged);
                    return true;
                }
                rejectionReason = "Player progress could not be saved";
                if (logFailure) Debug.LogException(exception);
                return false;
            }
            finally
            {
                mutationInProgress = false;
            }
        }

        private static void AdoptCommittedData(ProgressData committed,
                                               bool coinsChanged,
                                               bool livesChanged,
                                               bool progressChanged,
                                               bool dailyOrdersChanged,
                                               Action beforePublish = null)
        {
            CanonicalizeSerializedState(committed);
            data = committed;
            persistenceDirty = false;
            nextRefreshPersistenceRetryUtcTicks = 0L;
            InvokeBeforePublish(beforePublish);
            if (coinsChanged) InvokeSafely(CoinsChanged, data.Coins);
            if (livesChanged) InvokeSafely(LivesChanged, data.Lives);
            if (progressChanged)
                InvokeSafely(ProgressChanged, data.NextUnlockedCampaignSlot);
            if (dailyOrdersChanged)
                InvokeSafely(DailyOrdersChanged, CaptureDailyOrders(data));
            PublishLifeTimerIfChanged(DateTime.UtcNow.Ticks);
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            string savePath = SavePath;
            bool hasPersistedCandidate = MayContainProgressFile(savePath)
                                         || MayContainProgressFile(savePath + ".tmp")
                                         || MayContainProgressFile(savePath + ".bak");
            ProgressData loadedData = TryLoad(savePath);
            if (loadedData == null) loadedData = TryLoad(savePath + ".tmp");
            if (loadedData == null) loadedData = TryLoad(savePath + ".bak");
            if (loadedData == null
                && (hasPersistedCandidate || progressLoadPending))
            {
                data = new ProgressData { Coins = 0, Lives = 0 };
                loaded = true;
                progressLoadPending = true;
                persistenceDirty = false;
                nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(
                    BartenderDailyClock.UtcNowTicks, RefreshRetryIntervalTicks);
                return;
            }
            if (loadedData == null)
                loadedData = CreateInitialProgressData(!hasPersistedCandidate);
            progressLoadPending = false;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (loadedData.DailyClockUtcTicks <= 0L
                && loadedData.DailyOrders.UtcDayKey > UtcDayKey(nowTicks))
            {
                loadedData.DailyOrders.UtcDayKey = UtcDayKey(nowTicks);
            }
            BartenderDailyClock.Initialize(loadedData.DailyClockUtcTicks,
                loadedData.DailyClockDeviceUtcTicks, nowTicks);
            Normalize(loadedData, nowTicks);
            lastPublishedTimerSeconds = long.MinValue;

            // Quarantine a broken active snapshot without charging an uncertain result. A terminal outbox
            // keeps ownership of its result and is recovered through its exact settlement receipt instead.
            if (HasActiveAttempt(loadedData)
                && !HasOwnedSettlementOutbox(loadedData.SettlementOutbox)
                && !IsValidActiveRoundJson(loadedData.ActiveRoundJson))
            {
                string interruptedAttemptId = loadedData.ActiveAttemptId;
                int interruptedCampaignSlot = loadedData.ActiveAttemptCampaignSlot;
                if (!TryCreateInterruptedAttemptSettlement(loadedData,
                        out ProgressData settledData))
                {
                    data = loadedData;
                    loaded = true;
                    persistenceDirty = false;
                    ArmInterruptedAttemptSettlement(interruptedAttemptId,
                        interruptedCampaignSlot, nowTicks);
                    return;
                }
                try
                {
                    Persist(settledData);
                    data = settledData;
                    loaded = true;
                    persistenceDirty = false;
                    ClearInterruptedAttemptSettlementPending();
                    return;
                }
                catch (Exception exception)
                {
                    ProgressData persisted = TryLoadResolvedInterruptedAttempt(
                        interruptedAttemptId, nowTicks);
                    if (persisted != null)
                    {
                        data = persisted;
                        loaded = true;
                        persistenceDirty = false;
                        ClearInterruptedAttemptSettlementPending();
                        return;
                    }

                    data = loadedData;
                    loaded = true;
                    persistenceDirty = false;
                    ArmInterruptedAttemptSettlement(interruptedAttemptId,
                        interruptedCampaignSlot, nowTicks);
                    Debug.LogException(exception);
                    return;
                }
            }

            data = loadedData;
            loaded = true;

            try
            {
                Persist(data);
                persistenceDirty = false;
            }
            catch (Exception exception)
            {
                persistenceDirty = true;
                nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(
                    BartenderDailyClock.UtcNowTicks, RefreshRetryIntervalTicks);
                Debug.LogException(exception);
            }
        }

        private static string SavePath
        {
            get
            {
                return Path.Combine(
                    Application.persistentDataPath,
                    BartenderProgressTuning.IsolatedEditorTestProfileEnabled
                        ? EditorTestSaveFilePrefix
                          + BartenderProgressTuning.EditorTestSaveSuffix + ".json"
                        : ProductionSaveFileName);
            }
        }

        private static bool MayContainProgressFile(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch
            {
                return true;
            }
        }

        private static ProgressData TryLoad(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return null;
                ProgressFileHeader header =
                    JsonUtility.FromJson<ProgressFileHeader>(json);
                if (header == null
                    || header.ActiveAttemptId == null
                    || header.Settlements == null
                    || !HasRequiredCoreProgressFields(json)
                    )
                {
                    Debug.LogWarning("Player progress unavailable; recovery will run.");
                    return null;
                }

                if (header.Version == 1 || header.Version == 2)
                    return MigrateLegacyProgress(json, header.Version);
                if ((header.Version != 4 && header.Version != 5 && header.Version != CurrentVersion)
                    || header.ActiveRoundJson == null)
                {
                    Debug.LogWarning("Player progress unavailable; recovery will run.");
                    return null;
                }
                if (header.Version >= 5
                    && (!HasSerializedField(json, "UnlockedRecipes")
                        || !HasSerializedField(json, "ServedUnits")))
                {
                    Debug.LogWarning("Player progress is missing collection or daily data; recovery will be attempted.");
                    return null;
                }
                if (header.Version == CurrentVersion
                    && (!HasSerializedField(json, "DailyClockUtcTicks")
                        || !HasSerializedField(json, "DailyClockDeviceUtcTicks")
                        || !HasSerializedField(json, "SelectedReplayCampaignSlot")
                        || !HasSerializedField(json, "ReplayCursor"))) return null;

                ProgressData parsed = JsonUtility.FromJson<ProgressData>(json);
                CanonicalizeSerializedState(parsed);
                if (header.Version < 6) parsed.SelectedReplayCampaignSlot = -1;
                if (header.Version == 4)
                {
                    parsed.Version = CurrentVersion;
                    parsed.DailyOrders.ServedUnits = parsed.DailyOrders.RewardClaimed
                        ? BartenderDailyOrdersTuning.ServedUnitTarget : 0;
                }
                return parsed;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Player progress read failed: " + exception.Message);
                return null;
            }
        }

        private static ProgressData CreateInitialProgressData(
            bool allowLegacyPlayerPrefs)
        {
            var initial = new ProgressData();
            if (!allowLegacyPlayerPrefs
                || !TryReadLegacyPlayerPrefs(out int legacyCoins,
                    out int legacyCampaignSlot))
                return initial;

            initial.Coins = Math.Max(0, legacyCoins);
            initial.NextUnlockedCampaignSlot = Math.Max(0, legacyCampaignSlot);
            return initial;
        }

        private static bool TryReadLegacyPlayerPrefs(
            out int legacyCoins, out int legacyCampaignSlot)
        {
#if UNITY_EDITOR
            if (BartenderProgressTuning.IsolatedEditorTestProfileEnabled)
            {
                legacyCoins = 0;
                legacyCampaignSlot = 0;
                return false;
            }
#endif
            legacyCoins = PlayerPrefs.GetInt(
                LegacyCoinsKey, DefaultStartingCoins);
            legacyCampaignSlot = PlayerPrefs.GetInt(LegacyProgressKey, 0);
            return true;
        }

        private static ProgressData MigrateLegacyProgress(
            string json, int loadedVersion)
        {
            LegacyProgressData legacy =
                JsonUtility.FromJson<LegacyProgressData>(json);
            if (legacy == null
                || legacy.Version != loadedVersion
                || legacy.ActiveAttemptId == null
                || legacy.Settlements == null)
                return null;

            var migrated = new ProgressData
            {
                Version = CurrentVersion,
                Coins = legacy.Coins,
                Lives = legacy.Lives,
                NextUnlockedCampaignSlot = legacy.NextUnlockedCampaignSlot,
                NextLifeUtcTicks = legacy.NextLifeUtcTicks,
                ActiveAttemptId = legacy.ActiveAttemptId,
                ActiveAttemptCampaignSlot = legacy.ActiveAttemptCampaignSlot,
                ActiveRoundJson = string.Empty,
                Settlements = new List<SettlementRecord>(
                    legacy.Settlements.Count),
            };
            for (int i = 0; i < legacy.Settlements.Count; i++)
            {
                LegacySettlementRecord record = legacy.Settlements[i];
                if (record == null)
                {
                    migrated.Settlements.Add(null);
                    continue;
                }

                int nextUnlockedOnWin = record.NextUnlockedOnWin;
                if (loadedVersion < 2)
                {
                    nextUnlockedOnWin =
                        record.Kind == (int)BartenderSettlementKind.Won
                        && record.CampaignSlot < int.MaxValue
                            ? record.CampaignSlot + 1
                            : -1;
                }
                migrated.Settlements.Add(new SettlementRecord
                {
                    AttemptId = record.AttemptId,
                    Kind = record.Kind,
                    CampaignSlot = record.CampaignSlot,
                    NextUnlockedOnWin = nextUnlockedOnWin,
                });
            }
            CanonicalizeSerializedState(migrated);
            return migrated;
        }

        private static bool HasRequiredCoreProgressFields(string json) =>
            HasSerializedField(json, "Coins")
            && HasSerializedField(json, "Lives")
            && HasSerializedField(json, "NextUnlockedCampaignSlot")
            && HasSerializedField(json, "NextLifeUtcTicks")
            && HasSerializedField(json, "ActiveAttemptCampaignSlot");

        private static bool HasSerializedField(string json, string fieldName) =>
            json.IndexOf("\"" + fieldName + "\"", StringComparison.Ordinal) >= 0;

        private static void Normalize(ProgressData target, long nowTicks)
        {
            target.Version = CurrentVersion;
            // Rebuild the highest id from fully valid receipts so corrupt cached values cannot exhaust
            // coordinator ids.
            target.LastSettlementOperationId = 0L;
            target.Coins = Math.Max(0, target.Coins);
            target.Lives = Mathf.Clamp(target.Lives, 0, MaxLives);
            target.NextUnlockedCampaignSlot = Math.Max(0,
                target.NextUnlockedCampaignSlot);
            if (target.SelectedReplayCampaignSlot < -1
                || target.SelectedReplayCampaignSlot >= target.NextUnlockedCampaignSlot)
                target.SelectedReplayCampaignSlot = -1;
            target.ReplayCursor = Math.Max(0, target.ReplayCursor);
            target.ActiveAttemptId = target.ActiveAttemptId ?? string.Empty;
            target.ActiveRoundJson = target.ActiveRoundJson ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target.ActiveAttemptId)
                || target.ActiveAttemptCampaignSlot < 0)
            {
                target.ActiveAttemptId = string.Empty;
                target.ActiveAttemptCampaignSlot = -1;
                target.ActiveRoundJson = string.Empty;
            }
            target.Settlements = target.Settlements ?? new List<SettlementRecord>();
            for (int i = target.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = target.Settlements[i];
                if (record == null || string.IsNullOrWhiteSpace(record.AttemptId))
                {
                    target.Settlements.RemoveAt(i);
                    continue;
                }
                if (TryGetTrustedSettlementOperationId(
                        record, out long trustedOperationId)
                    && trustedOperationId > target.LastSettlementOperationId)
                    target.LastSettlementOperationId = trustedOperationId;
            }
            CanonicalizeSerializedState(target);
            NormalizeDailyActivityFences(target.DailyActivityFences, target.ActiveAttemptId);
            ReconcileDailyOrders(target);
            bool hasTrustedSettlementOutbox = TryDecodeSettlementOutbox(
                target,
                out BsSettlementOutboxSnapshot trustedOutbox,
                out _,
                out _);
            if (hasTrustedSettlementOutbox
                && trustedOutbox.Draft.OperationId.Value
                    > target.LastSettlementOperationId)
                target.LastSettlementOperationId =
                    trustedOutbox.Draft.OperationId.Value;
            string preservedSettlementAttempt =
                hasTrustedSettlementOutbox
                ? trustedOutbox.Draft.AttemptId.Value
                : target.ActiveAttemptId;
            TrimSettlementHistory(target.Settlements, preservedSettlementAttempt);
            ReconcileLives(target, nowTicks);
        }

        private static void Persist(ProgressData source)
        {
            source.DailyClockUtcTicks = BartenderDailyClock.UtcNowTicks;
            source.DailyClockDeviceUtcTicks = DateTime.UtcNow.Ticks;
            PersistPrepared(source, SavePath);
        }

        private static void PersistPrepared(ProgressData source, string path)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(source));
            try
            {
                WriteProgressBytes(path, bytes);
            }
            catch
            {
                // A rename can succeed before backup cleanup throws. Exact intended bytes prove the
                // transaction is durable; returning failure here would let a paid operation retry twice.
                if (HasExactPersistedBytes(path, bytes)) return;
                throw;
            }
        }

        private static bool HasExactPersistedBytes(string path, byte[] expected)
        {
            try
            {
                byte[] actual = File.ReadAllBytes(path);
                if (actual.Length != expected.Length) return false;
                for (int i = 0; i < actual.Length; i++)
                    if (actual[i] != expected[i]) return false;
                return true;
            }
            catch { return false; }
        }

        private static void WriteProgressBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            try
            {
                File.Replace(temporaryPath, path, null);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
            catch (NotSupportedException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
            catch (IOException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
            catch (UnauthorizedAccessException)
            {
                ReplaceWithRecoverableRename(path, temporaryPath);
            }
        }

        private static void ReplaceWithRecoverableRename(string path,
                                                         string temporaryPath)
        {
            string backupPath = path + ".bak";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(path, backupPath);
            try
            {
                File.Move(temporaryPath, path);
                File.Delete(backupPath);
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(backupPath))
                    File.Move(backupPath, path);
                throw;
            }
        }

        private static ProgressData Clone(ProgressData source)
        {
            var clone = new ProgressData
            {
                Version = source.Version,
                Coins = source.Coins,
                Lives = source.Lives,
                NextUnlockedCampaignSlot = source.NextUnlockedCampaignSlot,
                SelectedReplayCampaignSlot = source.SelectedReplayCampaignSlot,
                ReplayCursor = source.ReplayCursor,
                DailyClockUtcTicks = source.DailyClockUtcTicks,
                DailyClockDeviceUtcTicks = source.DailyClockDeviceUtcTicks,
                NextLifeUtcTicks = source.NextLifeUtcTicks,
                ActiveAttemptId = source.ActiveAttemptId,
                ActiveAttemptCampaignSlot = source.ActiveAttemptCampaignSlot,
                ActiveRoundJson = source.ActiveRoundJson,
                LastSettlementOperationId = source.LastSettlementOperationId,
                SettlementOutbox = CloneSettlementOutbox(source.SettlementOutbox),
                Settlements = new List<SettlementRecord>(source.Settlements.Count),
                DailyOrders = CloneDailyOrders(source.DailyOrders),
                UnlockedRecipes = new List<string>(source.UnlockedRecipes),
                DailyActivityFences = new List<DailyActivityFenceRecord>(
                    source.DailyActivityFences?.Count ?? 0),
            };
            for (int i = 0; i < source.Settlements.Count; i++)
            {
                SettlementRecord record = source.Settlements[i];
                clone.Settlements.Add(new SettlementRecord
                {
                    AttemptId = record.AttemptId,
                    Kind = record.Kind,
                    CampaignSlot = record.CampaignSlot,
                    NextUnlockedOnWin = record.NextUnlockedOnWin,
                    OperationId = record.OperationId,
                    Revision = record.Revision,
                    BoardRevision = record.BoardRevision,
                    Cause = record.Cause,
                    TimeOfferId = record.TimeOfferId,
                    TimeOfferDisposition = record.TimeOfferDisposition,
                    PersistenceReceiptId = record.PersistenceReceiptId,
                });
            }
            if (source.DailyActivityFences != null)
            {
                for (int i = 0; i < source.DailyActivityFences.Count; i++)
                {
                    DailyActivityFenceRecord record =
                        source.DailyActivityFences[i];
                    if (record == null)
                    {
                        clone.DailyActivityFences.Add(null);
                        continue;
                    }
                    clone.DailyActivityFences.Add(new DailyActivityFenceRecord
                    {
                        AttemptId = record.AttemptId,
                        OperationId = record.OperationId,
                        DomainRevision = record.DomainRevision,
                        BoardRevision = record.BoardRevision,
                        Kind = record.Kind,
                        Amount = record.Amount,
                        RecipeKey = record.RecipeKey,
                        OrderIndex = record.OrderIndex,
                    });
                }
            }
            return clone;
        }

        private static DailyOrdersRecord CloneDailyOrders(DailyOrdersRecord source) =>
            source == null
                ? new DailyOrdersRecord()
                : new DailyOrdersRecord
                {
                    UtcDayKey = source.UtcDayKey,
                    DeliveredOrders = source.DeliveredOrders,
                    WonLevels = source.WonLevels,
                    ServedUnits = source.ServedUnits,
                    RewardClaimed = source.RewardClaimed,
                };

        private static bool ProgressDataEquals(ProgressData left, ProgressData right)
        {
            if (left == null || right == null
                || left.Version != right.Version
                || left.Coins != right.Coins
                || left.Lives != right.Lives
                || left.NextUnlockedCampaignSlot != right.NextUnlockedCampaignSlot
                || left.SelectedReplayCampaignSlot != right.SelectedReplayCampaignSlot
                || left.ReplayCursor != right.ReplayCursor
                || left.DailyClockUtcTicks != right.DailyClockUtcTicks
                || left.DailyClockDeviceUtcTicks != right.DailyClockDeviceUtcTicks
                || left.NextLifeUtcTicks != right.NextLifeUtcTicks
                || !string.Equals(left.ActiveAttemptId, right.ActiveAttemptId,
                    StringComparison.Ordinal)
                || left.ActiveAttemptCampaignSlot != right.ActiveAttemptCampaignSlot
                || !string.Equals(left.ActiveRoundJson, right.ActiveRoundJson,
                    StringComparison.Ordinal)
                || left.LastSettlementOperationId != right.LastSettlementOperationId
                || !SettlementOutboxEquals(
                    left.SettlementOutbox, right.SettlementOutbox)
                || !DailyOrdersRecordEquals(left.DailyOrders, right.DailyOrders)
                || !RecipeKeysEqual(left.UnlockedRecipes, right.UnlockedRecipes)
                || !DailyActivityFencesEqual(
                    left.DailyActivityFences, right.DailyActivityFences))
                return false;

            int leftCount = left.Settlements?.Count ?? 0;
            int rightCount = right.Settlements?.Count ?? 0;
            if (leftCount != rightCount) return false;
            for (int i = 0; i < leftCount; i++)
            {
                SettlementRecord a = left.Settlements[i];
                SettlementRecord b = right.Settlements[i];
                if (a == null || b == null)
                {
                    if (!ReferenceEquals(a, b)) return false;
                    continue;
                }
                if (!string.Equals(a.AttemptId, b.AttemptId,
                        StringComparison.Ordinal)
                    || a.Kind != b.Kind
                    || a.CampaignSlot != b.CampaignSlot
                    || a.NextUnlockedOnWin != b.NextUnlockedOnWin
                    || a.OperationId != b.OperationId
                    || a.Revision != b.Revision
                    || a.BoardRevision != b.BoardRevision
                    || a.Cause != b.Cause
                    || a.TimeOfferId != b.TimeOfferId
                    || a.TimeOfferDisposition != b.TimeOfferDisposition
                    || !string.Equals(a.PersistenceReceiptId,
                        b.PersistenceReceiptId, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static bool DailyOrdersRecordEquals(
            DailyOrdersRecord left, DailyOrdersRecord right)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);
            return left.UtcDayKey == right.UtcDayKey
                   && left.DeliveredOrders == right.DeliveredOrders
                   && left.WonLevels == right.WonLevels
                   && left.ServedUnits == right.ServedUnits
                   && left.RewardClaimed == right.RewardClaimed;
        }

        private static bool DailyActivityFencesEqual(
            List<DailyActivityFenceRecord> left,
            List<DailyActivityFenceRecord> right)
        {
            int leftCount = left?.Count ?? 0;
            int rightCount = right?.Count ?? 0;
            if (leftCount != rightCount) return false;
            for (int i = 0; i < leftCount; i++)
            {
                DailyActivityFenceRecord a = left[i];
                DailyActivityFenceRecord b = right[i];
                if (a == null || b == null)
                {
                    if (!ReferenceEquals(a, b)) return false;
                    continue;
                }
                if (!string.Equals(a.AttemptId, b.AttemptId,
                        StringComparison.Ordinal)
                    || a.OperationId != b.OperationId
                    || a.DomainRevision != b.DomainRevision
                    || a.BoardRevision != b.BoardRevision
                    || a.Kind != b.Kind
                    || a.Amount != b.Amount
                    || a.OrderIndex != b.OrderIndex
                    || !string.Equals(a.RecipeKey, b.RecipeKey, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static bool RecipeKeysEqual(List<string> left, List<string> right)
        {
            int count = left?.Count ?? 0;
            if (count != (right?.Count ?? 0)) return false;
            for (int i = 0; i < count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static SettlementOutboxRecord CloneSettlementOutbox(
            SettlementOutboxRecord source)
        {
            if (source == null) return null;
            return new SettlementOutboxRecord
            {
                State = source.State,
                OperationId = source.OperationId,
                AttemptId = source.AttemptId,
                CampaignSlot = source.CampaignSlot,
                Revision = source.Revision,
                BoardRevision = source.BoardRevision,
                Completion = source.Completion,
                Cause = source.Cause,
                NextUnlockedOnWin = source.NextUnlockedOnWin,
                TimeOfferId = source.TimeOfferId,
                TimeOfferDisposition = source.TimeOfferDisposition,
                PersistenceReceiptId = source.PersistenceReceiptId,
                ActiveRoundJson = source.ActiveRoundJson,
            };
        }

        private static bool SettlementOutboxEquals(
            SettlementOutboxRecord left, SettlementOutboxRecord right)
        {
            bool leftEmpty = !HasOwnedSettlementOutbox(left);
            bool rightEmpty = !HasOwnedSettlementOutbox(right);
            if (leftEmpty || rightEmpty) return leftEmpty && rightEmpty;
            return left.State == right.State
                   && left.OperationId == right.OperationId
                   && string.Equals(left.AttemptId, right.AttemptId,
                       StringComparison.Ordinal)
                   && left.CampaignSlot == right.CampaignSlot
                   && left.Revision == right.Revision
                   && left.BoardRevision == right.BoardRevision
                   && left.Completion == right.Completion
                   && left.Cause == right.Cause
                   && left.NextUnlockedOnWin == right.NextUnlockedOnWin
                   && left.TimeOfferId == right.TimeOfferId
                   && left.TimeOfferDisposition == right.TimeOfferDisposition
                   && string.Equals(left.PersistenceReceiptId,
                       right.PersistenceReceiptId, StringComparison.Ordinal)
                   && string.Equals(left.ActiveRoundJson,
                       right.ActiveRoundJson, StringComparison.Ordinal);
        }

        private static SettlementOutboxRecord CaptureSettlementOutbox(
            BsSettlementOutboxSnapshot snapshot)
        {
            if (!snapshot.IsValid || !snapshot.Receipt.IsValid) return null;
            BsSettlementDraft draft = snapshot.Draft;
            BsSettlementReceipt receipt = snapshot.Receipt;
            return new SettlementOutboxRecord
            {
                State = (int)snapshot.State,
                OperationId = draft.OperationId.Value,
                AttemptId = draft.AttemptId.Value,
                CampaignSlot = draft.CampaignSlot,
                Revision = draft.Revision,
                BoardRevision = draft.BoardRevision,
                Completion = (int)draft.Completion,
                Cause = (int)draft.Cause,
                NextUnlockedOnWin = draft.NextUnlockedOnWin,
                TimeOfferId = draft.TimeOfferId.Value,
                TimeOfferDisposition = (int)draft.TimeOfferDisposition,
                PersistenceReceiptId = receipt.PersistenceReceiptId,
                ActiveRoundJson = snapshot.ActiveRoundJson,
            };
        }

        private static bool IsSerializedEmptySettlementOutbox(
            SettlementOutboxRecord record)
        {
            if (record == null) return false;
            return record.State == (int)BsSettlementOutboxState.Empty
                   && record.OperationId == 0L
                   && string.IsNullOrEmpty(record.AttemptId)
                   && record.CampaignSlot == -1
                   && record.Revision == 0L
                   && record.BoardRevision == 0
                   && record.Completion == 0
                   && record.Cause == 0
                   && record.NextUnlockedOnWin == -1
                   && record.TimeOfferId == 0L
                   && record.TimeOfferDisposition == 0
                   && string.IsNullOrEmpty(record.PersistenceReceiptId)
                   && string.IsNullOrEmpty(record.ActiveRoundJson);
        }

        private static bool HasOwnedSettlementOutbox(
            SettlementOutboxRecord record) =>
            record != null && !IsSerializedEmptySettlementOutbox(record);

        private static void CanonicalizeSerializedState(ProgressData target)
        {
            if (target == null) return;
            if (IsSerializedEmptySettlementOutbox(target.SettlementOutbox))
                target.SettlementOutbox = null;
            target.DailyOrders = target.DailyOrders ?? new DailyOrdersRecord();
            target.DailyActivityFences = target.DailyActivityFences
                ?? new List<DailyActivityFenceRecord>();
            target.UnlockedRecipes = target.UnlockedRecipes ?? new List<string>();
            var seenRecipes = new HashSet<string>(StringComparer.Ordinal);
            target.UnlockedRecipes.RemoveAll(key =>
                !BartenderRecipeKey.TryDecode(key, out _) || !seenRecipes.Add(key));
            for (int i = 0; i < target.DailyActivityFences.Count; i++)
            {
                DailyActivityFenceRecord fence = target.DailyActivityFences[i];
                if (fence == null) continue;
                fence.AttemptId = fence.AttemptId ?? string.Empty;
                fence.RecipeKey = fence.RecipeKey ?? string.Empty;
            }
        }

        private static SettlementRecord CaptureSettlementRecord(
            BsSettlementDraft draft,
            BsSettlementReceipt receipt) => new SettlementRecord
        {
            AttemptId = draft.AttemptId.Value,
            Kind = (int)ToSettlementKind(draft.Completion),
            CampaignSlot = draft.CampaignSlot,
            NextUnlockedOnWin = draft.NextUnlockedOnWin,
            OperationId = draft.OperationId.Value,
            Revision = draft.Revision,
            BoardRevision = draft.BoardRevision,
            Cause = (int)draft.Cause,
            TimeOfferId = draft.TimeOfferId.Value,
            TimeOfferDisposition = (int)draft.TimeOfferDisposition,
            PersistenceReceiptId = receipt.PersistenceReceiptId,
        };

        private static bool TryDecodeSettlementOutbox(
            ProgressData source,
            out BsSettlementOutboxSnapshot snapshot,
            out BsSettlementRestoreEvidence evidence,
            out string rejectionReason)
        {
            snapshot = default;
            evidence = BsSettlementRestoreEvidence.None;
            rejectionReason = null;
            SettlementOutboxRecord record = source?.SettlementOutbox;
            if (!HasOwnedSettlementOutbox(record))
            {
                rejectionReason = "The settlement outbox is empty";
                return false;
            }
            if (record.OperationId == long.MaxValue)
            {
                rejectionReason = "The saved settlement operation space is exhausted";
                return false;
            }

            var request = new BsSettlementRequest(
                new BsAttemptId(record.AttemptId),
                new BsOperationId(record.OperationId),
                record.Revision,
                record.BoardRevision,
                (BsRoundCompletion)record.Completion,
                (BsRoundTransitionCause)record.Cause);
            var draft = new BsSettlementDraft(
                request,
                record.CampaignSlot,
                record.NextUnlockedOnWin,
                new BsTimeOfferId(record.TimeOfferId));
            BsSettlementReceipt receipt = BsSettlementReceipt.Durable(
                request, record.PersistenceReceiptId);
            if (record.TimeOfferDisposition != (int)draft.TimeOfferDisposition
                || !string.Equals(record.PersistenceReceiptId,
                    BuildSettlementPersistenceReceiptId(request),
                    StringComparison.Ordinal))
            {
                rejectionReason = "The saved settlement correlation is invalid";
                return false;
            }
            BsSettlementOutboxState state =
                (BsSettlementOutboxState)record.State;
            snapshot = new BsSettlementOutboxSnapshot(
                state,
                draft,
                receipt,
                Math.Max(source.LastSettlementOperationId, record.OperationId),
                record.ActiveRoundJson);
            if (!snapshot.IsValid)
            {
                rejectionReason = "The saved settlement outbox is invalid";
                return false;
            }

            SettlementRecord settled = FindSettlementMatch(source, draft, receipt);
            if (settled != null)
            {
                // A saved accounting record proves the result committed, even if the outbox state is stale. Do
                // not apply it again.
                snapshot = new BsSettlementOutboxSnapshot(
                    BsSettlementOutboxState.Committed,
                    draft,
                    receipt,
                    Math.Max(source.LastSettlementOperationId, record.OperationId),
                    record.ActiveRoundJson);
                evidence = BsSettlementRestoreEvidence.MatchingCommittedRecord;
                return true;
            }
            if (FindSettlement(source, draft.AttemptId.Value) != null)
            {
                rejectionReason =
                    "The saved settlement conflicts with settlement history";
                return false;
            }

            if (!string.Equals(source.ActiveAttemptId, draft.AttemptId.Value,
                    StringComparison.Ordinal)
                || source.ActiveAttemptCampaignSlot != draft.CampaignSlot
                || !string.Equals(source.ActiveRoundJson, record.ActiveRoundJson,
                    StringComparison.Ordinal)
                || !TryReadActiveRoundHeader(record.ActiveRoundJson,
                    out ActiveRoundHeader header)
                || !string.Equals(header.AttemptId, draft.AttemptId.Value,
                    StringComparison.Ordinal)
                || header.DomainRevision != draft.Revision
                || header.LastOperationId != draft.OperationId.Value
                || header.BoardRevision != draft.BoardRevision)
            {
                rejectionReason =
                    "The saved settlement outbox does not own the active attempt";
                return false;
            }
            if (state == BsSettlementOutboxState.Committed
                || state == BsSettlementOutboxState.Acknowledged)
            {
                rejectionReason =
                    "A committed settlement has no matching settlement record";
                return false;
            }

            evidence = BsSettlementRestoreEvidence.MatchingActiveAttempt;
            return true;
        }

        private static bool SettlementMatches(
            SettlementRecord record,
            BsSettlementDraft draft,
            BsSettlementReceipt receipt)
        {
            if (record == null || !receipt.IsValid || record.OperationId <= 0L)
                return false;
            return record.OperationId == draft.OperationId.Value
                   && string.Equals(record.AttemptId, draft.AttemptId.Value,
                       StringComparison.Ordinal)
                   && record.Kind == (int)ToSettlementKind(draft.Completion)
                   && record.CampaignSlot == draft.CampaignSlot
                   && record.NextUnlockedOnWin == draft.NextUnlockedOnWin
                   && record.Revision == draft.Revision
                   && record.BoardRevision == draft.BoardRevision
                   && record.Cause == (int)draft.Cause
                   && record.TimeOfferId == draft.TimeOfferId.Value
                   && record.TimeOfferDisposition ==
                       (int)draft.TimeOfferDisposition
                   && string.Equals(record.PersistenceReceiptId,
                       receipt.PersistenceReceiptId, StringComparison.Ordinal);
        }

        private static bool TryGetTrustedSettlementOperationId(
            SettlementRecord record,
            out long operationId)
        {
            operationId = 0L;
            if (record == null
                || record.OperationId <= 0L
                || record.OperationId == long.MaxValue)
                return false;

            BsRoundCompletion completion;
            switch ((BartenderSettlementKind)record.Kind)
            {
                case BartenderSettlementKind.Won:
                    completion = BsRoundCompletion.Won;
                    break;
                case BartenderSettlementKind.Failed:
                    completion = BsRoundCompletion.Failed;
                    break;
                case BartenderSettlementKind.Abandoned:
                    completion = BsRoundCompletion.Quit;
                    break;
                default:
                    return false;
            }

            var request = new BsSettlementRequest(
                new BsAttemptId(record.AttemptId),
                new BsOperationId(record.OperationId),
                record.Revision,
                record.BoardRevision,
                completion,
                (BsRoundTransitionCause)record.Cause);
            var draft = new BsSettlementDraft(
                request,
                record.CampaignSlot,
                record.NextUnlockedOnWin,
                new BsTimeOfferId(record.TimeOfferId));
            var receipt = BsSettlementReceipt.Durable(
                request, record.PersistenceReceiptId);
            if (!draft.IsValid
                || !receipt.IsValid
                || record.TimeOfferDisposition != (int)draft.TimeOfferDisposition
                || !string.Equals(
                    record.PersistenceReceiptId,
                    BuildSettlementPersistenceReceiptId(request),
                    StringComparison.Ordinal)
                || !SettlementMatches(record, draft, receipt))
                return false;

            operationId = record.OperationId;
            return true;
        }

        private static bool SettlementReceiptMatches(
            SettlementRecord record,
            BsSettlementReceipt receipt)
        {
            if (record == null || !receipt.IsValid || !receipt.IsDurable
                || record.OperationId <= 0L)
                return false;
            BsSettlementRequest request = receipt.Request;
            return record.OperationId == request.OperationId.Value
                   && string.Equals(record.AttemptId, request.AttemptId.Value,
                       StringComparison.Ordinal)
                   && record.Kind
                       == (int)ToSettlementKind(request.Completion)
                   && record.Revision == request.Revision
                   && record.BoardRevision == request.BoardRevision
                   && record.Cause == (int)request.Cause
                   && string.Equals(record.PersistenceReceiptId,
                       receipt.PersistenceReceiptId, StringComparison.Ordinal);
        }

        private static string BuildSettlementPersistenceReceiptId(
            BsSettlementRequest request) =>
            "settlement:" + request.AttemptId.Value + ":"
            + request.OperationId.Value.ToString(CultureInfo.InvariantCulture);

        private static BartenderSettlementKind ToSettlementKind(
            BsRoundCompletion completion)
        {
            switch (completion)
            {
                case BsRoundCompletion.Won:
                    return BartenderSettlementKind.Won;
                case BsRoundCompletion.Failed:
                    return BartenderSettlementKind.Failed;
                case BsRoundCompletion.Quit:
                    return BartenderSettlementKind.Abandoned;
                default:
                    return 0;
            }
        }

        private static bool TryReadActiveRoundHeader(
            string json, out ActiveRoundHeader header)
        {
            header = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            string trimmed = json.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '{'
                || trimmed[trimmed.Length - 1] != '}') return false;
            try
            {
                header = JsonUtility.FromJson<ActiveRoundHeader>(trimmed);
                return header != null && header.Version == ActiveRoundVersion
                       && !string.IsNullOrWhiteSpace(header.LevelSignature);
            }
            catch
            {
                header = null;
                return false;
            }
        }

        private static SettlementRecord FindSettlement(string attemptId)
            => FindSettlement(data, attemptId);

        private static SettlementRecord FindSettlement(ProgressData source,
                                                       string attemptId)
        {
            if (source?.Settlements == null) return null;
            for (int i = source.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = source.Settlements[i];
                if (string.Equals(record.AttemptId, attemptId, StringComparison.Ordinal))
                    return record;
            }
            return null;
        }

        private static SettlementRecord FindSettlementMatch(
            ProgressData source,
            BsSettlementDraft draft,
            BsSettlementReceipt receipt)
        {
            if (source?.Settlements == null) return null;
            for (int i = source.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = source.Settlements[i];
                if (SettlementMatches(record, draft, receipt)) return record;
            }
            return null;
        }

        private static SettlementRecord FindSettlementReceiptMatch(
            ProgressData source,
            BsSettlementReceipt receipt)
        {
            if (source?.Settlements == null) return null;
            for (int i = source.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = source.Settlements[i];
                if (SettlementReceiptMatches(record, receipt)) return record;
            }
            return null;
        }

        private static SettlementRecord FindRecoveryTombstone(
            ProgressData source,
            string attemptId,
            int campaignSlot)
        {
            if (source?.Settlements == null) return null;
            for (int i = source.Settlements.Count - 1; i >= 0; i--)
            {
                SettlementRecord record = source.Settlements[i];
                if (record != null
                    && string.Equals(record.AttemptId, attemptId,
                        StringComparison.Ordinal)
                    && record.CampaignSlot == campaignSlot
                    && record.Kind == (int)BartenderSettlementKind.Abandoned
                    && record.NextUnlockedOnWin == -1
                    && record.OperationId == 0L
                    && record.Revision == 0L
                    && record.BoardRevision == 0
                    && record.Cause == 0
                    && record.TimeOfferId == 0L
                    && record.TimeOfferDisposition == 0
                    && string.IsNullOrEmpty(record.PersistenceReceiptId))
                    return record;
            }
            return null;
        }

        private static bool ConsumeLife(ProgressData target, long nowTicks)
        {
            if (target == null || target.Lives <= 0) return false;
            bool wasFull = target.Lives >= MaxLives;
            target.Lives--;
            if (wasFull || target.NextLifeUtcTicks <= nowTicks)
                target.NextLifeUtcTicks = SafeAddTicks(nowTicks,
                    LifeRegenerationInterval.Ticks);
            return true;
        }

        private static bool CreditCoinsSaturating(ProgressData target, int reward)
        {
            if (target == null || reward <= 0) return false;
            int previous = target.Coins;
            target.Coins = (int)Math.Min(
                int.MaxValue, (long)target.Coins + reward);
            return target.Coins != previous;
        }

        private static bool HasActiveAttempt(ProgressData source) =>
            source != null && !string.IsNullOrWhiteSpace(source.ActiveAttemptId)
            && source.ActiveAttemptCampaignSlot >= 0;

        private static bool IsValidActiveRoundJson(string json) =>
            TryReadActiveRoundHeader(json, out _);

        private static bool TryCreateRecoveryQuarantine(
            ProgressData source,
            string expectedAttemptId,
            int expectedCampaignSlot,
            string expectedActiveRoundJson,
            bool clearSettlementOutbox,
            out ProgressData candidate)
        {
            candidate = null;
            if (!HasActiveAttempt(source)
                || !string.Equals(source.ActiveAttemptId, expectedAttemptId,
                    StringComparison.Ordinal)
                || source.ActiveAttemptCampaignSlot != expectedCampaignSlot
                || !string.Equals(source.ActiveRoundJson, expectedActiveRoundJson,
                    StringComparison.Ordinal))
                return false;

            candidate = Clone(source);
            if (FindRecoveryTombstone(
                    candidate, expectedAttemptId, expectedCampaignSlot) == null)
            {
                candidate.Settlements.Add(new SettlementRecord
                {
                    AttemptId = expectedAttemptId,
                    Kind = (int)BartenderSettlementKind.Abandoned,
                    CampaignSlot = expectedCampaignSlot,
                    NextUnlockedOnWin = -1,
                });
                TrimSettlementHistory(candidate.Settlements);
            }

            candidate.ActiveAttemptId = string.Empty;
            candidate.ActiveAttemptCampaignSlot = -1;
            candidate.ActiveRoundJson = string.Empty;
            if (clearSettlementOutbox) candidate.SettlementOutbox = null;
            return true;
        }

        private static bool TryCreateInterruptedAttemptSettlement(
            ProgressData source, out ProgressData candidate)
        {
            candidate = null;
            if (!HasActiveAttempt(source)
                || HasOwnedSettlementOutbox(source.SettlementOutbox))
                return false;

            return TryCreateRecoveryQuarantine(source, source.ActiveAttemptId,
                source.ActiveAttemptCampaignSlot, source.ActiveRoundJson, false,
                out candidate);
        }

        private static ProgressData TryLoadResolvedInterruptedAttempt(
            string attemptId, long nowTicks)
        {
            ProgressData persisted = TryLoad(SavePath);
            if (persisted == null) return null;
            Normalize(persisted, nowTicks);
            if (string.Equals(persisted.ActiveAttemptId, attemptId,
                    StringComparison.Ordinal)
                || FindSettlement(persisted, attemptId) == null)
                return null;
            return persisted;
        }

        private static void RetryInterruptedAttemptSettlement(long nowTicks)
        {
            if (!interruptedAttemptSettlementPending
                || BartenderDailyClock.UtcNowTicks < nextRefreshPersistenceRetryUtcTicks)
                return;

            if (!string.Equals(data.ActiveAttemptId, pendingInterruptedAttemptId,
                    StringComparison.Ordinal)
                || data.ActiveAttemptCampaignSlot != pendingInterruptedCampaignSlot)
            {
                ClearInterruptedAttemptSettlementPending();
                return;
            }

            ProgressData refreshed = Clone(data);
            ReconcileLives(refreshed, nowTicks);
            if (!TryCreateInterruptedAttemptSettlement(refreshed,
                    out ProgressData candidate))
            {
                if (HasActiveAttempt(refreshed)
                    && !HasOwnedSettlementOutbox(refreshed.SettlementOutbox))
                {
                    data = refreshed;
                    nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(BartenderDailyClock.UtcNowTicks,
                        RefreshRetryIntervalTicks);
                    return;
                }
                ClearInterruptedAttemptSettlementPending();
                return;
            }

            bool livesChanged = candidate.Lives != data.Lives;
            if (Commit(candidate, false, livesChanged, false, out _))
            {
                ClearInterruptedAttemptSettlementPending();
                return;
            }

            ProgressData persisted = TryLoadResolvedInterruptedAttempt(
                pendingInterruptedAttemptId, nowTicks);
            if (persisted != null)
            {
                int previousLives = data.Lives;
                data = persisted;
                persistenceDirty = false;
                ClearInterruptedAttemptSettlementPending();
                if (data.Lives != previousLives)
                    InvokeSafely(LivesChanged, data.Lives);
                PublishLifeTimerIfChanged(nowTicks);
                return;
            }

            nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(BartenderDailyClock.UtcNowTicks,
                RefreshRetryIntervalTicks);
        }

        private static void ClearInterruptedAttemptSettlementPending()
        {
            interruptedAttemptSettlementPending = false;
            pendingInterruptedAttemptId = string.Empty;
            pendingInterruptedCampaignSlot = -1;
            nextRefreshPersistenceRetryUtcTicks = 0L;
        }

        private static void ArmInterruptedAttemptSettlement(string attemptId,
                                                            int campaignSlot,
                                                            long nowTicks)
        {
            interruptedAttemptSettlementPending = true;
            pendingInterruptedAttemptId = attemptId;
            pendingInterruptedCampaignSlot = campaignSlot;
            nextRefreshPersistenceRetryUtcTicks = SafeAddTicks(BartenderDailyClock.UtcNowTicks,
                RefreshRetryIntervalTicks);
        }

        private static void NormalizeDailyActivityFences(
            List<DailyActivityFenceRecord> records, string activeAttemptId)
        {
            if (records == null) return;
            for (int i = records.Count - 1; i >= 0; i--)
            {
                DailyActivityFenceRecord record = records[i];
                bool poured = record != null
                    && record.Kind == (int)BartenderDailyActivityKind.PouredUnits
                    && record.Amount > 0;
                bool delivered = record != null
                    && record.Kind == (int)BartenderDailyActivityKind.DeliveredOrder
                    && record.Amount == 1;
                bool recipe = record != null
                    && record.Kind == (int)BartenderDailyActivityKind.DeliveredRecipe
                    && record.OrderIndex >= 0
                    && BartenderRecipeKey.TryDecode(record.RecipeKey, out OrderDef order)
                    && record.Amount == order.Contents.Count;
                if (record == null
                    || string.IsNullOrWhiteSpace(record.AttemptId)
                    || record.OperationId <= 0L
                    || record.DomainRevision <= 0L
                    || record.BoardRevision < 0
                    || (!poured && !delivered && !recipe))
                    records.RemoveAt(i);
            }
            TrimDailyActivityFences(records, activeAttemptId);
        }

        private static void TrimDailyActivityFences(
            List<DailyActivityFenceRecord> records, string activeAttemptId)
        {
            if (records == null) return;
            while (records.Count > DailyActivityFenceLimit)
            {
                int index = records.FindIndex(record => record == null
                    || record.Kind != (int)BartenderDailyActivityKind.DeliveredRecipe
                    || !string.Equals(record.AttemptId, activeAttemptId, StringComparison.Ordinal));
                if (index < 0) break;
                records.RemoveAt(index);
            }
        }

        private static void TrimSettlementHistory(List<SettlementRecord> records,
                                                  string preserveAttemptId = null)
        {
            while (records.Count > SettlementHistoryLimit)
            {
                int removeIndex = 0;
                if (!string.IsNullOrWhiteSpace(preserveAttemptId))
                {
                    removeIndex = records.FindIndex(record => record == null
                        || !string.Equals(record.AttemptId, preserveAttemptId,
                            StringComparison.Ordinal));
                    if (removeIndex < 0) removeIndex = 0;
                }
                records.RemoveAt(removeIndex);
            }
        }

        private static long SafeAddTicks(long value, long ticks)
        {
            if (ticks > 0L && value > DateTime.MaxValue.Ticks - ticks)
                return DateTime.MaxValue.Ticks;
            if (ticks < 0L && value < DateTime.MinValue.Ticks - ticks)
                return DateTime.MinValue.Ticks;
            return value + ticks;
        }

        private static void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            Delegate[] subscribers = handlers.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try { ((Action<T>)subscribers[i]).Invoke(value); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }
    }

    [DisallowMultipleComponent]
    internal sealed class BartenderProgressRuntimeDriver : MonoBehaviour
    {
        private static BartenderProgressRuntimeDriver instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            BartenderProgressRuntimeDriver existing = instance != null
                ? instance
                : FindFirstObjectByType<BartenderProgressRuntimeDriver>(
                    FindObjectsInactive.Include);
            if (existing != null && existing.gameObject.activeInHierarchy
                && existing.transform.parent == null)
            {
                instance = existing;
                DontDestroyOnLoad(existing.gameObject);
                existing.enabled = true;
                BartenderProgressService.Refresh();
                return;
            }

            if (existing != null)
            {
                existing.enabled = false;
                Destroy(existing);
            }
            instance = null;
            var host = new GameObject("Bartender Progress Runtime");
            host.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(host);
            instance = host.AddComponent<BartenderProgressRuntimeDriver>();
            BartenderProgressService.Refresh();
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                enabled = false;
                Destroy(this);
                return;
            }
            instance = this;
        }

        internal static void ResetRegistration() => instance = null;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void Update()
        {
            if (instance != this) return;
            BartenderProgressService.PumpPendingWrite();
            BartenderProgressService.Refresh();
        }

        private static void HandleSceneLoaded(Scene _, LoadSceneMode __) =>
            BartenderProgressService.Refresh();

        private void OnApplicationPause(bool paused) =>
            BartenderProgressService.SynchronizeDailyClock(paused);

        private void OnApplicationFocus(bool focused) =>
            BartenderProgressService.SynchronizeDailyClock(!focused);

        private void OnApplicationQuit() => BartenderProgressService.SynchronizeDailyClock(true);

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
