using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGatePreflightMissingSkillTests : ChaosGateTestBase
{
    // Gate 7a: Missing local skill blocks preflight
    [Xunit.Fact(DisplayName = "ChaosGate7a_missing_local_skill_blocks_subscription_preflight")]
    public void Gate7a_MissingLocalSkill_BlocksSubscriptionPreflight()
    {
        var root = CreateSeededRepo();
        var worktree = CreateLinkedWorktree(root);

        Directory.CreateDirectory(Path.Combine(worktree, ".agents", "skills"));

        var (goal, task, agents) = CreatePreflightScenario(
            "Run dotnet test to verify the implementation.", AgentRole.Developer);

        var result = WorkerProfileDispatcher.PreflightSubscriptionTask(
            goal, task, agents, WorkerProfileCatalog.Default(), worktree, DispatchedAt);

        Assert.False(result.Allowed);
        var findings = string.Join("\n", result.Findings);
        Assert.Contains("missing required local skill", findings, StringComparison.Ordinal);
        Assert.Contains("dotnet-windows-build-hygiene", findings, StringComparison.Ordinal);
    }
}
