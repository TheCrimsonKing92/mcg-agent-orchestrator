using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Records only the obligations owned by a successful full gate; other evidence still blocks landing.
internal static partial class AcceptanceCriterionEvidence
{
    public static ConductorAdvanceOutcome.Held? RecordAndCreateHold(Goal goal, string? candidateSha, AgentOrchestratorKernel? kernel, string? executionDirectory = null)
    {
        var diagnostic = RecordAndDescribeOutstanding(goal, candidateSha, kernel, executionDirectory);
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

    // Only operator-owned criteria require train admission evidence. Ordinary solo and
    // cohort evidence checks continue to use their existing obligation set.
    internal static string? DescribeTrainOperatorEvidenceGap(Goal goal)
    {
        var version = goal.AuthoritativeRefinedSpecVersion;
        if (version is null)
            return "Train admission requires an authoritative refined spec to establish operator-owned criteria.";
        if (version.Spec.OperatorOwnedAcceptanceCriteria.Count == 0)
            return null;

        var obligations = goal.CriterionEvidenceObligations
            .Where(item => item.CriterionVersion == version.Version && item.State != CriterionEvidenceState.Repaired)
            .ToArray();
        var missing = new List<string>();
        foreach (var criterion in version.Spec.OperatorOwnedAcceptanceCriteria)
        {
            var index = version.Spec.AcceptanceCriteria.ToList().FindIndex(item =>
                string.Equals(item.Trim(), criterion.Trim(), StringComparison.Ordinal));
            if (index < 0)
            {
                missing.Add("operator-owned criterion has no acceptance index");
                continue;
            }
            if (!obligations.Any(item => item.CriterionIndex == index &&
                    item.Owner == CriterionEvidenceOwner.Operator))
            {
                missing.Add($"{CriterionEvidenceObligation.DescribeCriterion(version.Version, index)}:Operator:NotCreated:next=operator observation");
            }
        }
        return missing.Count == 0 ? null :
            $"Acceptance completed but required criterion evidence remains outstanding: {string.Join(", ", missing)}.";
    }

    public static string? RecordAndDescribeOutstanding(Goal goal, string? candidateSha, AgentOrchestratorKernel? kernel, string? executionDirectory = null)
    {
        var diagnostic = RecordFullAcceptanceEvidence(goal, candidateSha, kernel, evidenceSource: null, executionDirectory);
        if (diagnostic is not null) return diagnostic;
        var outstanding = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
            .Where(IsBoundOrNonAcceptanceObligation)
            .ToArray();
        if (outstanding.Length == 0) return null;
        var detail = string.Join(", ", outstanding.Select(item =>
            $"{item.DisplayLabel}:{item.Owner}:{item.State}:next={item.RequiredScope}"));
        return $"Acceptance completed but required criterion evidence remains outstanding: {detail}.";
    }

    public static string? RebindRecordAndDescribeOutstanding(
        IReadOnlyList<Goal> goals,
        Func<GoalId, string?> resolveCandidateSha,
        AgentOrchestratorKernel? kernel,
        string evidenceSource,
        string? executionDirectory)
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
                evidenceSource,
                executionDirectory);
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
        string evidenceSource,
        string? executionDirectory)
    {
        if (kernel is null || string.IsNullOrWhiteSpace(candidateSha))
        {
            return "Acceptance passed but criterion evidence could not be recorded: the authoritative kernel or candidate SHA is unavailable. No obligation was resolved.";
        }

        var normalizedCandidate = candidateSha.Trim();
        var rebindDiagnostic = TryRebindPendingAcceptanceObligations(
            goal, normalizedCandidate, kernel, executionDirectory);
        if (rebindDiagnostic is not null) return rebindDiagnostic;

        var diagnostic = RecordFullAcceptanceEvidence(goal, normalizedCandidate, kernel, evidenceSource, executionDirectory: null);
        if (diagnostic is not null) return diagnostic;
        var outstanding = goal.GetOutstandingCriterionEvidenceObligations(normalizedCandidate)
            .Where(IsBoundOrNonAcceptanceObligation)
            .ToArray();
        if (outstanding.Length == 0) return null;
        var detail = string.Join(", ", outstanding.Select(item =>
            $"{item.DisplayLabel}:{item.Owner}:{item.State}:next={item.RequiredScope}"));
        return $"Acceptance completed but required criterion evidence remains outstanding: {detail}.";
    }

    public static string? RebindRecordFromPassedCandidateAndDescribeOutstanding(
        Goal goal,
        string candidateSha,
        string mainSha,
        AgentOrchestratorKernel kernel,
        string executionDirectory)
    {
        var hasPendingAcceptanceObligation = goal.OutstandingCriterionEvidenceObligations.Any(item =>
            item.Owner == CriterionEvidenceOwner.Acceptance &&
            item.State == CriterionEvidenceState.Pending &&
            string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal));
        var passedOutcome = hasPendingAcceptanceObligation
            ? GoalOperationJournal.NewestAcceptanceOutcomeForCandidate(
                GoalOperationJournal.Read(executionDirectory, goal.Id),
                candidateSha,
                mainSha)
            : null;
        if (hasPendingAcceptanceObligation &&
            passedOutcome?.AcceptanceOutcome is not ("passed" or "gate-passed"))
        {
            return $"Acceptance-owned criterion evidence remains outstanding, but no deterministic passed acceptance outcome matches candidate {candidateSha} on main {mainSha}. No obligation was resolved.";
        }

        return RebindRecordAndDescribeOutstanding(
            goal,
            candidateSha,
            kernel,
            passedOutcome is null
                ? "landing-executor current deterministic acceptance outcome"
                : $"goal-operation:{passedOutcome.Operation}",
            executionDirectory);
    }

    private static string? RecordFullAcceptanceEvidence(
        Goal goal,
        string? candidateSha,
        AgentOrchestratorKernel? kernel,
        string? evidenceSource,
        string? executionDirectory)
    {
        var outstanding = goal.GetOutstandingCriterionEvidenceObligations(candidateSha);
        if (outstanding.Count == 0) return null;
        if (kernel is null || string.IsNullOrWhiteSpace(candidateSha))
        {
            return "Acceptance passed but criterion evidence could not be recorded: the authoritative kernel or candidate SHA is unavailable. No obligation was resolved.";
        }

        TryCarryPatchEquivalentBindings(goal, candidateSha, kernel, executionDirectory, out var carryRefusal);
        outstanding = goal.GetOutstandingCriterionEvidenceObligations(candidateSha);
        var matching = outstanding
            .Where(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .ToArray();
        foreach (var obligation in matching)
        {
            if (!string.Equals(obligation.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
                return FormatRefusedCarryDiagnostic(candidateSha, obligation, carryRefusal);
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

        if (carryRefusal is not null)
        {
            var operatorObligations = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
                .Where(item => item.Owner == CriterionEvidenceOwner.Operator &&
                    item.State == CriterionEvidenceState.Satisfied &&
                    !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (operatorObligations.Length > 0)
            {
                var detail = string.Join(", ", operatorObligations.Select(item =>
                    $"{item.DisplayLabel}:{item.Owner}:{item.State}:next={item.RequiredScope}"));
                return $"Acceptance completed but required criterion evidence remains outstanding: {detail}. Carry-forward refused: {carryRefusal}.";
            }
        }

        return null;
    }

    private static bool TryCarryPatchEquivalentBindings(
        Goal goal,
        string candidateSha,
        AgentOrchestratorKernel kernel,
        string? executionDirectory,
        out string? refusal)
    {
        if (!TryPlanPatchEquivalentBindings(goal, candidateSha, executionDirectory,
                out var eligible, out var oldHeads, out var evidenceByHead, out refusal))
            return false;

        foreach (var obligation in eligible)
        {
            var oldReceiptId = obligation.ReceiptId;
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                obligation.CriterionIndex,
                obligation.CriterionVersion,
                obligation.Owner,
                "conductor patch-equivalent rebase carry",
                obligation.RequiredScope,
                obligation.FindingStableId,
                candidateSha);
            if (obligation.Owner == CriterionEvidenceOwner.Operator)
            {
                kernel.RecordCriterionEvidence(
                    goal.Id,
                    obligation.Id,
                    CriterionEvidenceOwner.Operator,
                    candidateSha,
                    $"{oldReceiptId}:carried:{candidateSha}",
                    obligation.RequiredScope,
                    passed: true,
                    detail: $"Carried operator evidence from {obligation.ExpectedCandidateSha} to patch-equivalent candidate {candidateSha}; {evidenceByHead[obligation.ExpectedCandidateSha!]}.");
            }
            else if (obligation.State == CriterionEvidenceState.Satisfied)
            {
                GoalOperationJournal.Completed(
                    executionDirectory,
                    goal,
                    "conductor:criterion-evidence-satisfied-acceptance-carry",
                    $"obligation={obligation.Id}; oldHead={obligation.ExpectedCandidateSha}; newHead={candidateSha}; reason=patch-equivalent carry of satisfied acceptance obligation");
            }
        }

        GoalOperationJournal.Completed(
            executionDirectory,
            goal,
            "conductor:criterion-evidence-patch-carry",
            $"oldHeads={string.Join(',', oldHeads)}; newHead={candidateSha}; equivalence={string.Join(" | ", evidenceByHead.Values)}");
        return true;
    }

    private static bool IsBoundOrNonAcceptanceObligation(CriterionEvidenceObligation obligation) =>
        obligation.Owner != CriterionEvidenceOwner.Acceptance ||
        !string.IsNullOrWhiteSpace(obligation.ExpectedCandidateSha);

}
