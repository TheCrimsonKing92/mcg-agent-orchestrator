namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static string BuildMtpFailureOutput(
        string checkName,
        CommandResult result,
        DotnetTestTelemetry telemetry,
        TrxCompletionEvidence trxEvidence,
        AcceptanceShardCompletionDecision completionDecision)
    {
        var details = new List<string>();
        var commandOutput = IsInterrupted(result)
            ? BuildInterruptedOutput(result)
            : TailOutput(result.Output);
        if (!string.IsNullOrWhiteSpace(commandOutput))
        {
            details.Add(commandOutput);
        }

        var trxPaths = telemetry.Paths.Where(File.Exists).ToArray();
        if (trxPaths.Length == 0)
        {
            details.Add(
                $"[FAIL] {checkName}: failed — no TRX produced (shard was killed or crashed before reporter flushed)");
            return string.Join(Environment.NewLine, details);
        }

        var failures = new List<string>();
        foreach (var trxPath in trxPaths)
        {
            try
            {
                failures.AddRange(ExtractTrxFailureEvidence(trxPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                details.Add(
                    $"[FAIL] {checkName}: failed — TRX found but could not be read ({FirstNonEmptyLine(ex.Message)})");
                return string.Join(Environment.NewLine, details);
            }
        }

        if (failures.Count == 0)
        {
            details.Add(BuildEmptyMtpFailureDiagnostic(checkName, result, trxEvidence, completionDecision));
        }
        else
        {
            details.AddRange(failures);
        }

        return string.Join(Environment.NewLine, details);
    }

    private static string BuildEmptyMtpFailureDiagnostic(
        string checkName,
        CommandResult result,
        TrxCompletionEvidence trxEvidence,
        AcceptanceShardCompletionDecision completionDecision)
    {
        if (!IsInterrupted(result) &&
            trxEvidence.ExecutedTestCount is > 0 &&
            trxEvidence.Passed &&
            string.Equals(
                completionDecision.FailedPredicate,
                AcceptanceShardCompletionPredicates.NonzeroExit,
                StringComparison.Ordinal))
        {
            var discovered = FormatTestCount(trxEvidence.DiscoveredTestCount);
            var diagnostic =
                $"[FAIL] {checkName}: failed — {FormatTestCount(trxEvidence.ExecutedTestCount)} of {discovered} tests executed and every TRX record is green (outcome={trxEvidence.Outcome}); " +
                $"the shard process exited {result.ExitCode} after the run completed and no test failure explains that exit code (predicate={completionDecision.FailedPredicate})";
            if (TestRunReportsAllPassed(result.Output))
            {
                diagnostic += "; run summary reported Passed!";
            }

            const string ForegroundThreadForcedExit =
                "[FATAL ERROR] Foreground threads were left running, forcing process exit";
            if (result.Output.Contains(ForegroundThreadForcedExit, StringComparison.Ordinal))
            {
                diagnostic += $"; runner reported: \"{ForegroundThreadForcedExit}\"";
            }

            return diagnostic;
        }

        if (trxEvidence.ExecutedTestCount is > 0)
        {
            return
                $"[FAIL] {checkName}: failed — TRX reports {FormatTestCount(trxEvidence.ExecutedTestCount)} of {FormatTestCount(trxEvidence.DiscoveredTestCount)} tests executed (outcome={trxEvidence.Outcome}) but contained no failure records that explain the shard result (predicate={completionDecision.FailedPredicate ?? "unknown"})";
        }

        return
            $"[FAIL] {checkName}: failed — TRX found but contained no failure records (process may have exited before tests ran)";
    }

    private static string FormatTestCount(int? count) =>
        count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    internal static string BuildMtpFailureOutputForTests(
        string checkName,
        CommandResult result,
        IEnumerable<string> trxPaths)
    {
        var paths = trxPaths.ToArray();
        var trxEvidence = InspectTrxCompletionEvidence(paths);
        var completionDecision = DecideTestShardCompletion(result, trxEvidence);
        return BuildMtpFailureOutput(
            checkName,
            result,
            new DotnetTestTelemetry(paths, []),
            trxEvidence,
            completionDecision);
    }
}
