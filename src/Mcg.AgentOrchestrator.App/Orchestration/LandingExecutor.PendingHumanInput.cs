using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    private static (ProspectiveEvidenceGateResolution Resolution, string? HoldDescription, IReadOnlyList<string> ObligationLines) EvaluatePendingHumanInput(
        AgentOrchestratorKernel kernel, Goal goal, OrchestratorWorkspace workspace, string? candidateRevision)
    {
        var orchestratorDirectory = workspace.OrchestratorDirectory;
        // Resolve candidate-bound gate and role reports before projecting pending input.
        var resolution = ProspectiveEvidenceGateResolver.Resolve(kernel, goal, orchestratorDirectory, candidateRevision);
        if (!string.IsNullOrWhiteSpace(candidateRevision))
            kernel.CloseRoleReportEvidence(goal.Id, candidateRevision,
                RoleReportGateClassEvidence.For(orchestratorDirectory, goal.Id, candidateRevision));
        var obligations = GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidateRevision,
            new(workspace.ExecutionDirectory, workspace.IntegrationBranch), kernel.HumanInputRequests.ToArray());
        foreach (var result in obligations.Where(result => result.Outcome == GateTestObligationEvaluator.Outcome.Satisfied &&
            result.RenameProvenance.Length > 0))
            Console.WriteLine(result.RenameProvenance);
        foreach (var result in obligations.Where(result => result.Outcome == GateTestObligationEvaluator.Outcome.Satisfied &&
            result.AnswerRebindProvenance.Length > 0))
            Console.WriteLine(result.AnswerRebindProvenance);
        var obligationLines = obligations
            .Where(result => result.Outcome is not (GateTestObligationEvaluator.Outcome.Satisfied or GateTestObligationEvaluator.Outcome.Waived))
            .Select(result =>
            {
                var outcome = result.Outcome switch
                {
                    GateTestObligationEvaluator.Outcome.Pending => "pending",
                    GateTestObligationEvaluator.Outcome.SelectorNotFound => "selector-not-found",
                    GateTestObligationEvaluator.Outcome.Failed => "failed",
                    _ => throw new InvalidOperationException($"Unexpected obligation outcome: {result.Outcome}")
                };
                var selectors = result.Selectors.Count == 0 ? "none" : string.Join(", ", result.Selectors);
                return $"gate test obligation {result.Obligation.Id[..Math.Min(8, result.Obligation.Id.Length)]} " +
                    $"(brief v{result.Obligation.BriefVersion}) {outcome}: {selectors}; source {result.SourceId ?? "none"}";
            }).ToArray();
        var pending = PostLandingOperatorCheck.HoldingLanding(kernel.GetPendingHumanInput(goal.Id));
        if (pending.Count == 0)
            return (resolution, obligationLines.Length == 0 ? null : string.Join("; ", obligationLines), obligationLines);

        var requests = pending.OrderBy(wait => wait.Id.Value, StringComparer.Ordinal).Select(wait =>
        {
            var unresolved = resolution.UnresolvedWaits.SingleOrDefault(item => item.RequestId == wait.Id);
            var detail = unresolved?.SubmissionFailure is { } failure
                ? $"; submission failed: {failure}"
                : unresolved is not null
                    ? $"; missing: {string.Join(", ", unresolved.MissingOrFailedTestNames)}"
                    : "; answer required through SubmitHumanInput";
            return $"request {wait.Id.Value} ({wait.Kind}, owner {wait.EvidenceOwner ?? "unspecified"}){detail}";
        });
        var hold = $"goal {goal.Id.Value} has pending human input: {string.Join("; ", requests)}";
        if (obligationLines.Length > 0) hold += "; " + string.Join("; ", obligationLines);
        return (resolution, hold, obligationLines);
    }

    private static string? DescribeGroupedPendingHumanInput(
        AgentOrchestratorKernel kernel, IReadOnlyList<Goal> goals, OrchestratorWorkspace workspace,
        Func<GoalId, string> candidateRevision)
    {
        var holds = goals.Select(goal => EvaluatePendingHumanInput(
                kernel, goal, workspace, candidateRevision(goal.Id)).HoldDescription)
            .Where(description => description is not null).ToArray();
        return holds.Length == 0 ? null : string.Join("; ", holds);
    }
}
