using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalRefinementPolicySelection(
    ConductorAutonomyPolicy Policy,
    string? FallbackDecision);

internal static partial class GoalRefinementWorkCoordinator
{
    internal const string PolicyFallbackDecisionPrefix = "spec_refinement_policy fallback=Conservative";

    internal static GoalRefinementPolicySelection LoadRefinementPolicy(OrchestratorWorkspace workspace)
    {
        var path = Path.GetFullPath(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"));
        try
        {
            var policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
                new DirectoryInfo(workspace.OrchestratorDirectory));
            if (ReferenceEquals(policy, ConductorAutonomyPolicy.Conservative))
            {
                return new GoalRefinementPolicySelection(
                    ConductorAutonomyPolicy.Conservative,
                    $"{PolicyFallbackDecisionPrefix} reason=missing path={path} detail=no conductor-policy.json found");
            }

            return new GoalRefinementPolicySelection(policy, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = string.Concat(ex.Message.Select(character => char.IsWhiteSpace(character) ? ' ' : character));
            return new GoalRefinementPolicySelection(
                ConductorAutonomyPolicy.Conservative,
                $"{PolicyFallbackDecisionPrefix} reason=load-failed path={path} detail={detail}");
        }
    }
}
