using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    private static (ProspectiveEvidenceGateResolution Resolution, string? HoldDescription) EvaluatePendingHumanInput(
        AgentOrchestratorKernel kernel, Goal goal, string orchestratorDirectory, string? candidateRevision)
    {
        // Only the existing goal/candidate-bound attempt resolver may submit automatic answers.
        // A grouped pass alone does not establish named evidence for an individual member.
        var resolution = ProspectiveEvidenceGateResolver.Resolve(kernel, goal, orchestratorDirectory, candidateRevision);
        var pending = kernel.GetPendingHumanInput(goal.Id);
        if (pending.Count == 0) return (resolution, null);

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
        return (resolution, $"goal {goal.Id.Value} has pending human input: {string.Join("; ", requests)}");
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
