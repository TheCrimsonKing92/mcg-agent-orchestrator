using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedGateReceiptTrxEvidence
{
    internal sealed record Receipt(
        string Kind, string ReceiptId, DateTimeOffset CompletedAt, IReadOnlyList<string> TrxPaths, int? BriefVersion = null);

    internal sealed record SelectorEvidence(
        string Selector, int Passed, int Failed, int Superseded, int Skipped, bool Satisfied,
        IReadOnlyList<string>? AmbiguousClassNames = null)
    {
        internal string ReportedEntry => ProspectiveEvidenceTrxRows.FormatEntry(Selector, AmbiguousClassNames);
    }

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
                        receipt.CompletedAt, receipt.GateTestResultPaths,
                        receipt.Identity.Members.Single(member => member.GoalId == goalId).BriefVersion)));
            }
            var cohortPath = Path.Combine(orchestratorDirectory, "cohort-acceptance.db");
            if (File.Exists(cohortPath))
            {
                var store = new CohortAcceptanceStore(cohortPath);
                receipts.AddRange(store.ReadPassedReceiptsForGoal(goalId)
                    .Where(receipt => receipt.Identity.Members.Any(member => member.GoalId == goalId &&
                        string.Equals(member.CandidateRevision, candidate, StringComparison.OrdinalIgnoreCase)))
                    .Select(receipt => new Receipt("cohort", receipt.ReceiptId,
                        receipt.CompletedAt, receipt.GateTestResultPaths,
                        receipt.Identity.Members.Single(member => member.GoalId == goalId).BriefVersion)));
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
        ProspectiveEvidenceTrxRows.Row[] rows;
        try
        {
            rows = trxPaths.Select(path => Path.GetFullPath(path, orchestratorDirectory))
                .SelectMany(ProspectiveEvidenceTrxRows.Read).ToArray();
        }
        catch (Exception)
        {
            // A partial receipt is never sufficient, even when its readable rows pass.
            return selectors.Select(selector => new SelectorEvidence(selector, 0, 0, 0, 0, false)).ToArray();
        }

        return selectors.Select(selector =>
        {
            var matches = ProspectiveEvidenceTrxRows.Match(selector, rows);
            var ambiguous = ProspectiveEvidenceTrxRows.AmbiguousClassNames(matches);
            var passed = matches.Count(row => row.Passed);
            var superseded = matches.Count(row => !row.Passed && rows.Any(rerun => rerun.Passed &&
                string.Equals(rerun.FullIdentity, row.FullIdentity, StringComparison.Ordinal) &&
                !string.Equals(rerun.Path, row.Path, StringComparison.OrdinalIgnoreCase)));
            var skipped = passed > 0 && matches.All(row => row.Passed || row.Outcome == "NotExecuted")
                ? matches.Length - passed - superseded : 0;
            var failed = matches.Length - passed - superseded - skipped;
            return new SelectorEvidence(selector, passed, failed, superseded, skipped,
                passed > 0 && failed == 0 && ambiguous is null, ambiguous);
        }).ToArray();
    }
}
