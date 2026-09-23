using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Records only the obligations owned by a successful full gate; other evidence still blocks landing.
internal static class AcceptanceCriterionEvidence
{
    public static ConductorAdvanceOutcome.Held? RecordAndCreateHold(Goal goal, string? candidateSha, AgentOrchestratorKernel? kernel)
    {
        var diagnostic = RecordAndDescribeOutstanding(goal, candidateSha, kernel);
        return diagnostic is null ? null : new ConductorAdvanceOutcome.Held(
            GoalLifecycleState.Verified, diagnostic, StableIdentity: HoldIdentity(goal, candidateSha));
    }

    public static string HoldIdentity(Goal goal, string? candidateSha)
    {
        var obligations = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => $"{item.Id}:{item.FindingStableId}:{item.Owner}:{item.State}:{item.RequiredScope}");
        var payload = $"{candidateSha}:{goal.AuthoritativeRefinedSpecVersion?.Version}:{string.Join("|", obligations)}";
        return "criterion-evidence:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    public static string? RecordAndDescribeOutstanding(Goal goal, string? candidateSha, AgentOrchestratorKernel? kernel)
    {
        var diagnostic = RecordFullAcceptanceEvidence(goal, candidateSha, kernel, evidenceSource: null);
        if (diagnostic is not null) return diagnostic;
        var outstanding = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
            .Where(IsBoundOrNonAcceptanceObligation)
            .ToArray();
        if (outstanding.Length == 0) return null;
        var detail = string.Join(", ", outstanding.Select(item =>
            $"{item.Id}:{item.Owner}:{item.State}:next={item.RequiredScope}"));
        return $"Acceptance completed but required criterion evidence remains outstanding: {detail}.";
    }

    public static string? RebindRecordAndDescribeOutstanding(
        IReadOnlyList<Goal> goals,
        Func<GoalId, string?> resolveCandidateSha,
        AgentOrchestratorKernel? kernel,
        string evidenceSource)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(resolveCandidateSha);
        var diagnostics = new List<string>();
        foreach (var goal in goals)
        {
            var diagnostic = RebindRecordAndDescribeOutstanding(
                goal,
                resolveCandidateSha(goal.Id),
                kernel,
                evidenceSource);
            if (diagnostic is not null)
            {
                diagnostics.Add($"goal {goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]}: {diagnostic}");
            }
        }

        return diagnostics.Count == 0 ? null : string.Join(" ", diagnostics);
    }

    public static string? RebindRecordAndDescribeOutstanding(
        Goal goal,
        string? candidateSha,
        AgentOrchestratorKernel? kernel,
        string evidenceSource)
    {
        if (kernel is null || string.IsNullOrWhiteSpace(candidateSha))
        {
            return "Acceptance passed but criterion evidence could not be recorded: the authoritative kernel or candidate SHA is unavailable. No obligation was resolved.";
        }

        var normalizedCandidate = candidateSha.Trim();
        var acceptanceObligations = goal.OutstandingCriterionEvidenceObligations.Where(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                item.State == CriterionEvidenceState.Pending &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .ToArray();
        foreach (var obligation in acceptanceObligations)
        {
            if (!string.Equals(obligation.ExpectedCandidateSha, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                kernel.MapCriterionEvidenceOwner(
                    goal.Id,
                    obligation.CriterionIndex,
                    obligation.CriterionVersion,
                    CriterionEvidenceOwner.Acceptance,
                    "conductor deterministic full acceptance",
                    obligation.RequiredScope,
                    obligation.FindingStableId,
                    normalizedCandidate);
            }
        }

        var diagnostic = RecordFullAcceptanceEvidence(goal, normalizedCandidate, kernel, evidenceSource);
        if (diagnostic is not null) return diagnostic;
        var outstanding = goal.GetOutstandingCriterionEvidenceObligations(normalizedCandidate)
            .Where(IsBoundOrNonAcceptanceObligation)
            .ToArray();
        if (outstanding.Length == 0) return null;
        var detail = string.Join(", ", outstanding.Select(item =>
            $"{item.Id}:{item.Owner}:{item.State}:next={item.RequiredScope}"));
        return $"Acceptance completed but required criterion evidence remains outstanding: {detail}.";
    }

    private static string? RecordFullAcceptanceEvidence(
        Goal goal,
        string? candidateSha,
        AgentOrchestratorKernel? kernel,
        string? evidenceSource)
    {
        var outstanding = goal.GetOutstandingCriterionEvidenceObligations(candidateSha);
        if (outstanding.Count == 0) return null;
        if (kernel is null || string.IsNullOrWhiteSpace(candidateSha))
        {
            return "Acceptance passed but criterion evidence could not be recorded: the authoritative kernel or candidate SHA is unavailable. No obligation was resolved.";
        }

        var matching = outstanding
            .Where(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .ToArray();
        foreach (var obligation in matching)
        {
            if (!string.Equals(obligation.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
                return $"Acceptance passed for {candidateSha}, but obligation '{obligation.Id}' is bound to {obligation.ExpectedCandidateSha ?? "no candidate"}. Rebind the obligation to the current candidate before recording its evidence.";
            // This receipt is generated by the conductor only after the normal
            // deterministic acceptance verifier returned success. External
            // operator intents cannot submit Acceptance-owned receipts.
            kernel.RecordCriterionEvidence(
                goal.Id,
                obligation.Id,
                CriterionEvidenceOwner.Acceptance,
                candidateSha,
                $"full-acceptance:{candidateSha}",
                CriterionEvidenceScopes.FullAcceptanceGate,
                passed: true,
                detail: string.IsNullOrWhiteSpace(evidenceSource)
                    ? "Normal deterministic full acceptance passed for the mapped candidate."
                    : $"Deterministic full acceptance passed for the mapped candidate; source={evidenceSource}.");
        }

        return null;
    }

    private static bool IsBoundOrNonAcceptanceObligation(CriterionEvidenceObligation obligation) =>
        obligation.Owner != CriterionEvidenceOwner.Acceptance ||
        !string.IsNullOrWhiteSpace(obligation.ExpectedCandidateSha);

}
