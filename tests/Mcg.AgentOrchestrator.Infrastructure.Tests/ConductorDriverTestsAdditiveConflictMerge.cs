using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;

// Parallel-safe: owned git repositories, goal kernels and event logs; other effects are injected.
public sealed class ConductorDriverTestsAdditiveConflictMerge
{
    [Fact]
    public void PreLandingAdditiveConflict_GatesMergedTreeWithoutEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var logPath = Path.Combine(fixture.Repository, ".orchestrator", "logs", "conduct-events.log");
        var effects = new List<string>();
        var driver = MakeDriver(
            rebaseOntoMain: current => GoalWorktrees.TryRebaseOntoMain(fixture.Repository, current.Id,
                ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, current, logPath)),
            runAcceptance: _ =>
            {
                Assert.Equal(MergedText, fixture.Read());
                Assert.Equal(3, Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);
                Assert.Equal("", Git(fixture.Worktree, "status", "--porcelain=v1"));
                effects.Add("acceptance");
                return true;
            },
            writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason));

        Assert.IsType<ConductorAdvanceOutcome.Done>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal(new[] { "acceptance" }, effects);
        AssertEvent(logPath, "merged", "outcome");
    }

    [Fact]
    public void PreLandingNonAdditiveConflict_PreservesExactEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, "base\n", "main\n", "goal\n")], goal.Id);
        var logPath = Path.Combine(fixture.Repository, ".orchestrator", "logs", "conduct-events.log");
        const string expected = "pre-landing rebase conflict (src/Widget.cs); use 'workspace rebase' to resolve";
        var escalations = new List<string>();
        var driver = MakeDriver(
            rebaseOntoMain: current => GoalWorktrees.TryRebaseOntoMain(fixture.Repository, current.Id,
                ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, current, logPath)),
            runAcceptance: _ => throw new InvalidOperationException("A refused merge must not start acceptance."),
            writeEscalation: (_, _, reason) => escalations.Add(reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(expected, Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Reason);
        Assert.Equal(expected, Assert.Single(escalations));
        fixture.AssertRestored();
        AssertEvent(logPath, "refused", "decision");
    }

    [Fact]
    public void GoalFrozenRuling_PassesItsPathToMergeRefusal()
    {
        var (kernel, goal) = SimpleGoal();
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, goal.Tasks.Single().Id,
            "Frozen file ruling", Mcg.AgentOrchestrator.Core.HumanWaitKind.SpecClarification).Request;
        var ruling = new Mcg.AgentOrchestrator.Core.FrozenFactRuling(["Widget"],
            [new("Widget.Fact", WidgetPath, "preserve")], "basis", "other facts", "diff check", ["evidence"]);
        kernel.SubmitHumanInput(request.Id, ruling.Render());
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var logPath = Path.Combine(fixture.Repository, ".orchestrator", "logs", "conduct-events.log");
        var options = ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, goal, logPath);

        Assert.Contains(WidgetPath, options.FrozenPaths);
        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, goal.Id, options);

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        fixture.AssertRestored();
        Assert.Contains("reason=frozen-path", File.ReadAllText(logPath));
        AssertEvent(logPath, "refused", "decision");
    }

    private static void AssertEvent(string path, string result, string operatorClass)
    {
        var line = Assert.Single(File.ReadAllLines(path));
        using var record = JsonDocument.Parse(line);
        Assert.Equal("rebase-automerge", record.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal(operatorClass, record.RootElement.GetProperty("operator").GetString());
        Assert.Contains($"result={result}", record.RootElement.GetProperty("detail").GetString());
    }
}
