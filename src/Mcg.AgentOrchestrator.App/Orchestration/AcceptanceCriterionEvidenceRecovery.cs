using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceCriterionEvidenceRecoveryResult(
    string SourceKind,
    string SourceId,
    string CandidateSha,
    string? HoldDiagnostic)
{
    public bool CanComplete => HoldDiagnostic is null;

    public string AuditDetail =>
        $"sourceKind={SourceKind}; sourceId={SourceId}; certifiedCandidate={CandidateSha}";
}

internal static class AcceptanceCriterionEvidenceRecovery
{
    public static AcceptanceCriterionEvidenceRecoveryResult? TryRecord(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string executionDirectory,
        string orchestratorDirectory,
        string? mainSha,
        MergeTrainAcceptanceStore mergeTrainStore,
        CohortAcceptanceStore cohortStore,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        if (goal.Status != GoalStatus.Verified ||
            string.IsNullOrWhiteSpace(mainSha) ||
            !goal.OutstandingCriterionEvidenceObligations.Any(IsPendingAcceptanceObligation))
        {
            return null;
        }

        foreach (var receipt in mergeTrainStore.ReadPassedReceiptsForGoal(goal.Id))
        {
            var member = receipt.Identity.Members.Single(item => item.GoalId == goal.Id);
            if (!IsAncestor(executionDirectory, member.CandidateRevision, mainSha, gitRunner))
            {
                continue;
            }

            return Record(
                kernel,
                goal,
                "merge-train-receipt",
                receipt.ReceiptId,
                member.CandidateRevision);
        }

        foreach (var receipt in cohortStore.ReadPassedReceiptsForGoal(goal.Id))
        {
            var member = receipt.Identity.Members.Single(item => item.GoalId == goal.Id);
            if (!IsAncestor(executionDirectory, member.CandidateRevision, mainSha, gitRunner))
            {
                continue;
            }

            return Record(
                kernel,
                goal,
                "cohort-receipt",
                receipt.ReceiptId,
                member.CandidateRevision);
        }

        var attempts = GoalTerminalReconciliationEvidenceResolver.ResolveForGoal(
                Path.Combine(orchestratorDirectory, "acceptance-gate-attempts"),
                goal.Id)
            .Where(item =>
                (item.State == GoalTerminalReconciliationEvidenceState.Present && item.RawExitCode == 0 ||
                 item.State == GoalTerminalReconciliationEvidenceState.MissingByInProcessProtocol && item.RawExitCode is null) &&
                item.AcceptancePassed == true &&
                string.Equals(item.TypedKind, "accepted", StringComparison.OrdinalIgnoreCase))
            .Select(item => TryReadAttempt(item, goal.Id))
            .Where(item => item is not null)
            .Cast<AcceptanceAttemptCandidate>()
            .OrderByDescending(item => item.StartedAt)
            .ThenByDescending(item => item.AttemptId, StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (!IsAncestor(executionDirectory, attempt.CandidateSha, mainSha, gitRunner))
            {
                continue;
            }

            return Record(
                kernel,
                goal,
                "acceptance-attempt",
                attempt.AttemptId,
                attempt.CandidateSha);
        }

        return null;
    }

    private static AcceptanceCriterionEvidenceRecoveryResult Record(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string sourceKind,
        string sourceId,
        string candidateSha)
    {
        var diagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
            goal,
            candidateSha,
            kernel,
            $"{sourceKind}:{sourceId}");
        return new AcceptanceCriterionEvidenceRecoveryResult(
            sourceKind,
            sourceId,
            candidateSha,
            diagnostic);
    }

    private static AcceptanceAttemptCandidate? TryReadAttempt(
        GoalTerminalReconciliationEvidence evidence,
        GoalId expectedGoalId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(evidence.AttemptMetadataPath));
            var root = document.RootElement;
            if (!TryGetString(root, "goalId", out var goalId) ||
                !goalId.Equals(expectedGoalId.Value, StringComparison.Ordinal) ||
                !TryGetString(root, "branchHeadSha", out var candidateSha) ||
                !TryGetString(root, "kind", out var kind) ||
                !kind.Equals(ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind, StringComparison.Ordinal) ||
                HasFocusedEvidenceRequest(root))
            {
                return null;
            }

            candidateSha = MergeTrainMemberBinding.NormalizeRevision(candidateSha, "branchHeadSha");
            var startedAt = TryGetString(root, "startedAt", out var startedAtText) &&
                DateTimeOffset.TryParse(startedAtText, out var parsedStartedAt)
                ? parsedStartedAt
                : DateTimeOffset.MinValue;
            return new AcceptanceAttemptCandidate(evidence.AttemptId, candidateSha, startedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasFocusedEvidenceRequest(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("focusedEvidenceRequest", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind != JsonValueKind.Null &&
                    (property.Value.ValueKind != JsonValueKind.String ||
                     !string.IsNullOrWhiteSpace(property.Value.GetString()));
            }
        }

        return false;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                value = property.Value.GetString()!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsAncestor(
        string executionDirectory,
        string candidateSha,
        string mainSha,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner) =>
        gitRunner(executionDirectory, ["merge-base", "--is-ancestor", candidateSha, mainSha]).ExitCode == 0;

    private static bool IsPendingAcceptanceObligation(CriterionEvidenceObligation obligation) =>
        obligation.Owner == CriterionEvidenceOwner.Acceptance &&
        obligation.State == CriterionEvidenceState.Pending &&
        string.Equals(obligation.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal);

    private sealed record AcceptanceAttemptCandidate(
        string AttemptId,
        string CandidateSha,
        DateTimeOffset StartedAt);
}
