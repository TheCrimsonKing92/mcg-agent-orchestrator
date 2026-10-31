using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its repository, kernel and cleanup hooks.
public sealed class GoalWorktreeOrphanSweepGoalProtectionTests : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void NonTerminalGoal_KeepsUnregisteredTree_UntilTerminal(bool activate)
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Protected orphan", [new(TaskId.New(), "Work", AgentRole.Developer)]);
            if (activate) kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Assert.False(goal.IsTerminal);
            var fixture = new OrphanSweepTestFixture(repo);
            var path = fixture.AddDirectory(goal.Id.Value[..8]);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));

            var protectedPass = fixture.Sweep(kernel);

            Assert.Equal(0, protectedPass.RemovedCount);
            Assert.Empty(fixture.Deletions);
            Assert.True(Directory.Exists(path));
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "non-terminal-goal", goal.Id);

            kernel.CancelGoal(goal.Id, "Exercise terminal eligibility.");
            Assert.True(goal.IsTerminal);
            fixture.Outcomes.Clear();
            var terminalPass = fixture.Sweep(kernel);

            Assert.Equal(1, terminalPass.RemovedCount);
            Assert.Empty(terminalPass.LeftoverPaths);
            Assert.Equal(path, Assert.Single(fixture.Deletions));
            Assert.False(Directory.Exists(path));
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "removed", goal.Id);
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MissingGoalState_KeepsGoalShapedTree_AndReportsSkip(bool partialKernel)
    {
        var repo = CreateSeededRepository();
        try
        {
            var fixture = new OrphanSweepTestFixture(repo);
            var path = fixture.AddDirectory("abcdef12");
            var result = fixture.Sweep(partialKernel ? new AgentOrchestratorKernel() : null);

            Assert.Equal(0, result.RemovedCount);
            Assert.True(Directory.Exists(path));
            Assert.Empty(fixture.Deletions);
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "goal-state-unavailable");
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void AmbiguousPrefix_NonTerminalMatch_PreventsTerminalRemoval()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var terminal = kernel.CreateGoal(new GoalId("12345678aaaaaaaaaaaaaaaaaaaaaaaa"),
                "Terminal", [new(TaskId.New(), "Work", AgentRole.Developer)]);
            kernel.CancelGoal(terminal.Id, "Terminal collision.");
            var live = kernel.CreateGoal(new GoalId("12345678bbbbbbbbbbbbbbbbbbbbbbbb"),
                "Live", [new(TaskId.New(), "Work", AgentRole.Developer)]);
            var fixture = new OrphanSweepTestFixture(repo);
            var path = fixture.AddDirectory("12345678");

            var result = fixture.Sweep(kernel);

            Assert.Equal(0, result.RemovedCount);
            Assert.Empty(fixture.Deletions);
            Assert.True(Directory.Exists(path));
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "non-terminal-goal", live.Id);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void RegisteredWorktree_IsSkipped_WithOneAttributedOutcome()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Registered", [new(TaskId.New(), "Work", AgentRole.Developer)]);
            kernel.CancelGoal(goal.Id, "Registration is independently protective.");
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            OrphanSweepTestFixture.AgeTree(path, OrphanSweepTestFixture.ReferenceTime.AddHours(-1));
            var fixture = new OrphanSweepTestFixture(repo);

            var result = fixture.Sweep(kernel);

            Assert.Equal(0, result.RemovedCount);
            Assert.Empty(fixture.Deletions);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "registered", goal.Id);
        }
        finally { DeleteDirectory(repo); }
    }
}
