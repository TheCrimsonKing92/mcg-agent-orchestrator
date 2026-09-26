using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum LaunchLockEntryPoint
{
    AcquireConsoleForChildSpawn,
    AcquireSuppressedChildSpawn,
    AcquireErrorModeForChildSpawn,
    Start
}

internal readonly record struct LaunchLockSample(long WaitTicks, long HoldTicks, long CreateTicks);

internal sealed record LaunchLockSnapshot(
    long Launches,
    long WaitTicksTotal,
    long WaitP50Ticks,
    long WaitP95Ticks,
    long WaitMaxTicks,
    long HoldTicksTotal,
    long HoldP50Ticks,
    long HoldP95Ticks,
    long HoldMaxTicks,
    long CreateTicksTotal,
    long[] ByEntry);

internal sealed class LaunchLockCounters
{
    // Bucket zero is zero duration; subsequent buckets cover powers of two microseconds.
    private const int BucketCount = 48;
    private readonly long[] waitBuckets = new long[BucketCount];
    private readonly long[] holdBuckets = new long[BucketCount];
    private readonly long[] byEntry = new long[4];
    private long launches;
    private long waitTicksTotal;
    private long waitMaxTicks;
    private long holdTicksTotal;
    private long holdMaxTicks;
    private long createTicksTotal;

    internal void Record(LaunchLockEntryPoint entry, long waitTicks, long holdTicks, long createTicks)
    {
        waitTicks = Math.Max(0, waitTicks);
        holdTicks = Math.Max(0, holdTicks);
        createTicks = Math.Max(0, createTicks);
        Interlocked.Increment(ref waitBuckets[BucketFor(waitTicks)]);
        Interlocked.Increment(ref holdBuckets[BucketFor(holdTicks)]);
        Interlocked.Add(ref waitTicksTotal, waitTicks);
        Interlocked.Add(ref holdTicksTotal, holdTicks);
        Interlocked.Add(ref createTicksTotal, createTicks);
        UpdateMax(ref waitMaxTicks, waitTicks);
        UpdateMax(ref holdMaxTicks, holdTicks);
        Interlocked.Increment(ref byEntry[(int)entry]);
        Interlocked.Increment(ref launches);
    }

    internal LaunchLockSnapshot Snapshot()
    {
        var count = Interlocked.Read(ref launches);
        var entryCounts = new long[byEntry.Length];
        for (var i = 0; i < entryCounts.Length; i++)
        {
            entryCounts[i] = Interlocked.Read(ref byEntry[i]);
        }
        var waitMax = Interlocked.Read(ref waitMaxTicks);
        var holdMax = Interlocked.Read(ref holdMaxTicks);
        return new LaunchLockSnapshot(
            count,
            Interlocked.Read(ref waitTicksTotal),
            Math.Min(Percentile(waitBuckets, count, 50), waitMax),
            Math.Min(Percentile(waitBuckets, count, 95), waitMax),
            waitMax,
            Interlocked.Read(ref holdTicksTotal),
            Math.Min(Percentile(holdBuckets, count, 50), holdMax),
            Math.Min(Percentile(holdBuckets, count, 95), holdMax),
            holdMax,
            Interlocked.Read(ref createTicksTotal),
            entryCounts);
    }

    private static int BucketFor(long ticks)
    {
        if (ticks == 0) return 0;
        var microseconds = (long)Math.Ceiling(ticks * 1_000_000.0 / Stopwatch.Frequency);
        var bucket = 1;
        while (microseconds > 1 && bucket < BucketCount - 1)
        {
            microseconds = (microseconds + 1) / 2;
            bucket++;
        }
        return bucket;
    }

    private static long Percentile(long[] buckets, long count, int percentile)
    {
        if (count == 0) return 0;
        var rank = (long)Math.Ceiling(count * percentile / 100.0);
        long cumulative = 0;
        for (var bucket = 0; bucket < buckets.Length; bucket++)
        {
            cumulative += Interlocked.Read(ref buckets[bucket]);
            if (cumulative >= rank)
            {
                if (bucket == 0) return 0;
                var upperMicroseconds = 1L << Math.Min(bucket - 1, 46);
                return (long)Math.Ceiling(upperMicroseconds * Stopwatch.Frequency / 1_000_000.0);
            }
        }
        return 0;
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current) return;
            current = previous;
        }
    }
}

internal static class LaunchLockTelemetry
{
    private static readonly AsyncLocal<Capture?> ActiveCapture = new();
    internal static LaunchLockCounters Process { get; } = new();

    internal static Capture BeginCapture()
    {
        var capture = new Capture(ActiveCapture.Value);
        ActiveCapture.Value = capture;
        return capture;
    }

    internal static void Record(LaunchLockEntryPoint entry, long waitTicks, long holdTicks, long createTicks = 0)
    {
        Process.Record(entry, waitTicks, holdTicks, createTicks);
        var capture = ActiveCapture.Value;
        if (capture is not null)
        {
            capture.Counters.Record(entry, waitTicks, holdTicks, createTicks);
            capture.Samples.Enqueue(new LaunchLockSample(waitTicks, holdTicks, createTicks));
        }
    }

    internal static string FormatSummary(LaunchLockSnapshot snapshot, int processId)
    {
        static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
        var entries = Enum.GetValues<LaunchLockEntryPoint>();
        var byEntry = new StringBuilder();
        for (var i = 0; i < entries.Length; i++)
        {
            if (i > 0) byEntry.Append(',');
            byEntry.Append(entries[i]).Append(':').Append(snapshot.ByEntry[i].ToString(CultureInfo.InvariantCulture));
        }

        return $"LAUNCH_LOCK_SUMMARY pid={processId} launches={snapshot.Launches} " +
               $"wait_ms_total={Ms(snapshot.WaitTicksTotal)} wait_p50_ms={Ms(snapshot.WaitP50Ticks)} " +
               $"wait_p95_ms={Ms(snapshot.WaitP95Ticks)} wait_max_ms={Ms(snapshot.WaitMaxTicks)} " +
               $"hold_ms_total={Ms(snapshot.HoldTicksTotal)} hold_p50_ms={Ms(snapshot.HoldP50Ticks)} " +
               $"hold_p95_ms={Ms(snapshot.HoldP95Ticks)} hold_max_ms={Ms(snapshot.HoldMaxTicks)} " +
               $"create_ms_total={Ms(snapshot.CreateTicksTotal)} by_entry={byEntry}";
    }

    internal sealed class Capture(Capture? previous) : IDisposable
    {
        internal LaunchLockCounters Counters { get; } = new();
        internal ConcurrentQueue<LaunchLockSample> Samples { get; } = new();
        internal LaunchLockSnapshot Snapshot() => Counters.Snapshot();

        public void Dispose() => ActiveCapture.Value = previous;
    }
}
