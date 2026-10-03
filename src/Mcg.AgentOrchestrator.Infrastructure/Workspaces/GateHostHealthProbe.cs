using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record HostHealthSample(GateLoadSample LaunchMs, GateLoadSample PagedPoolMb)
{
    internal string FormatProgressTokens() =>
        $"host_launch_ms={Format(LaunchMs)};host_paged_pool_mb={Format(PagedPoolMb)}";

    internal static string Format(GateLoadSample sample)
    {
        if (sample.IsAvailable)
            return sample.Value!.Value.ToString("0.###", CultureInfo.InvariantCulture);
        var reason = new string((sample.UnavailableReason ?? "probe-unavailable")
            .Take(80).Select(c => char.IsWhiteSpace(c) || c == ';' ? '-' : c).ToArray());
        return $"unavailable:{reason}";
    }
}

internal static class GateHostHealthProbe
{
    internal const int LaunchTimeoutMilliseconds = 10_000;
    private static readonly AsyncLocal<Func<HostHealthSample>?> Override = new();

    internal static IDisposable PushProbe(Func<HostHealthSample> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var previous = Override.Value;
        Override.Value = probe;
        return new RestoreScope(previous);
    }

    internal static HostHealthSample Capture()
    {
        try
        {
            var sample = Override.Value is { } probe
                ? probe()
                : new HostHealthSample(MeasureLaunch(), MeasurePagedPool());
            return new(Normalize(sample.LaunchMs), Normalize(sample.PagedPoolMb));
        }
        catch (Exception ex)
        {
            var unavailable = GateLoadSample.Unavailable($"probe-error:{ex.GetType().Name}");
            return new(unavailable, unavailable);
        }
    }

    private static GateLoadSample Normalize(GateLoadSample sample) =>
        sample.IsAvailable && sample.Value is not (>= 0)
            ? GateLoadSample.Unavailable("invalid-value") : sample;

    private static GateLoadSample MeasureLaunch()
    {
        if (!OperatingSystem.IsWindows())
            return GateLoadSample.Unavailable("unsupported-platform");
        try
        {
            var startInfo = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit");
            var started = Stopwatch.GetTimestamp();
            using var process = ProcessTreeGuiSuppression.Start(startInfo);
            if (!process.WaitForExit(LaunchTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return GateLoadSample.Unavailable("launch-timeout");
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return process.ExitCode == 0
                ? GateLoadSample.Available(elapsed)
                : GateLoadSample.Unavailable($"launch-exit-{process.ExitCode}");
        }
        catch (Exception ex)
        {
            return GateLoadSample.Unavailable($"launch-error:{ex.GetType().Name}");
        }
    }

    private static GateLoadSample MeasurePagedPool()
    {
        if (!OperatingSystem.IsWindows())
            return GateLoadSample.Unavailable("unsupported-platform");
        try
        {
            var info = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
            return GetPerformanceInfo(ref info, info.Size)
                ? GateLoadSample.Available((double)info.KernelPaged * (double)info.PageSize / (1024 * 1024))
                : GateLoadSample.Unavailable("getperformanceinfo-failed");
        }
        catch (Exception ex)
        {
            return GateLoadSample.Unavailable($"paged-pool-error:{ex.GetType().Name}");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public nuint SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    private sealed class RestoreScope(Func<HostHealthSample>? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Override.Value = previous;
        }
    }
}
