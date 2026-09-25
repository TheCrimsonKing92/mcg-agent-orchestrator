using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GateLoadSample
{
    private GateLoadSample(bool isAvailable, double? value, string? unavailableReason)
    {
        IsAvailable = isAvailable;
        Value = value;
        UnavailableReason = unavailableReason;
    }

    public bool IsAvailable { get; }

    public double? Value { get; }

    public string? UnavailableReason { get; }

    public static GateLoadSample Available(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Available load samples must be finite.");
        }

        return new(true, value, null);
    }

    public static GateLoadSample Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, null, reason);
    }
}

/// <summary>
/// Load observed when a lane receipt is emitted. ConcurrentGateCount counts distinct live gate heartbeats and
/// includes the calling gate. InFlightPaidWorkerDispatchCount remains explicitly unavailable until the
/// conductor supplies its authoritative cross-process value. ConcurrentShardCount is local to this gate.
/// </summary>
public sealed record GateLoadContext(
    GateLoadSample ConcurrentGateCount,
    GateLoadSample InFlightPaidWorkerDispatchCount,
    GateLoadSample ConcurrentShardCount,
    GateLoadSample HostCpuUtilizationPercent,
    GateLoadSample ProcessorCount,
    GateLoadSample CpuSampleWindowMilliseconds);

internal sealed class GateShardConcurrencyCounter
{
    private int _count;
    private int _peak;

    public int Count => Volatile.Read(ref _count);
    public int Peak => Volatile.Read(ref _peak);

    public IDisposable Enter()
    {
        var count = Interlocked.Increment(ref _count);
        var observed = Volatile.Read(ref _peak);
        while (count > observed)
        {
            var prior = Interlocked.CompareExchange(ref _peak, count, observed);
            if (prior == observed)
                break;
            observed = prior;
        }
        return new Scope(this);
    }

    private sealed class Scope(GateShardConcurrencyCounter owner) : IDisposable
    {
        private GateShardConcurrencyCounter? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._count);
            }
        }
    }
}

internal static class GateLoadContextProbe
{
    private const string CrossProcessCounterUnavailable = "cross-process-counter-unavailable";
    private const double MinimumCpuSampleWindowMilliseconds = 200;
    internal static readonly TimeSpan LiveGateHeartbeatFreshness = TimeSpan.FromMinutes(2);
    private static readonly object CpuSync = new();
    private static readonly AsyncLocal<Func<int?>?> ConcurrentGateCountProbeOverride = new();
    private static readonly AsyncLocal<Func<IReadOnlyList<LiveGateOccupant>>?> LiveGateOccupantProbeOverride = new();
    private static readonly AsyncLocal<Func<int?>?> InFlightWorkerDispatchProbeOverride = new();
    private static readonly AsyncLocal<Func<HostCpuSample>?> HostCpuProbeOverride = new();
    private static CpuTimes? _previousCpuTimes;

    internal static void Initialize()
    {
        try
        {
            _previousCpuTimes = ReadCpuTimes();
        }
        catch
        {
            _previousCpuTimes = null;
        }
    }

    public static GateLoadContext Capture(int concurrentShardCount)
    {
        var concurrentShards = concurrentShardCount >= 0
            ? GateLoadSample.Available(concurrentShardCount)
            : GateLoadSample.Unavailable("invalid-concurrent-shard-count");
        var concurrentGates = CaptureCount(
            ConcurrentGateCountProbeOverride.Value ?? CaptureConcurrentGateCount,
            minimumValue: 1,
            "gate-count-unavailable");
        var inFlightWorkers = CaptureCount(
            InFlightWorkerDispatchProbeOverride.Value ?? CaptureUnavailableWorkerDispatchCount,
            minimumValue: 0,
            CrossProcessCounterUnavailable);
        try
        {
            var cpu = (HostCpuProbeOverride.Value ?? CaptureHostCpu)();
            if (!double.IsFinite(cpu.UtilizationPercent) ||
                cpu.UtilizationPercent is < 0 or > 100 ||
                cpu.ProcessorCount <= 0 ||
                !double.IsFinite(cpu.WindowMilliseconds) ||
                cpu.WindowMilliseconds < MinimumCpuSampleWindowMilliseconds)
            {
                throw new LoadProbeUnavailableException("cpu-sample-invalid");
            }

            return new GateLoadContext(
                concurrentGates,
                inFlightWorkers,
                concurrentShards,
                GateLoadSample.Available(cpu.UtilizationPercent),
                GateLoadSample.Available(cpu.ProcessorCount),
                GateLoadSample.Available(cpu.WindowMilliseconds));
        }
        catch (LoadProbeUnavailableException exception)
        {
            return WithUnavailableCpu(concurrentGates, inFlightWorkers, concurrentShards, exception.Reason);
        }
        catch (Exception exception)
        {
            return WithUnavailableCpu(
                concurrentGates,
                inFlightWorkers,
                concurrentShards,
                $"probe-error:{exception.GetType().Name}");
        }
    }

    public static string FormatProgressTokens(GateLoadContext? context) =>
        $"load_concurrent_gates={Format(context?.ConcurrentGateCount)} " +
        $"load_inflight_paid_workers={Format(context?.InFlightPaidWorkerDispatchCount)} " +
        $"load_concurrent_shards={Format(context?.ConcurrentShardCount)} " +
        $"load_cpu_percent={Format(context?.HostCpuUtilizationPercent)} " +
        $"load_processor_count={Format(context?.ProcessorCount)} " +
        $"load_cpu_window_ms={Format(context?.CpuSampleWindowMilliseconds)}";

    internal static IDisposable PushHostCpuProbe(Func<HostCpuSample> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var previous = HostCpuProbeOverride.Value;
        HostCpuProbeOverride.Value = probe;
        return new RestoreAction(() => HostCpuProbeOverride.Value = previous);
    }

    internal static IDisposable PushConcurrentGateCountProbe(Func<int?> probe) =>
        PushCountProbe(ConcurrentGateCountProbeOverride, probe);

    internal static IDisposable PushLiveGateOccupantProbe(Func<IReadOnlyList<LiveGateOccupant>> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var previous = LiveGateOccupantProbeOverride.Value;
        LiveGateOccupantProbeOverride.Value = probe;
        return new RestoreAction(() => LiveGateOccupantProbeOverride.Value = previous);
    }

    internal static IDisposable PushInFlightWorkerDispatchProbe(Func<int?> probe) =>
        PushCountProbe(InFlightWorkerDispatchProbeOverride, probe);

    private static IDisposable PushCountProbe(AsyncLocal<Func<int?>?> target, Func<int?> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var previous = target.Value;
        target.Value = probe;
        return new RestoreAction(() => target.Value = previous);
    }

    private static GateLoadContext WithUnavailableCpu(
        GateLoadSample concurrentGates,
        GateLoadSample inFlightWorkers,
        GateLoadSample concurrentShards,
        string reason)
    {
        var unavailable = GateLoadSample.Unavailable(
            string.IsNullOrWhiteSpace(reason) ? "probe-unavailable" : reason);
        return new GateLoadContext(
            concurrentGates,
            inFlightWorkers,
            concurrentShards,
            unavailable,
            unavailable,
            unavailable);
    }

    private static GateLoadSample CaptureCount(Func<int?> probe, int minimumValue, string unavailableReason)
    {
        try
        {
            var value = probe();
            return value is null
                ? GateLoadSample.Unavailable(unavailableReason)
                : value >= minimumValue
                    ? GateLoadSample.Available(value.Value)
                    : GateLoadSample.Unavailable("count-out-of-range");
        }
        catch (LoadProbeUnavailableException exception)
        {
            return GateLoadSample.Unavailable(exception.Reason);
        }
        catch (Exception exception)
        {
            return GateLoadSample.Unavailable($"probe-error:{exception.GetType().Name}");
        }
    }

    private static int? CaptureUnavailableWorkerDispatchCount() => null;

    internal static IReadOnlyList<LiveGateOccupant> CaptureLiveGateOccupants()
    {
        var observed = (LiveGateOccupantProbeOverride.Value ?? ReadLiveGateOccupants)();
        return observed
            .Where(occupant => occupant.HeartbeatAge <= LiveGateHeartbeatFreshness)
            .GroupBy(occupant => occupant.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(occupant => occupant.CountsAsAcceptanceOccupant) ?? group.First())
            .OrderBy(occupant => occupant.Identity, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<LiveGateOccupant> ReadLiveGateOccupants()
    {
        var heartbeatDirectory = Path.GetDirectoryName(GateHeartbeatArtifacts.GetStableSlotPath(0));
        if (string.IsNullOrWhiteSpace(heartbeatDirectory))
        {
            throw new LoadProbeUnavailableException("gate-heartbeat-directory-unavailable");
        }

        if (!Directory.Exists(heartbeatDirectory))
        {
            return [];
        }

        var occupants = new List<LiveGateOccupant>();
        foreach (var status in GateHeartbeatArtifacts.ReadStableSlots())
        {
            var snapshot = status.Snapshot;
            if (!status.IsAvailable ||
                snapshot is null ||
                !string.Equals(snapshot.State, "running", StringComparison.OrdinalIgnoreCase) ||
                status.HeartbeatAge is not { } heartbeatAge)
            {
                continue;
            }

            occupants.Add(new LiveGateOccupant(
                snapshot.ProcessId,
                snapshot.GoalId,
                status.SlotIndex,
                heartbeatAge,
                status.Path,
                snapshot.RunClass));
        }

        return occupants;
    }

    private static int? CaptureConcurrentGateCount()
    {
        var otherGateCount = CaptureLiveGateOccupants()
            .Where(occupant => occupant.ProcessId != Environment.ProcessId)
            .Select(occupant => occupant.Identity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return otherGateCount + 1;
    }

    private static string Format(GateLoadSample? sample) =>
        sample is { IsAvailable: true, Value: not null }
            ? sample.Value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "unavailable";

    private static HostCpuSample CaptureHostCpu()
    {
        var current = ReadCpuTimes();
        lock (CpuSync)
        {
            var previous = _previousCpuTimes;
            _previousCpuTimes = current;
            if (previous is null)
            {
                throw new LoadProbeUnavailableException("cpu-baseline-unavailable");
            }

            if (current.Idle < previous.Value.Idle ||
                current.Kernel < previous.Value.Kernel ||
                current.User < previous.Value.User)
            {
                throw new LoadProbeUnavailableException("cpu-counter-regressed");
            }

            var idle = current.Idle - previous.Value.Idle;
            var kernel = current.Kernel - previous.Value.Kernel;
            var user = current.User - previous.Value.User;
            var total = kernel + user;
            var elapsedTicks = current.Timestamp - previous.Value.Timestamp;
            if (total == 0 || idle > total || elapsedTicks <= 0)
            {
                throw new LoadProbeUnavailableException("cpu-sample-window-invalid");
            }

            var utilization = (total - idle) * 100d / total;
            var windowMilliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
            return new HostCpuSample(utilization, Environment.ProcessorCount, windowMilliseconds);
        }
    }

    private static CpuTimes ReadCpuTimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new LoadProbeUnavailableException("unsupported-platform");
        }

        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new LoadProbeUnavailableException("getsystemtimes-failed");
        }

        return new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64(), Stopwatch.GetTimestamp());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User, long Timestamp);

    internal readonly record struct HostCpuSample(double UtilizationPercent, int ProcessorCount, double WindowMilliseconds);

    internal sealed record LiveGateOccupant(
        int? ProcessId,
        string? GoalId,
        int SlotIndex,
        TimeSpan HeartbeatAge,
        string SourcePath = "",
        string? RunClass = null)
    {
        internal bool CountsAsAcceptanceOccupant => GateHeartbeatRunClass.CountsAsAcceptanceOccupant(RunClass);
        internal string Identity => ProcessId is > 0
            ? $"pid:{ProcessId.Value}"
            : $"goal:{GoalId ?? SourcePath}";
    }

    internal sealed class LoadProbeUnavailableException(string reason) : Exception(reason)
    {
        public string Reason { get; } = string.IsNullOrWhiteSpace(reason) ? "probe-unavailable" : reason;
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

internal static class GateLoadContextModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => GateLoadContextProbe.Initialize();
}
