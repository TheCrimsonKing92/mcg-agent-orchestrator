using System.Text;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;

namespace Mcg.AgentOrchestrator.Core;

internal static class DispatchClassifierReceipt
{
    internal static DispatchOutcome WithClassifierReceipt(TaskOutcomeRule rule, DispatchOutcome outcome, int exitCode) =>
        (outcome with { OutcomeClass = rule.Class }) with
        {
            ClassifierReceipt = AppendEvidenceReceipt(
                $"CLASSIFIER rule={rule.Token}; outcome_class={TaskOutcomeClassifier.FormatClass(rule.Class)}; " +
                $"exit_code={exitCode}; exit_artifact=direct-provider-failure; " +
                "stdout_bytes=unknown; stderr_bytes=unknown; heartbeat_stdout_bytes=unknown; " +
                $"worker_result=absent; commit=none; verdict={outcome.Kind}",
                outcome)
        };

    internal static string AppendEvidenceReceipt(string receipt, DispatchOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.EvidenceSummary))
        {
            return receipt;
        }

        return $"{receipt}; evidence={SanitizeReceiptValue(outcome.EvidenceSummary, maxLength: 240)}";
    }

    internal static string BuildClassifierReceipt(
        TaskOutcomeRule rule,
        TaskOutcomeClass outcomeClass,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges,
        DispatchOutcomeKind verdict)
    {
        var stdoutBytes = GetOutputByteCount(verification.StandardOutputPath, verification.StandardOutput);
        var stderrBytes = GetOutputByteCount(verification.StandardErrorPath, verification.StandardError);
        var heartbeat = verification.HeartbeatStandardOutputBytes is { } heartbeatBytes
            ? heartbeatBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";
        var workerResult = DescribeWorkerResult(verification, workerResultPresent);
        var commitProvenance = DescribeCommitProvenance(task, verification, hasCommittedChanges);
        var duration = verification.DispatchStartedAt is { } startedAt
            ? Math.Max(0, (long)(verification.CompletedAt - startedAt).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

        var childExitCode = verification.ChildExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var rootExitCode = (verification.ObservedRootExitCode ?? verification.ExitCode)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reconciliationOrigin = string.IsNullOrWhiteSpace(verification.ReconciliationOriginRule)
            ? string.Empty
            : $"; origin_rule={SanitizeReceiptValue(verification.ReconciliationOriginRule)}";

        return $"CLASSIFIER rule={rule.Token}; outcome_class={TaskOutcomeClassifier.FormatClass(outcomeClass)}{reconciliationOrigin}; " +
            $"exit_code={verification.ExitCode}; root_exit_code={rootExitCode}; " +
            $"child_exit_code={childExitCode}; exit_artifact=verification-record; " +
            $"stdout_bytes={stdoutBytes}; stderr_bytes={stderrBytes}; heartbeat_stdout_bytes={heartbeat}; " +
            $"duration_ms={duration}; worker_result={workerResult}; commit={commitProvenance}; verdict={verdict}";
    }

    private static long GetOutputByteCount(string? path, string output)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        return Encoding.UTF8.GetByteCount(output);
    }

    private static string DescribeWorkerResult(TaskVerificationRecord verification, bool workerResultPresent)
    {
        if (TryGetWorkerResultBlockersValue(verification, out var blockers))
        {
            return $"present(blockers={SanitizeReceiptValue(blockers)})";
        }

        if (workerResultPresent ||
            verification.StandardOutput.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase) ||
            verification.StandardError.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerResultBlockers.TryFindBlocker(verification, out var blocker)
                ? $"present(blockers={SanitizeReceiptValue(blocker)})"
                : "present(blockers=absent)";
        }

        return "absent";
    }

    private static bool TryGetWorkerResultBlockersValue(TaskVerificationRecord verification, out string blockers)
    {
        return TryGetWorkerResultFieldValue(verification, "blockers", out blockers);
    }

    internal static bool TryGetWorkerResultFieldValue(
        TaskVerificationRecord verification,
        string fieldName,
        out string value) =>
        TryGetWorkerResultFieldValue(
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true),
            fieldName,
            out value);

    internal static bool TryGetWorkerResultFieldValue(
        IEnumerable<string> evidenceLines,
        string fieldName,
        out string value)
    {
        value = string.Empty;
        var inBlock = false;
        string? latestValue = null;

        foreach (var rawLine in evidenceLines)
        {
            var line = rawLine.Trim();
            if (IsWorkerResultOpener(line))
            {
                inBlock = true;
                latestValue = null;
                continue;
            }

            if (IsWorkerResultEndMarker(line))
            {
                if (inBlock && latestValue is not null)
                {
                    value = latestValue;
                    return true;
                }

                inBlock = false;
                continue;
            }

            if (!inBlock)
            {
                continue;
            }

            var sep = line.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
            {
                continue;
            }

            var key = NormalizeWorkerResultMarker(line[..sep]).TrimStart('-', ' ').Trim();
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                latestValue = line[(sep + 1)..].Trim();
            }
        }

        if (inBlock && latestValue is not null)
        {
            value = latestValue;
            return true;
        }

        return false;
    }

    private static string DescribeCommitProvenance(TaskSpec task, TaskVerificationRecord verification, bool hasCommittedChanges)
    {
        if (DispatchCompletionEvidence.HasDispatchResultCommitEvidence(task))
        {
            return "orchestrator";
        }

        return hasCommittedChanges || verification.HasCommittedChanges ? "worker" : "none";
    }

    internal static bool IsNoWorkerResultBlockersValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.StartsWith('<') && value.EndsWith('>'))
        {
            return true;
        }

        return value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none:", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeReceiptValue(string value, int maxLength = 80)
    {
        var sanitized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace(";", ",", StringComparison.Ordinal)
            .Trim();

        return sanitized.Length > maxLength ? sanitized[..maxLength] : sanitized;
    }
}
