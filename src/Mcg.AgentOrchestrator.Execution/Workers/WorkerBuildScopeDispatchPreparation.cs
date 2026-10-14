using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Owns build-scope preparation and its binding to the dispatch attribution boundary.</summary>
internal static class WorkerBuildScopeDispatchPreparation
{
    internal static void Prepare(Goal goal, TaskSpec task, string workingDirectory, string integrationBranch)
    {
        if (task.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            TargetToolchainDetector.Detect(workingDirectory) == Toolchain.Dotnet &&
            Directory.Exists(workingDirectory) &&
            GitCli.RunForRepository(workingDirectory, "symbolic-ref", "--short", "HEAD") is { Succeeded: true } branch &&
            branch.Output.Trim().StartsWith("goal/", StringComparison.OrdinalIgnoreCase))
            WorkerBuildProjectScope.Prepare(workingDirectory, goal.Id.Value, task.Id.Value, integrationBranch,
                WorkerBuildProjectScope.PinnedGoalBaseline(goal));
    }

    internal static void RecordDispatchBaseCommit(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskSpec task, string workingDirectory, string? worktreeHeadSha)
    {
        // Capture the scope at the same pre-spawn boundary as file attribution.
        WorkerBuildScopeIdentity? buildScopeIdentity = null;
        if (task.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            File.Exists(WorkerBuildProjectScope.MetadataPath(workingDirectory)))
        {
            var scope = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(
                File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(workingDirectory)))
                ?? throw new IOException("Worker build scope metadata missing at dispatch start.");
            WorkerBuildProjectScope.Prepare(workingDirectory, goalId.Value, task.Id.Value, scope.IntegrationBranch,
                WorkerBuildProjectScope.PinnedGoalBaseline(kernel.GetGoal(goalId)));
            buildScopeIdentity = WorkerBuildProjectScope.ReadDispatchIdentity(workingDirectory, worktreeHeadSha);
        }
        if (worktreeHeadSha is not null)
            kernel.RecordDispatchBaseCommit(goalId, task.Id, worktreeHeadSha, buildScopeIdentity);
    }
}
