using System.Collections.Generic;
using BartenderSort.Core;

namespace LiquidSort.Levels
{
    internal enum BartenderCommandIntentKind
    {
        Pour,
        Delivery,
    }

    internal sealed class BartenderCommandIntent
    {
        public BartenderCommandIntentKind Kind;
        public int SourceId = -1;
        public int TargetId = -1;
        public int GlassId = -1;
        public long Sequence;
        public BsAttemptId AttemptId;
        public BsRoundToken Token;
        public int Generation;
        public double TapClock;
        public double TapRealtime;

        public static BartenderCommandIntent Pour(int sourceId, int targetId) =>
            new BartenderCommandIntent
            {
                Kind = BartenderCommandIntentKind.Pour,
                SourceId = sourceId,
                TargetId = targetId,
            };

        public static BartenderCommandIntent Delivery(int glassId) =>
            new BartenderCommandIntent
            {
                Kind = BartenderCommandIntentKind.Delivery,
                GlassId = glassId,
            };

        public bool Reserves(int glassId) => glassId >= 0
            && (Kind == BartenderCommandIntentKind.Pour
                ? SourceId == glassId || TargetId == glassId
                : GlassId == glassId);
    }

    internal sealed class BartenderCommandLane
    {
        internal const int Capacity = 4;

        private readonly List<BartenderCommandIntent> queued =
            new List<BartenderCommandIntent>(Capacity);
        private long nextSequence;

        public BartenderCommandIntent RunningIntent { get; private set; }
        public bool Running => RunningIntent != null;
        public int Count => queued.Count;
        public bool IsFull => queued.Count >= Capacity;
        public bool HasPending => queued.Count > 0 || RunningIntent != null;

        public double? EarliestTapClock
        {
            get
            {
                double? earliest = null;
                for (int i = 0; i < queued.Count; i++)
                    if (!earliest.HasValue || queued[i].TapClock < earliest.Value)
                        earliest = queued[i].TapClock;
                return earliest;
            }
        }

        public bool Reserves(int glassId)
        {
            if (glassId < 0) return false;
            if (RunningIntent != null && RunningIntent.Reserves(glassId)) return true;
            for (int i = 0; i < queued.Count; i++)
                if (queued[i].Reserves(glassId)) return true;
            return false;
        }

        public bool TryEnqueue(BartenderCommandIntent intent)
        {
            if (intent == null || queued.Count >= Capacity) return false;
            if (intent.Kind == BartenderCommandIntentKind.Pour
                ? intent.SourceId < 0 || intent.TargetId < 0 || intent.SourceId == intent.TargetId
                  || Reserves(intent.SourceId) || Reserves(intent.TargetId)
                : intent.GlassId < 0 || Reserves(intent.GlassId))
                return false;
            intent.Sequence = ++nextSequence;
            queued.Add(intent);
            return true;
        }

        public bool TryPeek(out BartenderCommandIntent intent)
        {
            intent = queued.Count > 0 ? queued[0] : null;
            return intent != null;
        }

        public BartenderCommandIntent Dequeue()
        {
            if (queued.Count == 0) return null;
            BartenderCommandIntent head = queued[0];
            queued.RemoveAt(0);
            return head;
        }

        public bool TryBeginRun(BartenderCommandIntent intent)
        {
            if (intent == null || RunningIntent != null) return false;
            RunningIntent = intent;
            return true;
        }

        public void EndRun(BartenderCommandIntent intent)
        {
            if (ReferenceEquals(RunningIntent, intent)) RunningIntent = null;
        }

        public void CancelQueued(List<BartenderCommandIntent> dropped = null)
        {
            if (dropped != null) dropped.AddRange(queued);
            queued.Clear();
        }
    }
}
