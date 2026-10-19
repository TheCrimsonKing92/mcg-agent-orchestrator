using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>One bounded recovery of budget failures onto an eligible subscription binding.</summary>
internal sealed class ProviderHoldFailedGoalRedispatch(
    AgentOrchestratorKernel kernel,
    Func<AgentOrchestratorKernel, Goal, ProviderHoldAgentSubstitution> createResolver,
    bool retryOnHeldBinding = false)
{
    private const string RetryMessage = "Retry on an eligible agent while the original provider binding remains held.";
    internal bool RetriesOnHeldBinding => retryOnHeldBinding;

    internal bool TryRedispatch(Goal goal, FailedGoalRecoveryFacts facts,
        Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec> retry)
    {
        var failed = facts.Tasks.Where(task => task.Status == WorkTaskStatus.Failed).ToArray();
        if (failed.Length == 0 || failed.Any(task => task.ProviderFailureKind != ProviderFailureKind.BudgetExhausted))
            return false;
        var scratch = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        scratch.ProviderHoldHistory.CopyFrom(kernel.ProviderHoldHistory);
        var substitutions = new List<(TaskId TaskId, ProviderHoldAgentSubstitution.Result.Substitute Substitute)>();
        var heldRetries = new List<(TaskId TaskId, string Message)>();
        foreach (var task in failed)
        {
            // The normal preflight requires Assigned. Retry only the scratch view until every task qualifies.
            scratch.RetryTaskAutomatically(goal.Id, task.TaskId, RetryMessage, RetryCause.ProviderInterruption);
            var viewGoal = scratch.GetGoal(goal.Id);
            var resolution = createResolver(scratch, viewGoal).Resolve(viewGoal, scratch.GetTask(goal.Id, task.TaskId));
            if (resolution is not ProviderHoldAgentSubstitution.Result.Substitute substitute)
            {
                if (retryOnHeldBinding && HeldRetryMessage(resolution) is { } message)
                {
                    heldRetries.Add((task.TaskId, message));
                    continue;
                }
                return false;
            }
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
        foreach (var (taskId, message) in heldRetries)
            retry(goal.Id, taskId, message, null, RetryCause.ProviderInterruption);
        return true;
    }

    private static string? HeldRetryMessage(ProviderHoldAgentSubstitution.Result resolution)
    {
        var findings = resolution switch
        {
            ProviderHoldAgentSubstitution.Result.None none => none.Findings,
            ProviderHoldAgentSubstitution.Result.WaitOnPin pin => pin.Findings,
            _ => []
        };
        foreach (var finding in findings.Where(line => line.StartsWith(
                     "blocked: code=provider-budget-exhausted;", StringComparison.Ordinal)))
        {
            var match = Regex.Match(finding,
                @"for binding (?<key>.+?) \((?:provider-only|provider\+credential)\);.*; next_probe=(?<at>\S+); reset_source=(?<source>operator|parsed|backoff); attempt=(?<attempt>\d+)\z",
                RegexOptions.CultureInvariant);
            if (match.Success && ProviderBudgetHoldSchedules.TryParseInstant(match.Groups["at"].Value, out _) &&
                int.TryParse(match.Groups["attempt"].Value, out var attempt) && attempt > 0)
                return $"Retry on the held binding {match.Groups["key"].Value}; the conductor probes it at next_probe={match.Groups["at"].Value} (reset_source={match.Groups["source"].Value}; attempt={match.Groups["attempt"].Value}).";
        }
        return null;
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
                targetHome: new WorkerTargetHome(home.IsHome(workspace), WorkerAcceptanceManifestResolver.For(workspace)))),
            retryOnHeldBinding: true);
    }
}
