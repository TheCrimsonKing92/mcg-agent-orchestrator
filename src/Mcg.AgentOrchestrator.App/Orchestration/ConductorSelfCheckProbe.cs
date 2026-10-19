using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorSelfCheckProbe
{
    internal const string SelfCheckTimeoutEnvironmentVariable = "MCG_ORCHESTRATOR_SELF_CHECK_TIMEOUT_SECONDS";
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal delegate SuccessorProbeResult SuccessorProbeRunner(
        IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken);

    internal readonly record struct SuccessorProbeResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
    internal readonly record struct ProbeOutcome(SuccessorProbeResult Result, int Attempts);

    internal static TimeSpan ResolveTimeout(string? configuredValue) =>
        double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        seconds >= 10 && seconds <= 600
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;

    internal static ProbeOutcome Probe(
        SuccessorProbeRunner runner, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout,
        Action<string>? recordEvent, CancellationToken cancellationToken, Func<Func<long>>? startTimer = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = (startTimer ?? StartTimer)();
            var result = runner(arguments, workingDirectory, timeout, cancellationToken);
            var milliseconds = elapsed();
            var outcome = result.TimedOut ? "timeout" : result.ExitCode == 0 ? "ok" : "failed";
            recordEvent?.Invoke(FormattableString.Invariant(
                $"SELF_CHECK_ELAPSED attempt={attempt} ms={milliseconds} outcome={outcome}"));
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.TimedOut || attempt == 2)
                return new(result, attempt);

            recordEvent?.Invoke(FormattableString.Invariant($"SELF_CHECK_RETRY reason=timeout firstMs={milliseconds}"));
        }
    }

    internal static string FormatFailure(ProbeOutcome probe) =>
        $"probe successor startup contract failed exit={probe.Result.ExitCode} timedOut={probe.Result.TimedOut} " +
        $"attempts={probe.Attempts} stdoutTail=[{BoundedTail(probe.Result.Stdout)}] stderrTail=[{BoundedTail(probe.Result.Stderr)}]";

    internal static string BoundedTail(string? stream)
    {
        var lines = (stream ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(5)
            .Select(line => new string(line.Take(200).Select(character => char.IsControl(character) ? ' ' : character).ToArray()))
            .ToArray();
        return lines.Length == 0 ? "<empty>" : string.Join(" | ", lines);
    }

    private static Func<long> StartTimer()
    {
        var stopwatch = Stopwatch.StartNew();
        return () => stopwatch.ElapsedMilliseconds;
    }
}
