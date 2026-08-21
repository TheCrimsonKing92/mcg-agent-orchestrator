using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GateLoadSample(bool IsAvailable, double? Value, string? UnavailableReason)
{
    public static GateLoadSample Available(double value) => new(true, value, null);

    public static GateLoadSample Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, null, reason);
    }
}

/// <summary>
/// Load observed when a lane receipt is emitted. ConcurrentGateCount and
/// InFlightPaidWorkerDispatchCount are cross-process values; they remain explicitly unavailable until a
/// conductor-owned bridge is added. ConcurrentShardCount is local to this gate and includes the completing shard.
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

    public int Count => Volatile.Read(ref _count);

    public IDisposable Enter()
    {
        Interlocked.Increment(ref _count);
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
    private static readonly object CpuSync = new();
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
        try
        {
            var cpu = (HostCpuProbeOverride.Value ?? CaptureHostCpu)();
            if (cpu.UtilizationPercent is < 0 or > 100 || cpu.ProcessorCount <= 0 || cpu.WindowMilliseconds <= 0)
            {
                throw new LoadProbeUnavailableException("cpu-sample-invalid");
            }

            return new GateLoadContext(
                GateLoadSample.Unavailable(CrossProcessCounterUnavailable),
                GateLoadSample.Unavailable(CrossProcessCounterUnavailable),
                concurrentShards,
                GateLoadSample.Available(cpu.UtilizationPercent),
                GateLoadSample.Available(cpu.ProcessorCount),
                GateLoadSample.Available(cpu.WindowMilliseconds));
        }
        catch (LoadProbeUnavailableException exception)
        {
            return WithUnavailableCpu(concurrentShards, exception.Reason);
        }
        catch (Exception exception)
        {
            return WithUnavailableCpu(concurrentShards, $"probe-error:{exception.GetType().Name}");
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

    private static GateLoadContext WithUnavailableCpu(GateLoadSample concurrentShards, string reason)
    {
        var unavailable = GateLoadSample.Unavailable(
            string.IsNullOrWhiteSpace(reason) ? "probe-unavailable" : reason);
        return new GateLoadContext(
            GateLoadSample.Unavailable(CrossProcessCounterUnavailable),
            GateLoadSample.Unavailable(CrossProcessCounterUnavailable),
            concurrentShards,
            unavailable,
            unavailable,
            unavailable);
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
