using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorFirstTickEvent(
    Func<DateTimeOffset?> startedAt, Func<DateTimeOffset> clock, Action<string> emit)
{
    private int _observed;

    internal static ConductorFirstTickEvent ForCurrentProcess() => new(
        () =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch { return null; }
        },
        () => DateTimeOffset.UtcNow,
        line => ConductorBatchLoop.EmitProgress(line));

    internal void Observe()
    {
        if (Interlocked.Exchange(ref _observed, 1) != 0)
            return;

        var start = startedAt();
        var line = start is null
            ? "STARTUP_FIRST_TICK elapsed_ms=unknown process_started=unknown"
            : $"STARTUP_FIRST_TICK elapsed_ms={Math.Max(0, (clock() - start.Value).Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture)} process_started={start.Value.ToString("O", CultureInfo.InvariantCulture)}";
        try { emit(line); } catch { }
    }
}
