using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Recovers sign-in failures without revisiting an agent already failed over from.</summary>
internal sealed class ProviderSignInFailover(AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents)
{
    internal const string Marker = "PROVIDER_SIGNIN_FAILOVER";

    internal bool TryFailover(Goal goal, FailedGoalRecoveryDecision decision,
        Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec> retry)
    {
        if (decision.DiscriminatingEvidence != "failed-terminal-fallthrough")
            return false;
        var failed = goal.Tasks.Where(task => task.Status == WorkTaskStatus.Failed).ToArray();
        if (failed.Length != 1)
            return false;
        var task = failed[0];
        if (!DispatchFailureClassifier.HasRecoverableProviderAuthenticationFailure(task) ||
            OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id))
            return false;
        var assigned = agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId);
        if (assigned is null)
            return false;
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in goal.Timeline.Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRedelegated))
        {
            var markerIndex = evt.Message.IndexOf(Marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;
            foreach (var token in evt.Message[(markerIndex + Marker.Length)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (token.StartsWith("from=", StringComparison.Ordinal))
                    excluded.Add(token["from=".Length..]);
        }
        var replacement = agents.FirstOrDefault(agent => agent.Status == AgentStatus.Available &&
            agent.Role == task.RequiredRole && AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy) &&
            !string.Equals(agent.Model.ProviderName, assigned.Model.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            !excluded.Contains(agent.Id.Value));
        if (replacement is null)
            return false;
        var message = $"{Marker} from={assigned.Id.Value} from_provider={assigned.Model.ProviderName} to={replacement.Id.Value} to_provider={replacement.Model.ProviderName} role={task.RequiredRole}";
        kernel.ReassignTaskAgent(goal.Id, task.Id, replacement, message);
        retry(goal.Id, task.Id, message, null, RetryCause.ProviderInterruption);
        return true;
    }

    internal static ProviderSignInFailover? ForWorkspace(AgentOrchestratorKernel? kernel, OrchestratorWorkspace? workspace)
    {
        if (kernel is null || workspace is null) return null;
        return new ProviderSignInFailover(kernel, AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents);
    }
}
