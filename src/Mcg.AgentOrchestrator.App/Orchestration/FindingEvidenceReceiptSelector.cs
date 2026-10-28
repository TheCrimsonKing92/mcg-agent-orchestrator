using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Selects receipts from task evidence; verification mutation and dispatch stay with the conductor.
internal static class FindingEvidenceReceiptSelector
{
    internal static bool TrySelectReviewContinuation(Goal goal, TaskSpec reviewer, string candidateSha,
        CandidateIdentity identity, Func<FindingEvidenceRequest, FindingEvidenceRequest?> normalize,
        string executionBasis, out FindingEvidenceReviewContinuation? continuation, out string reason)
    {
        continuation = null;
        reason = "review-context-or-lineage-unavailable";
        var prior = reviewer.LastVerification ?? reviewer.VerificationHistory.LastOrDefault();
        if (reviewer.RequiredRole != AgentRole.Reviewer || prior?.CandidateIdentity is null ||
            !CandidateIdentity.AreSameCandidate(prior.CandidateIdentity, identity) ||
            !string.Equals(prior.ReviewedCommit, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            prior.DispatchStartedAt is not { } dispatchAt ||
            !WorkerResultBlockers.TryFindReviewFindingRound(prior, out var reviewRound, out _)) return false;
        var delivered = reviewer.DispatchHistory.SingleOrDefault(dispatch => dispatch.DispatchedAt == dispatchAt)
            ?.FindingEvidenceReviewContext;
        if (delivered is null || delivered.GoalId != goal.Id.Value || delivered.TaskId != reviewer.Id.Value ||
            delivered.DispatchAt != dispatchAt || !string.Equals(delivered.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            return false;
        var required = ReviewFindings.GetOpenBlockingFindings(reviewRound.Findings, goal.EffectiveAcceptanceCriteriaCorrections);
        reason = "source-correction-or-required-request-unavailable";
        if (required.Count == 0 || required.Any(finding => finding.Category != FindingCategory.TestEvidence || finding.EvidenceRequest is null))
            return false;
        var selected = new List<FindingEvidenceReviewSelection>();
        foreach (var finding in required)
        {
            reason = $"required-evidence-incomplete:{finding.StableId}";
            var request = normalize(finding.EvidenceRequest!);
            if (request is null) return false;
            var requestIdentity = ConductorDriver.BuildFindingEvidenceIdentity(request);
            FindingEvidenceReviewSelection? selection = null;
            IReadOnlyList<FindingEvidenceReviewSelection>? reusedSelection = null;
            foreach (var source in goal.Tasks.Where(task => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer))
            {
                if (source.Id != reviewer.Id && source.Status != WorkTaskStatus.Completed) continue;
                var current = source.LastVerification ?? source.VerificationHistory.LastOrDefault();
                var record = ResolveEvidenceVerification(source, current, finding.StableId);
                if (record?.DispatchStartedAt is not { } sourceDispatch ||
                    !string.Equals(record.ReviewedCommit, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                    !CandidateIdentity.AreSameCandidate(record.CandidateIdentity, identity) ||
                    !source.DispatchHistory.Any(dispatch => dispatch.DispatchedAt == sourceDispatch && dispatch.GoalId == goal.Id) ||
                    !WorkerResultBlockers.TryFindReviewFindingRound(record, out var round, out _)) continue;
                var sourceFinding = record.MergedReviewFindings?.SingleOrDefault(item => item.StableId == finding.StableId);
                if (sourceFinding?.EvidenceRequest is null || normalize(sourceFinding.EvidenceRequest) is not { } sourceRequest ||
                    ConductorDriver.BuildFindingEvidenceIdentity(sourceRequest) != requestIdentity ||
                    sourceFinding.EvidenceOutcome is not { Honoured: true, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                        ReceiptId: { Length: > 0 } receiptId } ||
                    sourceFinding.EvidenceOutcome.RequestedSelectionIdentity != requestIdentity) continue;
                var roundId = ConductorDriver.BuildFindingRoundFingerprint(source.Id, record.CompletedAt, round);
                if (TrySelectRecordedCoverage(goal, source, record, sourceFinding, request, requestIdentity, roundId,
                        candidateSha, identity, executionBasis, out reusedSelection)) break;
                var receipts = record.FindingEvidenceReceipts ?? [];
                var receipt = receipts.LastOrDefault(item => item.ReceiptId == receiptId);
                if (receipt is null || !Qualifies(receipt, finding.StableId, requestIdentity, roundId, candidateSha, executionBasis, request)) continue;
                if (receipts.LastOrDefault(item => item.FindingRoundFingerprint == roundId &&
                        item.ExecutionBasisIdentity == executionBasis &&
                        string.Equals(item.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                        item.RequestDispositions?.Any(disposition => disposition.RequestIdentity == requestIdentity) == true)
                    ?.ReceiptId != receiptId) continue;
                // A later result for this request, including a RED or refused result, supersedes it.
                var disposition = record.FindingEvidenceRequestDispositions?.LastOrDefault(item =>
                    item.FindingStableId == finding.StableId && item.RequestIdentity == requestIdentity &&
                    item.FindingRoundFingerprint == roundId && item.ExecutionBasisIdentity == executionBasis &&
                    string.Equals(item.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase));
                if (disposition?.Decision is not (FindingEvidenceRequestDecision.Executed or FindingEvidenceRequestDecision.CoveredByReceipt) ||
                    disposition.SourceReceiptIds?.Contains(receiptId, StringComparer.Ordinal) != true) continue;
                selection = new(finding.StableId, requestIdentity, source.Id.Value, record.CompletedAt, sourceDispatch,
                    roundId, receiptId, FindingEvidenceReviewContext.ExecutionIdentity(receipt), roundId);
                break;
            }
            if (reusedSelection is { Count: > 0 })
            {
                selected.AddRange(reusedSelection);
                continue;
            }
            if (selection is null) return false;
            selected.Add(selection);
        }
        reason = "evidence-already-delivered";
        if (!selected.Any(item => !delivered.ReceiptIds.Contains(item.ReceiptId, StringComparer.Ordinal) &&
                !delivered.ExecutionIdentities.Contains(item.ExecutionIdentity, StringComparer.Ordinal))) return false;
        continuation = new(goal.Id.Value, reviewer.Id.Value, candidateSha, identity, prior.CompletedAt, dispatchAt,
            delivered.ExecutionIdentities, selected.OrderBy(item => item.FindingId, StringComparer.Ordinal).ToArray());
        reason = "complete-newly-applicable-evidence-round";
        return true;
    }

    // Receipt closure is a decision over an existing worker round, not a new execution round.
    // Recover only its explicitly retained finding/outcome and receipt lineage, never arbitrary history.
    private static TaskVerificationRecord? ResolveEvidenceVerification(TaskSpec source,
        TaskVerificationRecord? current, string findingId)
    {
        if (source.RequiredRole != AgentRole.Tester || current?.Command != "conductor finding-receipt-closure")
            return current;
        var finding = current.MergedReviewFindings?.SingleOrDefault(item => item.StableId == findingId);
        if (finding is not { State: ReviewFindingState.Resolved, EvidenceOutcome.ReceiptId: { Length: > 0 } receiptId } ||
            current.FindingEvidenceReceipts?.Any(receipt => receipt.ReceiptId == receiptId) != true) return null;
        return source.VerificationHistory.LastOrDefault(record =>
            record.Command != "conductor finding-receipt-closure" && record.CompletedAt <= current.CompletedAt &&
            string.Equals(record.ReviewedCommit, current.ReviewedCommit, StringComparison.OrdinalIgnoreCase) &&
            (current.DispatchStartedAt is null || current.DispatchStartedAt == record.DispatchStartedAt) &&
            record.MergedReviewFindings?.Any(item => item.StableId == findingId &&
                item.EvidenceOutcome?.ReceiptId == receiptId &&
                item.EvidenceOutcome.RequestedSelectionIdentity == finding.EvidenceOutcome.RequestedSelectionIdentity &&
                item.EvidenceOutcome is { Honoured: true, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence } &&
                item.EvidenceRequest is not null && finding.EvidenceRequest is not null &&
                ConductorDriver.BuildFindingEvidenceIdentity(item.EvidenceRequest) ==
                    ConductorDriver.BuildFindingEvidenceIdentity(finding.EvidenceRequest)) == true &&
            record.FindingEvidenceReceipts?.Any(receipt => receipt.ReceiptId == receiptId) == true);
    }

    // The canonical transition intentionally ignores receipt copies and file locations. Launch
    // still needs the exact claimed receipt and its retained artifacts to remain authoritative.
    internal static bool ClaimedSelectionsRemainValid(Goal goal, FindingEvidenceReviewContinuation claimed,
        FindingEvidenceReviewContinuation validated, string executionBasis) =>
        claimed.Selections.Count > 0 && claimed.Selections.All(selection =>
        {
            var source = goal.Tasks.SingleOrDefault(task => task.Id.Value == selection.SourceTaskId);
            var origin = source?.VerificationHistory.SingleOrDefault(record =>
                record.CompletedAt == selection.SourceVerificationAt && record.DispatchStartedAt == selection.SourceDispatchAt);
            var receipt = origin?.FindingEvidenceReceipts?.LastOrDefault(item => item.ReceiptId == selection.ReceiptId);
            var equivalent = validated.Selections.SingleOrDefault(item => item.Canonical == selection.Canonical);
            var validatedReceipt = origin?.FindingEvidenceReceipts?.LastOrDefault(item => item.ReceiptId == equivalent?.ReceiptId);
            var executed = validatedReceipt?.RequestDispositions?.Where(item =>
                item.Decision == FindingEvidenceRequestDecision.Executed).ToArray();
            return origin is not null && source!.DispatchHistory.Any(dispatch =>
                    dispatch.GoalId == goal.Id && dispatch.DispatchedAt == selection.SourceDispatchAt) &&
                CandidateIdentity.AreSameCandidate(origin.CandidateIdentity, claimed.CandidateIdentity) &&
                WorkerResultBlockers.TryFindReviewFindingRound(origin, out var round, out _) &&
                ConductorDriver.BuildFindingRoundFingerprint(source.Id, origin.CompletedAt, round) == selection.FindingRoundFingerprint &&
                receipt is not null && executed is { Length: > 0 } && validatedReceipt is not null &&
                ConductorDriver.BuildFindingEvidenceIdentity(receipt.Request) ==
                    ConductorDriver.BuildFindingEvidenceIdentity(validatedReceipt.Request) &&
                FindingEvidenceReviewContext.ExecutionIdentity(receipt) == selection.ExecutionIdentity &&
                executed.All(binding => Qualifies(receipt, binding.FindingStableId, binding.RequestIdentity,
                    selection.FindingRoundFingerprint, claimed.CandidateSha, executionBasis, validatedReceipt.Request));
        });

    // Preserve the existing recorded coverage-reuse contract. Every source receipt must still
    // be complete for its OWN request; an arbitrary union of partial executions is ineligible.
    private static bool TrySelectRecordedCoverage(Goal goal, TaskSpec source, TaskVerificationRecord current,
        ReviewFinding finding, FindingEvidenceRequest request, string requestId, string requestRound,
        string candidateSha, CandidateIdentity identity, string executionBasis,
        out IReadOnlyList<FindingEvidenceReviewSelection>? selected)
    {
        selected = null;
        var outcome = finding.EvidenceOutcome!;
        if (outcome.DecisionReason?.StartsWith("reused-covered-green:", StringComparison.Ordinal) != true ||
            outcome.RequestedSelectionIdentity != requestId || outcome.SourceReceiptIds is not { Count: > 0 } sourceIds ||
            !ConductorDriver.TryResolveFindingEvidenceCoverage(source, request, candidateSha, executionBasis,
                out var receipts, out var uncovered, out _) || uncovered.Count > 0 ||
            !sourceIds.Order(StringComparer.Ordinal).SequenceEqual(receipts.Select(receipt => receipt.ReceiptId).Order(StringComparer.Ordinal)))
            return false;
        var binding = current.FindingEvidenceRequestDispositions?.LastOrDefault(item =>
            item.FindingStableId == finding.StableId && item.RequestIdentity == requestId &&
            item.FindingRoundFingerprint == requestRound && item.ExecutionBasisIdentity == executionBasis &&
            string.Equals(item.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase));
        if (binding?.Decision != FindingEvidenceRequestDecision.CoveredByReceipt ||
            binding.SourceReceiptIds is null ||
            !sourceIds.Order(StringComparer.Ordinal).SequenceEqual(binding.SourceReceiptIds.Order(StringComparer.Ordinal))) return false;
        var result = new List<FindingEvidenceReviewSelection>();
        foreach (var receipt in receipts)
        {
            var origin = source.VerificationHistory.FirstOrDefault(record =>
                CandidateIdentity.AreSameCandidate(record.CandidateIdentity, identity) &&
                record.FindingEvidenceReceipts?.Any(item => item.ReceiptId == receipt.ReceiptId) == true &&
                WorkerResultBlockers.TryFindReviewFindingRound(record, out var round, out _) &&
                ConductorDriver.BuildFindingRoundFingerprint(source.Id, record.CompletedAt, round) == receipt.FindingRoundFingerprint);
            if (origin?.DispatchStartedAt is not { } dispatchAt ||
                !source.DispatchHistory.Any(dispatch => dispatch.DispatchedAt == dispatchAt && dispatch.GoalId == goal.Id)) return false;
            var executed = receipt.RequestDispositions?.FirstOrDefault(item => item.Decision == FindingEvidenceRequestDecision.Executed);
            if (executed is null || !Qualifies(receipt, executed.FindingStableId, executed.RequestIdentity,
                    receipt.FindingRoundFingerprint!, candidateSha, executionBasis, receipt.Request)) return false;
            result.Add(new(finding.StableId, requestId, source.Id.Value, origin.CompletedAt, dispatchAt,
                receipt.FindingRoundFingerprint!, receipt.ReceiptId, FindingEvidenceReviewContext.ExecutionIdentity(receipt), requestRound));
        }
        selected = result;
        return true;
    }

    private static bool Qualifies(FindingEvidenceReceipt receipt, string findingId, string requestIdentity,
        string roundId, string candidateSha, string executionBasis, FindingEvidenceRequest request) =>
        receipt is { Accepted: true, Passed: true } &&
        string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
        receipt.FindingRoundFingerprint == roundId && receipt.ExecutionBasisIdentity == executionBasis &&
        request.NegativeControl == receipt.Request.NegativeControl &&
        FindingEvidenceRevertPaths.SamePaths(request.RevertPaths, receipt.Request.RevertPaths) &&
        FindingEvidenceMutation.Same(request.Mutation, receipt.Request.Mutation) &&
        FindingEvidenceRoundPlanner.ExpandSelections(request.Selections).All(selection =>
            FindingEvidenceRoundPlanner.ExpandSelections(receipt.Request.Selections).Contains(selection)) &&
        receipt.RequestDispositions?.Any(item => item.FindingStableId == findingId && item.RequestIdentity == requestIdentity &&
            item.FindingRoundFingerprint == roundId && item.ExecutionBasisIdentity == executionBasis &&
            string.Equals(item.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            item.Decision == FindingEvidenceRequestDecision.Executed &&
            item.SourceReceiptIds?.Contains(receipt.ReceiptId, StringComparer.Ordinal) == true) == true &&
        (receipt.Arms ?? []).Count(arm => arm.Arm == FindingEvidenceArm.Candidate) == 1 &&
        (receipt.Arms ?? []).Any(arm => arm is { Arm: FindingEvidenceArm.Candidate, Accepted: true, Passed: true,
            Disposition: FindingEvidenceArmDisposition.Green, ExecutedTestCount: > 0 } && arm.NotRunSelections.Count == 0 &&
            string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            FindingEvidenceTrxCoverage.HasPassedSelections(arm, request)) &&
        (request.NegativeControl is null || receipt.NegativeControlOutcome == FindingEvidenceNegativeControlOutcome.Demonstrated &&
            (receipt.Arms ?? []).Count(arm => arm.Arm == FindingEvidenceArm.SourceReverted) == 1 &&
            (receipt.Arms ?? []).Any(arm => arm is { Arm: FindingEvidenceArm.SourceReverted, Accepted: true, Passed: false,
                Disposition: FindingEvidenceArmDisposition.Red, ExecutedTestCount: > 0 } && arm.NotRunSelections.Count == 0 &&
                string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                FindingEvidenceTrxCoverage.HasRedSelections(arm, request)));

    internal static bool SuppliedReceiptsPassAtCandidate(
        TaskSpec task, string candidateSha, IReadOnlyList<string> receiptIds)
    {
        if (receiptIds.Count == 0 || !ConductorGitRevisionReader.IsValid(candidateSha)) return false;
        var receipts = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).ToArray();
        return receiptIds.All(id =>
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var receipt = receipts.LastOrDefault(candidate =>
                string.Equals(candidate.ReceiptId, id, StringComparison.Ordinal));
            return receipt is { Accepted: true, Passed: true } &&
                ConductorGitRevisionReader.IsValid(receipt.CandidateSha) &&
                string.Equals(receipt.CandidateSha.Trim(), candidateSha.Trim(), StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static bool TrySelectReceiptClosableFindings(
        Goal goal,
        TaskSpec task,
        string candidateSha,
        out IReadOnlyList<(ReviewFinding Finding, FindingEvidenceReceipt Receipt)> closable)
    {
        var diagnosis = FindingReceiptClosureDiagnosis.Evaluate(goal, task, candidateSha);
        closable = diagnosis.ClosableFindings;
        return diagnosis.IsClosable;
    }
}
