using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    private static (ProspectiveEvidenceGateResolution Resolution, string? HoldDescription, IReadOnlyList<string> ObligationLines) EvaluatePendingHumanInput(
        AgentOrchestratorKernel kernel, Goal goal, string orchestratorDirectory, string? candidateRevision)
    {
        // Only the existing goal/candidate-bound attempt resolver may submit automatic answers.
        // A grouped pass alone does not establish named evidence for an individual member.
        var resolution = ProspectiveEvidenceGateResolver.Resolve(kernel, goal, orchestratorDirectory, candidateRevision);
        var obligationLines = GateTestObligationEvaluator.Evaluate(goal, orchestratorDirectory, candidateRevision)
            .Where(result => result.Outcome != GateTestObligationEvaluator.Outcome.Satisfied)
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
        var pending = kernel.GetPendingHumanInput(goal.Id);
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
        AgentOrchestratorKernel kernel, IReadOnlyList<Goal> goals, string orchestratorDirectory,
        Func<GoalId, string> candidateRevision)
    {
        var holds = goals.Select(goal => EvaluatePendingHumanInput(
                kernel, goal, orchestratorDirectory, candidateRevision(goal.Id)).HoldDescription)
            .Where(description => description is not null).ToArray();
        return holds.Length == 0 ? null : string.Join("; ", holds);
    }
}
