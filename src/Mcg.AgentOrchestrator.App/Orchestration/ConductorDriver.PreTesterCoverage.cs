using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal sealed record PreTesterCoverageSource(string? ReceiptId, FindingEvidenceReceipt? Receipt,
        IReadOnlyList<FindingEvidenceSelection> Selections, DateTimeOffset CompletedAt);

    private IReadOnlyList<PreTesterCoverageSource> ReadPreTesterCoverage(Goal goal, string candidateSha, string basis)
    {
        return _focusedEvidenceAttemptCoordinator.ReadFocusedCoverageAttempts(goal.Id)
            .Where(attempt => attempt.FindingRoundFingerprint == "pre-tester-deferred" &&
                string.Equals(attempt.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                (attempt.FocusedExecutionBasisIdentity is null || attempt.FocusedExecutionBasisIdentity == basis))
            .Select(attempt =>
            {
                var receipt = _focusedEvidenceAttemptCoordinator.ReadOriginalFocusedReceipt(attempt);
                if (receipt is not null && !IsReusableGreenFindingEvidenceReceipt(receipt, candidateSha, basis)) receipt = null;
                return new PreTesterCoverageSource(attempt.FocusedEvidenceReceiptId, receipt,
                    (attempt.FocusedEvidenceMemberRequests ?? []).SelectMany(ParseDispositionSelections).ToArray(),
                    attempt.CompletedAt ?? attempt.StartedAt);
            }).ToArray();
    }

    // Resume the already selected remainder even if its reused source was invalidated while
    // it ran. Only scheduling is retained here; completion revalidates every reused source.
    private bool TryResumePreTesterRemainder(Goal goal, TaskSpec task, string candidateSha,
        string round, string identity, string basis, FindingEvidenceRequest request,
        out FindingEvidenceRequest remainder, out IReadOnlyList<FindingEvidenceReceipt> sources)
    {
        remainder = request;
        sources = [];
        if (request.NegativeControl is not null || request.RevertPaths is not null || request.Mutation is not null) return false;
        var attempts = _focusedEvidenceAttemptCoordinator.ReadFocusedCoverageAttempts(goal.Id);
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
            .Select(source => source.OriginalFocusedReceipt).OfType<FindingEvidenceReceipt>().ToArray();
        remainder = request with { Selections = (attempt.FocusedEvidenceMemberRequests ?? []).SelectMany(ParseDispositionSelections).ToArray() };
        return remainder.Selections.Count > 0;
    }

    private IReadOnlyList<FindingEvidenceReceipt> RevalidatePreTesterSources(Goal goal, TaskSpec task,
        FindingEvidenceRequest originalRequest, string candidateSha, string basis,
        IReadOnlyList<FindingEvidenceReceipt> sources)
    {
        if (!sources.Any(source => source.FindingRoundFingerprint == "pre-tester-deferred")) return sources;
        if (!TryNormalizeFindingEvidenceRequest(originalRequest, _getFindingEvidenceEngineSettings(goal),
                (project, name) => _resolveFindingEvidenceSiblingClasses(goal, project, name),
                out originalRequest, out _, out _, out _)) return [];
        var liveSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!string.Equals(liveSha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            BuildFindingEvidenceExecutionBasisIdentity(_getFindingEvidenceEngineSettings(goal)) != basis) return [];
        TryResolveFindingEvidenceCoverage(task, originalRequest, candidateSha, basis,
            out var current, out _, out _, ReadPreTesterCoverage(goal, candidateSha, basis));
        return sources.Where(source => current.Any(valid => valid.ReceiptId == source.ReceiptId)).ToArray();
    }

    private bool CombinedPreTesterCoverageIsComplete(Goal goal, FindingEvidenceRequest request,
        FindingEvidenceReceipt executed, IReadOnlyList<FindingEvidenceReceipt> reused)
    {
        if (!TryNormalizeFindingEvidenceRequest(request, _getFindingEvidenceEngineSettings(goal),
                (project, name) => _resolveFindingEvidenceSiblingClasses(goal, project, name),
                out var normalized, out _, out _, out _)) return false;
        var coverage = reused.Append(executed).SelectMany(receipt =>
            FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, FormatFindingEvidenceSelection)).ToHashSet();
        return coverage.IsSupersetOf(FindingEvidenceRoundPlanner.ExpandSelections(normalized.Selections));
    }
}
