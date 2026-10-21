using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Execution provenance belongs to the attempt. It makes no claim about baseline attribution.
public sealed record AcceptanceLaneRerunAttribution(
    string GoalId, string CandidateSha, string MainSha, string AttemptId, string CheckName,
    int FirstRunOrdinal, int RerunOrdinal,
    IReadOnlyList<AcceptanceLaneRerunReceipt> FirstReceipts,
    IReadOnlyList<AcceptanceLaneRerunReceipt> RerunReceipts);

public sealed record AcceptanceLaneRerunReceipt(string Path, string Sha256, string RunId);

internal sealed record AcceptanceConfirmedLaneFailure(
    string TestIdentity, AcceptanceLaneRerunAttribution Attribution);

internal static class AcceptanceLaneRerunAttributionReader
{
    internal static AcceptanceCheckResult Attach(AcceptanceCheckResult check,
        string? goalId, string? candidateSha, string? mainSha)
    {
        if (string.IsNullOrWhiteSpace(goalId) || string.IsNullOrWhiteSpace(candidateSha) ||
            string.IsNullOrWhiteSpace(mainSha) || string.IsNullOrWhiteSpace(check.TestResultAttemptId) ||
            !TryOrdinals(check, out var firstOrdinal, out var rerunOrdinal) ||
            Inspect(check) is not { } inspected) return check;
        return check with { LaneRerun = check.LaneRerun! with
        {
            Attribution = new(goalId, candidateSha, mainSha, check.TestResultAttemptId,
                check.Name, firstOrdinal, rerunOrdinal, inspected.First.Receipts, inspected.Rerun.Receipts)
        } };
    }

    internal static IReadOnlyList<AcceptanceConfirmedLaneFailure> ReadConfirmedFailures(
        AcceptanceCheckResult check, string goalId, string? candidateSha, string? mainSha, string? attemptId)
    {
        if (check.LaneRerun?.Attribution is not { } proof || string.IsNullOrWhiteSpace(attemptId) ||
            string.IsNullOrWhiteSpace(candidateSha) || string.IsNullOrWhiteSpace(mainSha) ||
            proof.GoalId != goalId || proof.CandidateSha != candidateSha || proof.MainSha != mainSha ||
            proof.AttemptId != attemptId || proof.AttemptId != check.TestResultAttemptId ||
            proof.CheckName != check.Name || !TryOrdinals(check, out var firstOrdinal, out var rerunOrdinal) ||
            proof.FirstRunOrdinal != firstOrdinal || proof.RerunOrdinal != rerunOrdinal ||
            Inspect(check) is not { } actual || proof.FirstReceipts is null || proof.RerunReceipts is null ||
            !actual.First.Receipts.SequenceEqual(proof.FirstReceipts) ||
            !actual.Rerun.Receipts.SequenceEqual(proof.RerunReceipts)) return [];
        return actual.First.Rows.Keys.Where(identity => actual.First.Rows[identity] == "Failed" &&
                actual.Rerun.Rows.GetValueOrDefault(identity) == "Failed")
            .Order(StringComparer.Ordinal).Select(identity => new AcceptanceConfirmedLaneFailure(identity, proof)).ToArray();
    }

    private static bool TryOrdinals(AcceptanceCheckResult check, out int first, out int rerun)
    {
        first = rerun = -1;
        if (check.LaneRerun is not { } lane || string.IsNullOrWhiteSpace(check.TestResultAttemptId)) return false;
        var prefix = $"{check.TestResultAttemptId}:{AcceptanceArtifactFileNames.Slug(check.Name)}:";
        bool Parse(string? invocation, out int ordinal)
        {
            ordinal = -1;
            return invocation is not null && invocation.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(invocation.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) &&
                ordinal >= 0 && invocation == prefix + ordinal.ToString(CultureInfo.InvariantCulture);
        }
        return Parse(lane.FirstInvocationId, out first) && Parse(lane.RerunInvocationId, out rerun) &&
            first < rerun && rerun == check.TestResultRunOrdinal;
    }

    private sealed record ReceiptRows(IReadOnlyList<AcceptanceLaneRerunReceipt> Receipts,
        Dictionary<string, string> Rows);

    private static (ReceiptRows First, ReceiptRows Rerun)? Inspect(AcceptanceCheckResult check)
    {
        if (check.Passed || check.TestResultIsExplicitCrossAttemptReuse ||
            check.LaneRerun is not { Outcome: AcceptanceLaneRerunEvidence.ConfirmedFailure } lane ||
            lane.FirstPredicate != AcceptanceShardCompletionPredicates.FailingTrx ||
            lane.RerunPredicate != AcceptanceShardCompletionPredicates.FailingTrx ||
            check.CompletionDecision is not { Passed: false, TimedOut: false,
                FailedPredicate: AcceptanceShardCompletionPredicates.FailingTrx } ||
            (check.CompletionDecision.ExecutedTestCount ?? check.ExecutedTestCount) is not > 0 ||
            check.ExecutedTestCount is <= 0 ||
            check.TestResultPaths is null || lane.RerunTestResultPaths is null ||
            lane.RerunTestResultPaths.Any(path => !check.TestResultPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) ||
            lane.FirstFailingTestIdentities is not { Count: > 0 } || lane.RerunFailingTestIdentities is not { Count: > 0 }) return null;
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            ReceiptRows? Read(IReadOnlyList<string>? sourcePaths)
            {
                if (sourcePaths is not { Count: > 0 }) return null;
                var receipts = new List<AcceptanceLaneRerunReceipt>();
                var rows = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in sourcePaths)
                {
                    if (string.IsNullOrWhiteSpace(path) || !paths.Add(Path.GetFullPath(path))) return null;
                    // Hash and parse the same bytes, including at the consuming boundary.
                    var bytes = File.ReadAllBytes(path);
                    using var stream = new MemoryStream(bytes, writable: false);
                    var doc = XDocument.Load(stream);
                    if (doc.Root?.Name.LocalName != "TestRun" || doc.Root.Attribute("id")?.Value is not { Length: > 0 } runId ||
                        !runIds.Add(runId)) return null;
                    var definitions = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var definition in doc.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
                        if (definition.Attribute("id")?.Value is not { Length: > 0 } id || !definitions.TryAdd(id, definition)) return null;
                    var results = doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").ToArray();
                    if (results.Length == 0) return null;
                    foreach (var row in results)
                    {
                        definitions.TryGetValue(row.Attribute("testId")?.Value ?? string.Empty, out var definition);
                        var identity = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
                        var outcome = row.Attribute("outcome")?.Value;
                        // Failed and Passed certify execution; taxonomy fatal outcomes such as NotRunnable do not.
                        if (string.IsNullOrWhiteSpace(identity) || outcome is not ("Failed" or "Passed") ||
                            !rows.TryAdd(identity, outcome)) return null;
                    }
                    receipts.Add(new(path, Convert.ToHexString(SHA256.HashData(bytes)), runId));
                }
                return new(receipts, rows);
            }
            var first = Read(lane.FirstTestResultPaths);
            var rerun = Read(lane.RerunTestResultPaths);
            bool Matches(ReceiptRows rows, IReadOnlyList<string> declared) =>
                declared.Count == declared.Distinct(StringComparer.Ordinal).Count() &&
                rows.Rows.Where(row => AcceptanceTrxOutcomeTaxonomy.IsFatal(row.Value)).Select(row => row.Key)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(declared);
            if (first is null || rerun is null || !Matches(first, lane.FirstFailingTestIdentities) ||
                !Matches(rerun, lane.RerunFailingTestIdentities)) return null;
            return (first, rerun);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or
            ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }
}
