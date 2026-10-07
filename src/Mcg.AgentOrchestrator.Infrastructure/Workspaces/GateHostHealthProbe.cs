using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record HostHealthSample(
    GateLoadSample LaunchMs, GateLoadSample PagedPoolMb, GateLoadSample? FileCachePagedPoolMb = null)
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
    private static readonly IReadOnlyList<string> FileCachePoolTags = Array.AsReadOnly(new[]
    {
        "FMfn", "Ntff", "MmSt", "MPsc", "NtFC", "NtFs", "FIcs", "MPhc",
        "Ntfc", "NtFU", "NtfF", "MmSm", "Ntf0"
    });

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
                : new HostHealthSample(MeasureLaunch(), MeasurePagedPool(), MeasureFileCachePagedPool());
            return new(Normalize(sample.LaunchMs), Normalize(sample.PagedPoolMb),
                Normalize(sample.FileCachePagedPoolMb ?? GateLoadSample.Unavailable("not-measured")));
        }
        catch (Exception ex)
        {
            var unavailable = GateLoadSample.Unavailable($"probe-error:{ex.GetType().Name}");
            return new(unavailable, unavailable, unavailable);
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

    private static GateLoadSample MeasureFileCachePagedPool()
    {
        if (!OperatingSystem.IsWindows())
            return GateLoadSample.Unavailable("unsupported-platform");
        try
        {
            const int systemPoolTagInformation = 22;
            const uint statusInfoLengthMismatch = 0xC0000004;
            const int maximumBufferSize = 16 * 1024 * 1024;
            var size = 64 * 1024;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var buffer = new byte[size];
                var status = NtQuerySystemInformation(systemPoolTagInformation, buffer, (uint)size, out var returned);
                if (status == statusInfoLengthMismatch)
                {
                    var nextSize = Math.Max((long)size * 2, returned);
                    if (nextSize > maximumBufferSize)
                        return GateLoadSample.Unavailable("pooltag-buffer-limit");
                    size = (int)nextSize;
                    continue;
                }
                if (status != 0)
                    return GateLoadSample.Unavailable($"pooltag-query-status-0x{status:X8}");
                if (returned > buffer.Length)
                    return GateLoadSample.Unavailable("malformed-buffer");
                return ParseFileCachePagedPool(buffer.AsSpan(0, (int)returned), IntPtr.Size);
            }
            return GateLoadSample.Unavailable("pooltag-query-retry-limit");
        }
        catch (Exception ex)
        {
            return GateLoadSample.Unavailable($"file-cache-error:{ex.GetType().Name}");
        }
    }

    internal static GateLoadSample ParseFileCachePagedPool(ReadOnlySpan<byte> buffer, int pointerSize)
    {
        if (pointerSize is not (4 or 8) || buffer.Length < pointerSize)
            return GateLoadSample.Unavailable("malformed-buffer");
        // SYSTEM_POOLTAG has ULONG counters and SIZE_T byte totals; x64 aligns SIZE_T to 8 bytes.
        var stride = pointerSize == 8 ? 40 : 28;
        var pagedUsedOffset = pointerSize == 8 ? 16 : 12;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count > (buffer.Length - pointerSize) / stride)
            return GateLoadSample.Unavailable("malformed-buffer");
        double bytes = 0;
        for (var index = 0; index < count; index++)
        {
            var entry = buffer.Slice(pointerSize + index * stride, stride);
            foreach (var tag in FileCachePoolTags)
            {
                if (entry[0] != tag[0] || entry[1] != tag[1] || entry[2] != tag[2] || entry[3] != tag[3])
                    continue;
                bytes += pointerSize == 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(entry[pagedUsedOffset..])
                    : BinaryPrimitives.ReadUInt32LittleEndian(entry[pagedUsedOffset..]);
                break;
            }
        }
        return GateLoadSample.Available(bytes / (1024 * 1024));
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int informationClass, [Out] byte[] buffer, uint bufferLength, out uint returnLength);

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
