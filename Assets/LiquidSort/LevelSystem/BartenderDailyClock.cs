using System;
using System.Diagnostics;

namespace LiquidSort.Levels
{
    /// <summary>
    /// A local elapsed-time clock for daily orders. Device UTC is consulted at a
    /// restart/resume boundary; live countdowns use Stopwatch, so changing the clock
    /// cannot strand an active day in the future. The persisted pair is rebased on
    /// every checkpoint, including after a device-clock rollback.
    /// </summary>
    internal static class BartenderDailyClock
    {
        private static long anchorUtc;
        private static long anchorStamp;
        private static long suspendedUtc;
        private static long suspendedDeviceUtc;
        private static bool suspended;
        private static bool initialized;
        private static readonly long MaximumTicks = DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay;

        internal static long UtcNowTicks => initialized
            ? Add(anchorUtc, ElapsedTicks(anchorStamp, Stopwatch.GetTimestamp()))
            : DateTime.UtcNow.Ticks;

        internal static void Initialize(long savedUtc, long savedDeviceUtc, long deviceUtc)
        {
            long utc = savedUtc > 0 && savedDeviceUtc > 0
                ? Add(savedUtc, Math.Max(0L, deviceUtc - savedDeviceUtc)) : deviceUtc;
            Rebase(utc);
            suspended = false;
        }

        internal static bool Suspend()
        {
            if (!initialized || suspended) return false;
            suspendedUtc = UtcNowTicks;
            suspendedDeviceUtc = DateTime.UtcNow.Ticks;
            suspended = true;
            return true;
        }

        internal static bool Resume()
        {
            if (!initialized || !suspended) return false;
            long fromDevice = Add(suspendedUtc,
                Math.Max(0L, DateTime.UtcNow.Ticks - suspendedDeviceUtc));
            Rebase(Math.Max(UtcNowTicks, fromDevice));
            suspended = false;
            return true;
        }

        internal static void Reset()
        {
            initialized = false;
            suspended = false;
            anchorUtc = anchorStamp = 0L;
        }

        private static void Rebase(long utc)
        {
            anchorUtc = Math.Max(1L, Math.Min(MaximumTicks, utc));
            anchorStamp = Stopwatch.GetTimestamp();
            initialized = true;
        }

        private static long ElapsedTicks(long start, long end) =>
            (long)Math.Min(TimeSpan.FromDays(3650d).Ticks,
                Math.Max(0d, (end - start) / (double)Stopwatch.Frequency * TimeSpan.TicksPerSecond));

        private static long Add(long utc, long elapsed)
        {
            long start = Math.Max(0L, Math.Min(MaximumTicks, utc));
            return start + Math.Min(Math.Max(0L, elapsed), MaximumTicks - start);
        }
    }
}
