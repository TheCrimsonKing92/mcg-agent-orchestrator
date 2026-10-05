namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // The App targets net10.0, so Environment.CpuUsage.TotalTime avoids allocating
    // a finalizable Process handle per reading. It measures all process threads,
    // including background gate and canary work during each accounting window.
    private readonly Func<TimeSpan> _processCpuTime;
    private TimeSpan _lastCpuReading;
    private TimeSpan _tickCpuStart;
    private long _tickCpuSweepMs;
    private long _tickCpuPrewalkMs;
    private long _tickCpuWalkMs;

    private TimeSpan ReadProcessCpu()
    {
        try
        {
            _lastCpuReading = _processCpuTime();
        }
        catch (Exception)
        {
            // Telemetry must not fault a tick; reuse the last successful reading.
        }

        return _lastCpuReading;
    }

    private void BeginTickCpuAccounting()
    {
        _tickCpuStart = ReadProcessCpu();
        _tickCpuSweepMs = _tickCpuPrewalkMs = _tickCpuWalkMs = 0;
    }

    private static long CpuDeltaMs(TimeSpan start, TimeSpan end) =>
        Math.Max(0L, (long)(end - start).TotalMilliseconds);

    private long EndCpuPhase(TimeSpan start, ref long phaseTotal)
    {
        var elapsedMs = CpuDeltaMs(start, ReadProcessCpu());
        phaseTotal += elapsedMs;
        return elapsedMs;
    }

    private string FormatTickCpuSummary() =>
        $" cpu_ms={CpuDeltaMs(_tickCpuStart, ReadProcessCpu())} cpu_sweep_ms={_tickCpuSweepMs} cpu_prewalk_ms={_tickCpuPrewalkMs} cpu_walk_ms={_tickCpuWalkMs}";
}
