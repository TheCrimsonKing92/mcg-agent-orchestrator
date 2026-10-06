using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: owned temporary files, injected launcher probe and sandbox, no worker process or environment mutation.
public sealed class WorkerSubscriptionModelResolverNoSparkLaneTests : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions Sandbox = new(
        false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void SmallDeveloperTask_PreparesConfiguredFullProfile()
    {
        PrepareAndAssertFullProfile(mechanicalRetry: false);
    }

    [Fact]
    public void ComplexMechanicalDeveloperRetry_PreparesConfiguredFullProfile()
    {
        PrepareAndAssertFullProfile(mechanicalRetry: true);
    }

    private static void PrepareAndAssertFullProfile(bool mechanicalRetry)
    {
        var root = CreateTempDirectory();
        try
        {
            var workingDirectory = Path.Combine(root, "repo");
            Directory.CreateDirectory(workingDirectory);
            File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), mechanicalRetry
                ? "Build an end-to-end distributed integration with horizontal scaling."
                : "Update one label.", AgentRole.Developer);
            var goal = kernel.CreateGoal(mechanicalRetry
                ? "Implement high-risk multi-scope persistence migration"
                : "Fix a typo", [task]);
            var agents = AgentCatalog.Default().Agents;
            var agent = AgentCatalog.Default().GetRequired(AgentRole.Developer);
            Assert.False(agent.IsProviderRoutingConstrained);
            if (!mechanicalRetry)
                kernel.RecordGoalPolicyDecision(goal.Id,
                    "Intake pipeline decision (auto): developer-only; risk labels: small-task, low-risk.");
            kernel.ActivateGoal(goal.Id, agents);
            if (mechanicalRetry)
                kernel.RetryTask(goal.Id, task.Id, "Mechanical retry: rerun named commands and quote receipts; commit nothing.",
                    retryRoundKind: RetryRoundKind.Mechanical);

            WorkerProfileDispatcher.PrepareSubscriptionTask(
                kernel, goal, task, agents, WorkerProfileCatalog.Default(), Path.Combine(root, "prompts"),
                workingDirectory, DateTimeOffset.Parse("2026-07-17T12:00:00Z"),
                sandboxOptions: Sandbox, commandExists: _ => true);

            var dispatch = Assert.IsType<TaskDispatchRecord>(task.LastDispatch);
            Assert.Equal(mechanicalRetry ? TaskComplexity.Complex : TaskComplexity.Simple, dispatch.TaskComplexity);
            Assert.Equal("codex-cli", dispatch.WorkerName);
            Assert.Equal(agent.Subscription!.ModelAlias, dispatch.ModelName);
            Assert.Contains("full-profile: role is write-capable or gate-heavy", dispatch.ModelSelectionReason, StringComparison.Ordinal);
            Assert.Contains($"--model '{agent.Subscription.ModelAlias}'", dispatch.Command, StringComparison.Ordinal);
            Assert.DoesNotContain("gpt-5.3-codex-spark", dispatch.Command, StringComparison.Ordinal);
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryWithRetry(root);
        }
    }
}
