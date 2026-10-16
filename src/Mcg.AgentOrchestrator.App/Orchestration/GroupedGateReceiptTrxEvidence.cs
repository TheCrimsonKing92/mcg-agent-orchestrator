using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedGateReceiptTrxEvidence
{
    internal sealed record Receipt(
        string Kind, string ReceiptId, DateTimeOffset CompletedAt, IReadOnlyList<string> TrxPaths);

    internal sealed record SelectorEvidence(
        string Selector, int Passed, int Failed, int Superseded, bool Satisfied);

    internal static IReadOnlyList<Receipt> ReadPassedReceipts(
        string orchestratorDirectory, GoalId goalId, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return [];
        try
        {
            var receipts = new List<Receipt>();
            var trainPath = Path.Combine(orchestratorDirectory, "merge-train-acceptance.db");
            if (File.Exists(trainPath))
            {
                var store = MergeTrainAcceptanceStore.OpenReadOnly(trainPath);
                receipts.AddRange(store.ReadPassedReceiptsForGoal(goalId)
                    .Where(receipt => receipt.Identity.Members.Any(member => member.GoalId == goalId &&
                        string.Equals(member.CandidateRevision, candidate, StringComparison.OrdinalIgnoreCase)))
                    .Select(receipt => new Receipt("merge-train", receipt.ReceiptId,
                        receipt.CompletedAt, receipt.GateTestResultPaths)));
            }
            var cohortPath = Path.Combine(orchestratorDirectory, "cohort-acceptance.db");
            if (File.Exists(cohortPath))
            {
                var store = new CohortAcceptanceStore(cohortPath);
                receipts.AddRange(store.ReadPassedReceiptsForGoal(goalId)
                    .Where(receipt => receipt.Identity.Members.Any(member => member.GoalId == goalId &&
                        string.Equals(member.CandidateRevision, candidate, StringComparison.OrdinalIgnoreCase)))
                    .Select(receipt => new Receipt("cohort", receipt.ReceiptId,
                        receipt.CompletedAt, receipt.GateTestResultPaths)));
            }
            return receipts.OrderByDescending(receipt => receipt.CompletedAt)
                .ThenBy(receipt => receipt.Kind == "merge-train" ? 0 : 1)
                .ThenBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
        }
        catch (Exception)
        {
            // Inaccessible or invalid stores cannot authorize an automatic answer.
            return [];
        }
    }

    internal static IReadOnlyList<SelectorEvidence> Evaluate(
        IReadOnlyList<string> selectors, IReadOnlyList<string> trxPaths, string orchestratorDirectory)
    {
        TestRow[] rows;
        try
        {
            rows = trxPaths.Select(path => Path.GetFullPath(path, orchestratorDirectory))
                .SelectMany(ReadRows).ToArray();
        }
        catch (Exception)
        {
            // A partial receipt is never sufficient, even when its readable rows pass.
            return selectors.Select(selector => new SelectorEvidence(selector, 0, 0, 0, false)).ToArray();
        }

        return selectors.Select(selector =>
        {
            var matches = rows.Where(row => ProspectiveEvidenceGateResolver.Matches(selector, row.FullIdentity) ||
                ProspectiveEvidenceGateResolver.Matches(selector, row.ShortIdentity)).ToArray();
            var passed = matches.Count(row => row.Passed);
            var superseded = matches.Count(row => !row.Passed && rows.Any(rerun => rerun.Passed &&
                string.Equals(rerun.FullIdentity, row.FullIdentity, StringComparison.Ordinal) &&
                !string.Equals(rerun.Path, row.Path, StringComparison.OrdinalIgnoreCase)));
            var failed = matches.Length - passed - superseded;
            return new SelectorEvidence(selector, passed, failed, superseded, passed > 0 && failed == 0);
        }).ToArray();
    }

    private static IEnumerable<TestRow> ReadRows(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var definitions = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTest" && element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
            var full = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
            if (full is null) continue;
            var className = definition?.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "TestMethod")?.Attribute("className")?.Value.Trim();
            var namespaceEnd = className?.LastIndexOf('.') ?? -1;
            var shortIdentity = namespaceEnd >= 0 && full.StartsWith(className!, StringComparison.Ordinal)
                ? full[(namespaceEnd + 1)..] : full;
            yield return new(full, shortIdentity, path, result.Attribute("outcome")?.Value == "Passed");
        }
    }

    private sealed record TestRow(string FullIdentity, string ShortIdentity, string Path, bool Passed);
}
