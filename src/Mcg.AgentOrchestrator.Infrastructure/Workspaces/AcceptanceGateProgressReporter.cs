namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGateProgressReporter
{
    internal static void Emit(AcceptanceGateProgress progress, string? trailingTokens,
        IAcceptanceRunExecutionContext? context)
    {
        progress = AcceptanceGateProgressProvenance.Attach(progress, context);
        var line =
            $"PHASE_PROGRESS goal={FormatNullableToken(progress.GoalId, 8)} phase={progress.Phase} elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} " +
            $"target={QuoteProgressToken(progress.CurrentTarget)} child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"output_bytes={progress.OutputBytes} heartbeat={QuoteProgressToken(progress.HeartbeatPath)} {GateLoadContextProbe.FormatProgressTokens(progress.LoadContext)}" + progress.ProvenanceTokens;
        Console.WriteLine($"{line}{(trailingTokens is null ? "" : " " + trailingTokens)} ts={progress.LastObservedAt:O}");
        Console.Out.Flush();
        context?.ReportProgress(progress);
    }

    internal static string FormatNullableToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    internal static string QuoteProgressToken(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
