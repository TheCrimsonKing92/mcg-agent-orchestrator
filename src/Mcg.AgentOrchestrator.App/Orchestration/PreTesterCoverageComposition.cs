using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the bridge from original pre-Tester authority to ordinary request composition.
// Finding state and receipt persistence remain with the conductor's existing recording path.
internal static class PreTesterCoverageComposition
{
    internal sealed record PreTesterCoverageSource(string? ReceiptId, FindingEvidenceReceipt? Receipt,
        IReadOnlyList<FindingEvidenceSelection> Selections, DateTimeOffset CompletedAt);

    internal static IReadOnlyList<FindingEvidenceBatch> BindExecutionIdentities(IReadOnlyList<FindingEvidenceBatch> batches) =>
        batches.Select(batch => batch.Members.Any(member =>
                member.CoverageDecisionReason == "pre-tester-authority-missing" ||
                (member.ReusedSourceReceipts ?? []).Any(source => source.FindingRoundFingerprint == "pre-tester-deferred" ||
                    (source.RequestDispositions ?? []).Any(disposition => disposition.Reason == "retained-uncovered-execution")))
            // A retained C receipt must not deduplicate a later B repair. Member identities
            // still name the original B+C finding request; batch identities name actual work.
            ? batch with { Identity = BuildFindingEvidenceIdentity(batch.TypedRequest) }
            : batch).ToArray();

    internal static IReadOnlyList<PreTesterCoverageSource> ReadCoverage(
        ConductorDriver driver, Goal goal, string candidateSha, string basis)
    {
        return driver._focusedEvidenceAttemptCoordinator.ReadFocusedCoverageAttempts(goal.Id)
            .Where(attempt => attempt.FindingRoundFingerprint == "pre-tester-deferred" &&
                string.Equals(attempt.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                (attempt.FocusedExecutionBasisIdentity is null || attempt.FocusedExecutionBasisIdentity == basis))
            .Select(attempt =>
            {
                var receipt = driver._focusedEvidenceAttemptCoordinator.ReadOriginalFocusedReceipt(attempt);
                if (receipt is not null && !IsReusableGreenFindingEvidenceReceipt(receipt, candidateSha, basis)) receipt = null;
                return new PreTesterCoverageSource(attempt.FocusedEvidenceReceiptId, receipt,
                    (attempt.FocusedEvidenceMemberRequests ?? []).SelectMany(ParseDispositionSelections).ToArray(),
                    attempt.CompletedAt ?? attempt.StartedAt);
            }).ToArray();
    }

    // Resume the already selected remainder even if its reused source was invalidated while
    // it ran. Only scheduling is retained here; completion revalidates every reused source.
    internal static bool TryResumeRemainder(ConductorDriver driver, Goal goal, TaskSpec task, string candidateSha,
        string round, string identity, string basis, FindingEvidenceRequest request,
        out FindingEvidenceRequest remainder, out IReadOnlyList<FindingEvidenceReceipt> sources)
    {
        remainder = request;
        sources = [];
        if (request.NegativeControl is not null || request.RevertPaths is not null || request.Mutation is not null) return false;
        var attempts = driver._focusedEvidenceAttemptCoordinator.ReadFocusedCoverageAttempts(goal.Id);
        var attempt = attempts.LastOrDefault(attempt => attempt.FindingRoundFingerprint == round &&
            string.Equals(attempt.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            attempt.SupersededBy is null &&
            (attempt.FocusedEvidenceRequestDispositions ?? []).Any(disposition =>
                disposition.RequestIdentity == identity && disposition.Decision == FindingEvidenceRequestDecision.PendingExecution &&
                disposition.ExecutionBasisIdentity == basis &&
                disposition.SourceReceiptIds is { Count: > 0 }));
        if (attempt is null || task.LastVerification?.FindingEvidenceReceipts?.Any(receipt =>
            receipt.ReceiptId == attempt.FocusedEvidenceReceiptId) == true) return false;
        var ids = attempt.FocusedEvidenceRequestDispositions!.Where(disposition => disposition.RequestIdentity == identity)
            .SelectMany(disposition => disposition.SourceReceiptIds ?? []).ToHashSet(StringComparer.Ordinal);
        sources = attempts.Where(source => ids.Contains(source.FocusedEvidenceReceiptId ?? ""))
            .Select(source => source.OriginalFocusedReceipt).OfType<FindingEvidenceReceipt>()
            .Concat(task.VerificationHistory.SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
                .Where(receipt => ids.Contains(receipt.ReceiptId)))
            .DistinctBy(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
        remainder = request with { Selections = (attempt.FocusedEvidenceMemberRequests ?? []).SelectMany(ParseDispositionSelections).ToArray() };
        return remainder.Selections.Count > 0;
    }

    private static IReadOnlyList<FindingEvidenceReceipt> RevalidateSources(ConductorDriver driver, Goal goal, TaskSpec task,
        FindingEvidenceRequest originalRequest, string candidateSha, string basis,
        IReadOnlyList<FindingEvidenceReceipt> sources)
    {
        if (!sources.Any(source => source.FindingRoundFingerprint == "pre-tester-deferred")) return sources;
        if (!TryNormalizeFindingEvidenceRequest(originalRequest, driver._getFindingEvidenceEngineSettings(goal),
                (project, name) => driver._resolveFindingEvidenceSiblingClasses(goal, project, name),
                out originalRequest, out _, out _, out _)) return [];
        var liveSha = driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!string.Equals(liveSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            BuildFindingEvidenceExecutionBasisIdentity(driver._getFindingEvidenceEngineSettings(goal)) != basis) return [];
        TryResolveFindingEvidenceCoverage(task, originalRequest, candidateSha, basis,
            out var current, out _, out _, ReadCoverage(driver, goal, candidateSha, basis));
        return sources.Where(source => current.Any(valid => valid.ReceiptId == source.ReceiptId)).ToArray();
    }

    private static bool CombinedCoverageIsComplete(ConductorDriver driver, Goal goal, FindingEvidenceRequest request,
        FindingEvidenceReceipt executed, IReadOnlyList<FindingEvidenceReceipt> reused)
    {
        if (!TryNormalizeFindingEvidenceRequest(request, driver._getFindingEvidenceEngineSettings(goal),
                (project, name) => driver._resolveFindingEvidenceSiblingClasses(goal, project, name),
                out var normalized, out _, out _, out _)) return false;
        var coverage = reused.Append(executed).SelectMany(receipt =>
            FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, FormatFindingEvidenceSelection)).ToHashSet();
        return coverage.IsSupersetOf(FindingEvidenceRoundPlanner.ExpandSelections(normalized.Selections));
    }

    internal sealed record Completion(FindingEvidenceReceipt Receipt,
        IReadOnlyList<FindingEvidenceReceipt> ContributingReceipts, FindingEvidenceOutcome Outcome,
        bool Incomplete, string ExecutionResultReason);

    internal static Completion Complete(ConductorDriver driver, Goal goal, TaskSpec task,
        ReviewFinding finding, FindingEvidenceRequestGroup member, FindingEvidenceReceipt receipt,
        string candidateSha, string basis, string round, FindingEvidenceOutcomeReason? resultReason)
    {
        var validSources = RevalidateSources(driver, goal, task, finding.EvidenceRequest!,
            candidateSha, basis, member.ReusedSourceReceipts ?? []);
        var invalidated = validSources.Count != (member.ReusedSourceReceipts?.Count ?? 0);
        var preTesterComposition = member.CoverageDecisionReason == "pre-tester-authority-missing" ||
            (member.ReusedSourceReceipts ?? []).Any(source => source.FindingRoundFingerprint == "pre-tester-deferred");
        var incomplete = preTesterComposition && (invalidated ||
            !CombinedCoverageIsComplete(driver, goal, finding.EvidenceRequest!, receipt, validSources));
        if (incomplete)
        {
            // Keep C's execution receipt while the original B+C request remains pending.
            // An execution disposition for C does not assert that B executed again.
            var executedSelections = FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, FormatFindingEvidenceSelection);
            var retainedExecution = new List<FindingEvidenceRequestDisposition>();
            if (executedSelections.Count > 0 && (receipt.Arms ?? []).Any(arm =>
                    arm is { Arm: FindingEvidenceArm.Candidate, Accepted: true, ExecutedTestCount: > 0 }))
                retainedExecution.Add(new(finding.StableId,
                    BuildFindingEvidenceIdentity(receipt.Request with { Selections = executedSelections }),
                    "executed-standalone", "retained-uncovered-execution", FindingEvidenceRequestDecision.Executed,
                    candidateSha, round, SourceReceiptIds: [receipt.ReceiptId], ExecutionBasisIdentity: basis));
            retainedExecution.Add(new(finding.StableId, member.Identity,
                "incomplete", "reused-pre-tester-invalidated-or-incomplete", FindingEvidenceRequestDecision.PendingExecution,
                candidateSha, round, SourceReceiptIds: [receipt.ReceiptId], ExecutionBasisIdentity: basis));
            receipt = receipt with { RequestDispositions = (receipt.RequestDispositions ?? [])
                .Where(item => item.FindingStableId != finding.StableId)
                .Concat(retainedExecution)
                .ToArray() };
        }
        var sourceReceiptIds = validSources.Select(source => source.ReceiptId).Append(receipt.ReceiptId)
            .Distinct(StringComparer.Ordinal).ToArray();
        var outcome = new FindingEvidenceOutcome(
            Honoured: true,
            ReceiptId: receipt.ReceiptId,
            ResultReason: incomplete ? FindingEvidenceOutcomeReason.Unknown : resultReason ?? FindingEvidenceOutcomeReason.Unknown,
            RequestedSelectionIdentity: member.Identity,
            DecisionReason: incomplete ? "reused-pre-tester-invalidated:pending-execution" : member.CoverageDecisionReason is null
                ? "executed-focused-evidence"
                : $"executed-uncovered-after-reuse:{member.CoverageDecisionReason}",
            SourceReceiptIds: sourceReceiptIds);
        return new(receipt, validSources.Append(receipt).ToArray(), outcome, incomplete,
            FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(resultReason ?? FindingEvidenceOutcomeReason.Unknown));
    }
}
