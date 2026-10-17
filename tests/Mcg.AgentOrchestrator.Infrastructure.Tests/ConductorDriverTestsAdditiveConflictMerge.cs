using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
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

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, executed.FromState);
        Assert.Equal("Landed: Landed", executed.Description);

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
        const string expected = "pre-landing rebase conflict (src/Widget.cs); not confined to ratchet ceiling rows, so the conductor cannot resolve it; the operator integrates main into the goal branch";
        var escalations = new List<string>();
        var driver = MakeDriver(
            rebaseOntoMain: current => GoalWorktrees.TryRebaseOntoMain(fixture.Repository, current.Id,
                ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, current, logPath)),
            runAcceptance: _ => throw new InvalidOperationException("A refused merge must not start acceptance."),
            writeEscalation: (_, _, reason) => escalations.Add(reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(expected, Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Reason);
        Assert.Equal(expected, Assert.Single(escalations));
        Assert.StartsWith("pre-landing rebase conflict (src/Widget.cs)", escalations[0]);
        Assert.DoesNotContain("workspace rebase", escalations[0]);
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

    [Fact]
    public void PreLandingRatchetConflict_RebindsObligationsBeforeAcceptance()
    {
        var (kernel, goal) = SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(RatchetFiles(), goal.Id);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["class row", "file row"], VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < 2; index++)
            kernel.MapCriterionEvidenceOwner(goal.Id, index, 1, CriterionEvidenceOwner.Acceptance, "seed",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: fixture.OriginalHead);
        PassVerification(kernel, goal, goal.Tasks.Single());
        var logPath = Path.Combine(fixture.Repository, ".orchestrator/logs/conduct-events.log");
        var calls = 0;
        var driver = MakeDriver(
            rebaseOntoMain: current => RatchetConflictDispatchIntegration.MergeMainBeforeLanding(kernel,
                fixture.Repository, current, ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, current, logPath),
                TrunkBranchName.Default),
            runAcceptanceSummary: _ =>
            {
                calls++;
                var head = Git(fixture.Worktree, "rev-parse", "HEAD").Trim();
                Assert.Equal(new[] { head, fixture.OriginalHead, fixture.MainHead },
                    Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' '));
                var measured = SourceSizeRatchet.MeasureClasses(fixture.Worktree, ["Widget"], out var unreadable)["Widget"];
                Assert.Empty(unreadable);
                Assert.Equal(RegistryText(FileRow(WidgetPath, 1, "goal A\n        // goal B"),
                    ClassRow(measured.TotalLines, measured.PartialFiles, "goal A\n        // goal B")),
                    fixture.Read(SourceSizeRatchet.SourcePath));
                Assert.All(goal.CriterionEvidenceObligations, item => Assert.Equal(head, item.ExpectedCandidateSha));
                Assert.Empty(Git(fixture.Worktree, "status", "--porcelain=v1"));
                foreach (var item in goal.CriterionEvidenceObligations)
                    kernel.RecordCriterionEvidence(goal.Id, item.Id, item.Owner, head, "gate-" + item.Id,
                        item.RequiredScope, true, "Merged head passed");
                return new AcceptanceVerificationSummary(true, [], BranchHeadSha: head);
            },
            writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, calls);
        Assert.Equal("Landed: Landed", Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Description);
        AssertEvent(logPath, "merged", "outcome");
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
