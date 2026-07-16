using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGatePreflightDirtyWorktreeTests : ChaosGateTestBase
{
    // Gate 7b: Dirty worktree before dispatch blocks preflight
    [Xunit.Fact(DisplayName = "ChaosGate7b_dirty_worktree_before_dispatch_blocks_subscription_preflight")]
    public void Gate7b_DirtyWorktreeBeforeDispatch_BlocksSubscriptionPreflight()
    {
        var root = CreateSeededRepo();
        var worktree = GoalWorktrees.Ensure(root, GoalId.New());
        WriteSkill(worktree, "dotnet-windows-build-hygiene");
        File.WriteAllText(Path.Combine(worktree, "Dirty.cs"), "// uncommitted");

        var (goal, task, agents) = CreatePreflightScenario(
            "Run dotnet test to verify the implementation.", AgentRole.Developer);

        var result = WorkerProfileDispatcher.PreflightSubscriptionTask(
            goal, task, agents, WorkerProfileCatalog.Default(), worktree, DispatchedAt);

        Assert.False(result.Allowed);
        var findings = string.Join("\n", result.Findings);
        Assert.Contains("uncommitted change", findings, StringComparison.Ordinal);
    }
}
