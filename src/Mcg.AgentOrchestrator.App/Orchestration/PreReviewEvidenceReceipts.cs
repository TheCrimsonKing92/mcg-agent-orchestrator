using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns current-candidate pre-review receipt coverage, reuse, and recording.
// The driver supplies its authoritative recording callback; this helper owns no kernel state.
internal static class PreReviewEvidenceReceipts
{
    internal static PreReviewEvidenceReceipt Record(
        Action<GoalId, TaskId, PreReviewEvidenceReceipt> record,
        Goal goal,
        TaskSpec reviewerTask,
        PreReviewEvidenceContext context,
        int round,
        PreReviewEvidenceDisposition disposition,
        IReadOnlyList<AcceptanceCheckResult> checks,
        IReadOnlyList<string> failingTests,
        string? evidencePointer)
    {
        var receipt = new PreReviewEvidenceReceipt(
            goal.Id.Value,
            round,
            context.CandidateSha!,
            context.SelectedFocusedTests,
            disposition,
            checks.Count(check => check.Passed),
            checks.Count(check => !check.Passed),
            checks.Select((check, index) => new PreReviewEvidenceCheckReceipt(
                check.Name,
                ResolvePreReviewReceiptTarget(context, index, checks.Count),
                check.Passed,
                check.ExitCode,
                check.ArtifactsPath,
                check.TestResultPaths)).ToArray(),
            failingTests,
            context.MappingReason,
            evidencePointer,
            DateTimeOffset.UtcNow);
        record(goal.Id, reviewerTask.Id, receipt);
        return receipt;
    }

    internal static bool TryReuse(
        TaskSpec reviewerTask,
        string goalId,
        string? candidateSha,
        IReadOnlyList<string> requestedSelections,
        out IReadOnlyList<PreReviewEvidenceReceipt> constituentReceipts)
    {
        constituentReceipts = [];
        if (string.IsNullOrWhiteSpace(candidateSha) || requestedSelections.Count == 0)
        {
            return false;
        }

        var currentCandidateReceipts = reviewerTask.PreReviewEvidenceHistory
            .Where(receipt =>
                string.Equals(receipt.GoalId, goalId, StringComparison.Ordinal) &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .ToArray();
        if (currentCandidateReceipts.Length == 0)
        {
            return false;
        }

        // The latest receipt for each selection is authoritative. An earlier green receipt must
        // never hide a later red, incomplete, or mapping-needed result for the same candidate.
        var latestReceipts = requestedSelections
            .Select(selection => currentCandidateReceipts.FirstOrDefault(receipt =>
                receipt.SelectedFocusedTests.Contains(selection, StringComparer.Ordinal)))
            .ToArray();
        if (latestReceipts.Any(receipt => receipt is null) ||
            latestReceipts.Any(receipt => !HasCompleteGreenCoverage(receipt!)))
        {
            return false;
        }

        constituentReceipts = latestReceipts
            .OfType<PreReviewEvidenceReceipt>()
            .Distinct()
            .ToArray();
        return constituentReceipts.Count > 0;
    }

    private static bool HasCompleteGreenCoverage(PreReviewEvidenceReceipt receipt) =>
        receipt.Disposition == PreReviewEvidenceDisposition.Green &&
        receipt.FailedCheckCount == 0 && receipt.FailingTestIdentities.Count == 0 &&
        receipt.SelectedFocusedTests.Count > 0 &&
        receipt.SelectedFocusedTests.Distinct(StringComparer.Ordinal).Count() == receipt.SelectedFocusedTests.Count &&
        receipt.PassedCheckCount == receipt.Checks.Count &&
        receipt.Checks.Count == receipt.SelectedFocusedTests.Count &&
        receipt.Checks.All(check => check.Passed && check.ExitCode == 0) &&
        receipt.SelectedFocusedTests.All(selection =>
            receipt.Checks.Count(check => string.Equals(check.Command, selection, StringComparison.Ordinal)) == 1);

    internal static void RecordReuse(
        Action<GoalId, TaskId, PreReviewEvidenceReceipt> record,
        Goal goal,
        TaskSpec reviewerTask,
        PreReviewEvidenceContext context,
        int round,
        IReadOnlyList<PreReviewEvidenceReceipt> constituents)
    {
        var evidencePointers = constituents
            .Select(receipt => receipt.EvidencePointer)
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pointer => pointer, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var checks = context.SelectedFocusedTests.Select(selection => constituents
            .SelectMany(constituent => constituent.Checks)
            .First(check => string.Equals(check.Command, selection, StringComparison.Ordinal))).ToArray();
        var receipt = new PreReviewEvidenceReceipt(
            goal.Id.Value,
            round,
            context.CandidateSha!,
            context.SelectedFocusedTests,
            PreReviewEvidenceDisposition.Green,
            PassedCheckCount: checks.Length,
            FailedCheckCount: 0,
            // A reuse receipt must describe the original checks, never invent executions that
            // did not occur in this reviewer round.
            Checks: checks,
            FailingTestIdentities: [],
            MappingReason: context.MappingReason + "; reused green current-candidate evidence coverage.",
            EvidencePointer: evidencePointers.Length == 0 ? PreReviewEvidenceReceipt.SyntheticReuseAdvisory : string.Join(",", evidencePointers),
            RecordedAt: DateTimeOffset.UtcNow,
            Advisories: [PreReviewEvidenceReceipt.SyntheticReuseAdvisory]);
        record(goal.Id, reviewerTask.Id, receipt);
    }

    internal static bool ValidateCoverage(
        PreReviewEvidenceContext context,
        FocusedEvidenceRunResult evidence,
        out string failure)
    {
        failure = string.Empty;
        if (evidence.Checks.Count == context.SelectedFocusedTests.Count)
        {
            return true;
        }

        failure = $"cardinality mismatch: planned={context.SelectedFocusedTests.Count} actual={evidence.Checks.Count}";
        return false;
    }

    private static string ResolvePreReviewReceiptTarget(
        PreReviewEvidenceContext context,
        int index,
        int checkCount)
    {
        return checkCount == context.SelectedFocusedTests.Count
            ? context.SelectedFocusedTests[index]
            : "(unmapped: check/command cardinality mismatch)";
    }
}
