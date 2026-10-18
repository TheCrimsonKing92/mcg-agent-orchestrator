using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>One bounded recovery of budget failures onto an eligible subscription binding.</summary>
internal sealed class ProviderHoldFailedGoalRedispatch(
    AgentOrchestratorKernel kernel,
    Func<AgentOrchestratorKernel, Goal, ProviderHoldAgentSubstitution> createResolver)
{
    private const string RetryMessage = "Retry on an eligible agent while the original provider binding remains held.";

    internal bool TryRedispatch(Goal goal, FailedGoalRecoveryFacts facts,
        Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec> retry)
    {
        var failed = facts.Tasks.Where(task => task.Status == WorkTaskStatus.Failed).ToArray();
        if (failed.Length == 0 || failed.Any(task => task.ProviderFailureKind != ProviderFailureKind.BudgetExhausted))
            return false;
        var scratch = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var substitutions = new List<(TaskId TaskId, ProviderHoldAgentSubstitution.Result.Substitute Substitute)>();
        foreach (var task in failed)
        {
            // The normal preflight requires Assigned. Retry only the scratch view until every task qualifies.
            scratch.RetryTaskAutomatically(goal.Id, task.TaskId, RetryMessage, RetryCause.ProviderInterruption);
            var viewGoal = scratch.GetGoal(goal.Id);
            if (createResolver(scratch, viewGoal).Resolve(viewGoal, scratch.GetTask(goal.Id, task.TaskId))
                is not ProviderHoldAgentSubstitution.Result.Substitute substitute)
                return false;
            scratch.SubstituteProviderHeldTaskAgent(goal.Id, task.TaskId, substitute.Agent,
                substitute.HeldBindingKey, substitute.EvidenceReceipt);
            substitutions.Add((task.TaskId, substitute));
        }
        foreach (var (taskId, substitute) in substitutions)
        {
            kernel.SubstituteProviderHeldTaskAgent(goal.Id, taskId, substitute.Agent,
                substitute.HeldBindingKey, substitute.EvidenceReceipt);
            retry(goal.Id, taskId, RetryMessage, null, RetryCause.ProviderInterruption);
        }
        return true;
    }

    internal static ProviderHoldFailedGoalRedispatch? ForWorkspace(AgentOrchestratorKernel? kernel,
        OrchestratorWorkspace? workspace, ConductorAutonomyPolicy policy)
    {
        if (kernel is null || workspace is null) return null;
        var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
        var home = OrchestratorHome.ResolveForLaunch(workspace.ExecutionDirectory);
        var sandbox = WorkerSandboxOptions.FromEnvironment();
        return new(kernel, (view, goal) => new ProviderHoldAgentSubstitution(view, agents,
            (viewGoal, task, holdScope) => WorkerProfileDispatcher.PreflightSubscriptionTask(
                viewGoal, task, agents, profiles, workspace.ResolveExecutionDirectory(goal.Id), DateTimeOffset.UtcNow,
                allowGitReference: sandbox.Enabled, sandboxOptions: sandbox, providerHoldScope: holdScope,
                cascadeTesterCheapFirst: policy.CascadeTesterCheapFirst, cascadeCheapModelAlias: policy.CascadeCheapModelAlias,
                cascadeMechanicalReworkCheap: policy.CascadeMechanicalReworkCheap,
                orchestratorSkillDirectory: Path.Combine(home.InstallRootDirectory, ".agents", "skills"),
                integrationBranch: workspace.IntegrationBranch,
                targetHome: new WorkerTargetHome(home.IsHome(workspace), WorkerAcceptanceManifestResolver.For(workspace)))));
    }
}
