namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static bool StopsOnCaptureLimit(GateHeartbeatContext context) =>
        string.Equals(context.RunClass, GateHeartbeatRunClass.FocusedEvidence, StringComparison.Ordinal);

    internal static bool IsInterrupted(CommandResult result) => result.TimedOut || result.CaptureLimited;

    private static bool IsCaptureLimitFailureName(string name) =>
        name.StartsWith("acceptance-check-capture-limit:", StringComparison.Ordinal);

    internal static AcceptanceShardCompletionDecision InferPartitionCompletionDecisionForTests(
        AcceptanceCheckResult result) => InferPartitionCompletionDecision(result);

    private string CaptureLimitDetail(AcceptanceCheckResult result) =>
        IsCaptureLimitFailureName(result.Name) ? $" cap_bytes={EngineSettings.OutputCaptureLimitBytes}" : string.Empty;

    private static string BuildInterruptedFailureName(AcceptanceManifestCheck check, CommandResult result) =>
        result.CaptureLimited
            ? $"acceptance-check-capture-limit: {Slug(check.Name)} {BuildTimeoutSummary(result)}"
            : BuildTimeoutFailureName(check, result);

    private static string BuildInterruptedOutput(CommandResult result) =>
        result.CaptureLimited
            ? BuildCaptureLimitOutput(result, result.CaptureLimitBytes ?? throw new InvalidOperationException(
                "Capture-limited process result is missing its configured cap bytes."))
            : BuildTimeoutOutput(result);

    private static string BuildCaptureLimitOutput(CommandResult result, long capBytes)
    {
        var details = new List<string>
        {
            $"Verification command reached output capture limit cap_bytes={capBytes} after {BuildTimeoutSummary(result)}."
        };
        if (!string.IsNullOrWhiteSpace(result.CommandLine))
            details.Add($"Command: {result.CommandLine}");
        if (!string.IsNullOrWhiteSpace(result.StdoutPath))
            details.Add($"stdout: {result.StdoutPath}");
        if (!string.IsNullOrWhiteSpace(result.StderrPath))
            details.Add($"stderr: {result.StderrPath}");
        var tail = TailOutput(result.Output);
        if (!string.IsNullOrWhiteSpace(tail))
        {
            details.Add("Last output:");
            details.Add(tail);
        }
        return string.Join(Environment.NewLine, details);
    }

    internal static Task<CommandResult> RunProcessWithCaptureLimitForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan timeout,
        long captureLimitBytes,
        string runClass,
        Action<SpawnProcessIdentity>? commandIdentityObserver = null,
        CancellationToken cancellationToken = default)
    {
        var context = new GateHeartbeatContext(
            "test-goal", "verification-check", "capture-limit", null, null, null,
            string.Join(' ', arguments.Select(QuoteForDisplay)), null) { RunClass = runClass };
        return RunProcessAsync(
            arguments, workingDirectory, timeout, forceUtf8ConsoleOutput: false,
            cancellationToken, commandIdentityObserver: commandIdentityObserver,
            engineSettings: new AcceptanceGateEngineSettings { OutputCaptureLimitBytes = captureLimitBytes },
            stopOnCaptureLimit: StopsOnCaptureLimit(context));
    }
}
