using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static string AppendActionableCandidateRedFailureDetail(
        string message,
        string receiptId,
        IReadOnlyList<string> failingTestIdentities,
        IReadOnlyList<string>? testResultPaths,
        Func<string, AcceptanceTrxReadResult>? readTrx = null)
    {
        var header = $"Candidate failure detail (receipt {receiptId}):";
        try
        {
            var identities = failingTestIdentities
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var paths = testResultPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? [];
            var lines = new List<string>();
            var failures = new Dictionary<string, AcceptanceTrxFailure>(StringComparer.Ordinal);
            if (paths.Length == 0)
                lines.Add($"detail unavailable: no TRX path was recorded for receipt {receiptId}");

            foreach (var path in paths)
            {
                if (failures.Count == identities.Length || failures.Count == MaxCriterionRetryEvidenceLines)
                    break;

                var result = (readTrx ?? AcceptanceTrxFailureReader.Read)(path);
                if (result.Status != AcceptanceTrxReadStatus.Readable)
                {
                    lines.Add($"detail unavailable: {DescribeTrxReadFailure(result.Status)} at {path}");
                    continue;
                }

                var allowed = identities.ToHashSet(StringComparer.Ordinal);
                foreach (var failure in result.Failures)
                {
                    if (failure.TestName is null || !allowed.Contains(failure.TestName) ||
                        failures.ContainsKey(failure.TestName))
                        continue;
                    if (failures.Count == MaxCriterionRetryEvidenceLines)
                        break;
                    failures.Add(failure.TestName, failure);
                }
                if (failures.Count == MaxCriterionRetryEvidenceLines && identities.Length > failures.Count)
                {
                    lines.Add($"{identities.Length - failures.Count} more failures omitted - see {path}");
                    break;
                }
            }

            foreach (var identity in identities)
            {
                if (failures.TryGetValue(identity, out var failure))
                    lines.Add(FormatCappedTrxFailure(failure));
                else if (failures.Count < MaxCriterionRetryEvidenceLines)
                    lines.Add($"detail unavailable: no failure detail found for {identity}");
            }
            return message + Environment.NewLine + Environment.NewLine + header +
                   Environment.NewLine + string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return message + Environment.NewLine + Environment.NewLine + header + Environment.NewLine +
                   $"detail unavailable: {ex.GetType().FullName}: {NormalizeRetryLineEndings(ex.Message)}";
        }
    }

    private static string FormatCappedTrxFailure(AcceptanceTrxFailure failure)
    {
        var message = string.IsNullOrEmpty(failure.Message) ? "failure message unavailable" : failure.Message;
        if (message.Length > 2000)
            message = message[..2000] + "... [message truncated]";

        var stackLines = string.IsNullOrEmpty(failure.StackTrace)
            ? Array.Empty<string>()
            : failure.StackTrace.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Split('\n');
        var stack = stackLines.Length == 0
            ? "stack trace unavailable"
            : string.Join(Environment.NewLine, stackLines.Take(10));
        if (stackLines.Length > 10)
            stack += Environment.NewLine + $"... ({stackLines.Length - 10} more stack lines)";
        return $"[FAIL] {failure.TestName} ({failure.Outcome}){Environment.NewLine}" +
               NormalizeRetryLineEndings(message) + Environment.NewLine + stack;
    }

    private static string NormalizeRetryLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
}
