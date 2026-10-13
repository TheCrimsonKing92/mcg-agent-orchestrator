using System.Security.Cryptography;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial record AcceptanceCheckResult
{
    public AcceptanceAssemblyCleanupAttribution? AssemblyCleanupAttribution { get; init; }
}

/// <summary>Original cleanup receipts bound to the candidate and invocation that produced them.</summary>
public sealed record AcceptanceAssemblyCleanupAttribution(
    string GoalId, string CandidateSha, string MainSha, string CheckName, string AttemptId,
    int RunOrdinal, IReadOnlyList<AcceptanceAssemblyCleanupReceipt> Receipts);

public sealed record AcceptanceAssemblyCleanupReceipt(string Path, string Sha256, string RunId);

internal static class AcceptanceAssemblyCleanupAttributionReader
{
    internal static AcceptanceCheckResult Attach(AcceptanceCheckResult check,
        string? goalId, string? candidateSha, string? mainSha)
    {
        if (!HasCleanupDecision(check) || string.IsNullOrWhiteSpace(goalId) ||
            string.IsNullOrWhiteSpace(candidateSha) || string.IsNullOrWhiteSpace(mainSha) ||
            string.IsNullOrWhiteSpace(check.TestResultAttemptId)) return check;
        var receipts = Inspect(check);
        return receipts is null ? check : check with
        {
            AssemblyCleanupAttribution = new(goalId, candidateSha, mainSha, check.Name,
                check.TestResultAttemptId, check.TestResultRunOrdinal, receipts)
        };
    }

    internal static bool IsBoundCleanupOnly(AcceptanceCheckResult check,
        string goalId, string? candidateSha, string? mainSha)
    {
        if (!HasCleanupDecision(check) || check.TestResultIsExplicitCrossAttemptReuse ||
            check.AssemblyCleanupAttribution is not { } proof ||
            string.IsNullOrWhiteSpace(candidateSha) || string.IsNullOrWhiteSpace(mainSha) ||
            proof.GoalId != goalId || proof.CandidateSha != candidateSha || proof.MainSha != mainSha ||
            proof.CheckName != check.Name || proof.AttemptId != check.TestResultAttemptId ||
            string.IsNullOrWhiteSpace(proof.AttemptId) || proof.RunOrdinal != check.TestResultRunOrdinal ||
            proof.Receipts is not { Count: > 0 }) return false;
        var actual = Inspect(check);
        return actual is not null && actual.SequenceEqual(proof.Receipts);
    }

    private static bool HasCleanupDecision(AcceptanceCheckResult check) =>
        !check.Passed && check.TestResultRunOrdinal >= 0 &&
        check.CompletionDecision is { Passed: false, FailedPredicate: AcceptanceShardCompletionPredicates.AssemblyCleanupFailure } &&
        check.FailureClassification is AcceptanceFailureClassifications.AssemblyCleanupFailure or
            AcceptanceFailureClassifications.SharedGateApparatusInvalidated;

    private static IReadOnlyList<AcceptanceAssemblyCleanupReceipt>? Inspect(AcceptanceCheckResult check)
    {
        if (check.TestResultPaths is not { Count: > 0 }) return null;
        try
        {
            var receipts = new List<AcceptanceAssemblyCleanupReceipt>();
            AcceptanceAssemblyCleanupEvidence.RowCounts counts = default;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in check.TestResultPaths)
            {
                // Hash and inspect the same bytes; a path alone is not evidence.
                var bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes, writable: false);
                var document = XDocument.Load(stream);
                var runId = document.Root?.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(runId) || !runIds.Add(runId)) return null;
                var rows = document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
                counts = counts.Add(AcceptanceAssemblyCleanupEvidence.Classify(rows));
                var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
                    .Where(element => element.Attribute("id") is not null)
                    .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows.Where(row => AcceptanceTrxOutcomeTaxonomy.IsFatal(row.Attribute("outcome")?.Value)))
                {
                    definitions.TryGetValue(row.Attribute("testId")?.Value ?? string.Empty, out var definition);
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
                    if (string.IsNullOrWhiteSpace(identity)) return null;
                    identities.Add(identity);
                }
                receipts.Add(new(path, Convert.ToHexString(SHA256.HashData(bytes)), runId));
            }
            // Never suppress identities absent from the original evidence, including aggregate/mixed checks.
            return counts.IsCleanupOnly && identities.Count > 0 &&
                identities.SetEquals(check.FailingTestIdentities ?? []) ? receipts : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Xml.XmlException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }
}
