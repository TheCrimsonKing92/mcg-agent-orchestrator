using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;
using static ConductorConflictIntegrationRecordTests;

// Parallel-safe: independent repositories and kernels; worker, build and acceptance effects are injected.
public sealed class ConflictIntegrationScenarioTests
{
    private sealed class Scenario : IDisposable
    {
        internal Fixture Fixture { get; }
        internal ConductorDriver Driver { get; }
        internal List<string> Escalations { get; } = [];
        internal int DeveloperDispatches { get; private set; }
        internal int TesterDispatches { get; private set; }
        internal int AcceptanceRuns { get; private set; }
        internal string? ResolvedHead { get; private set; }

        internal Scenario(bool failBuild = false, bool outside = false, bool handRetry = false)
        {
            Fixture = new(failBuild: failBuild);
            PassVerification(Fixture.Kernel, Fixture.Goal, Fixture.Tester);
            Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(Fixture.Goal, new(WorkspaceExists: true)));
            var git = Fixture.GitFixture;
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Git(git.Worktree, "rev-parse", "HEAD").Trim()),
                dispatchAndStart: _ =>
                {
                    if (Fixture.Developer.Status == WorkTaskStatus.Assigned)
                    {
                        DeveloperDispatches++;
                        Assert.Equal(git.MainHead, Git(git.Worktree, "rev-parse", "MERGE_HEAD").Trim());
                        ResolvedHead = Fixture.Resolve(outside: outside);
                        PassVerification(Fixture.Kernel, Fixture.Goal, Fixture.Developer, hasCommittedChanges: true);
                    }
                    else
                    {
                        Assert.Equal(WorkTaskStatus.Assigned, Fixture.Tester.Status);
                        TesterDispatches++;
                    }
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, note, kind, cause) => Fixture.Kernel.RetryTaskAutomatically(goalId, taskId, note, cause, retryRoundKind: kind),
                rebaseOntoMain: goal => RatchetConflictDispatchIntegration.MergeMainBeforeLanding(Fixture.Kernel, git.Repository, goal,
                    ConductorDriver.CreateAdditiveConflictMergeOptions(Fixture.Kernel, goal, Path.Combine(git.Repository, "events.log")), "main"),
                runAcceptance: _ =>
                {
                    AcceptanceRuns++;
                    Assert.Equal(ResolvedHead, Git(git.Worktree, "rev-parse", "HEAD").Trim());
                    return true;
                },
                writeEscalation: (_, _, reason) => Escalations.Add(reason), executionDirectory: git.Repository);
            Driver.ConfigureDeveloperCompletionStructuralPreflightRetry(Fixture.Kernel, Fixture.Coordinator);
            Driver.OverrideDeveloperCompletionStructuralPreflightForTests(_ => new(false, "no structural violations"), TimeSpan.FromSeconds(30));
            if (handRetry) Fixture.Kernel.RetryTaskAutomatically(Fixture.Goal.Id, Fixture.Developer.Id, "manual retry", RetryCause.MainDriftConflict);
        }

        internal ConductorAdvanceResult Tick() => Driver.AdvanceOnce(Fixture.Goal, ConductorAutonomyPolicy.Conservative);

        internal void BeginAndDispatch()
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(Tick().Outcome);
            Assert.Empty(Escalations);
            Assert.Equal(0, AcceptanceRuns);
            Fixture.GitFixture.AssertRestored();
            Assert.Equal(WorkTaskStatus.Assigned, Fixture.Developer.Status);
            Assert.Equal(RetryCause.MainDriftConflict, Fixture.Developer.PendingRetryCause);
            Tick();
            Assert.Equal(1, DeveloperDispatches);
            Assert.Equal(0, TesterDispatches);
            Assert.Empty(Escalations);
        }

        public void Dispose() => Fixture.Dispose();
    }

    [Fact]
    public void LandedConflict_IsIntegratedResolvedAndReturnsToGate()
    {
        using var scenario = new Scenario();
        scenario.BeginAndDispatch();
        scenario.Tick();
        Assert.Equal(1, scenario.Fixture.BuildCalls);
        Assert.Equal(GoalOperationStatus.Completed, scenario.Fixture.Entry.Status);
        Assert.Equal(1, scenario.TesterDispatches);
        PassVerification(scenario.Fixture.Kernel, scenario.Fixture.Goal, scenario.Fixture.Tester);
        scenario.Tick();
        Assert.Equal(1, scenario.AcceptanceRuns);
        Assert.Equal(1, scenario.DeveloperDispatches);
        Assert.Empty(scenario.Escalations);
        var git = scenario.Fixture.GitFixture;
        Assert.Equal("1", Git(git.Worktree, "rev-list", "--count", git.OriginalHead + "..HEAD", "--first-parent").Trim());
        Assert.Equal(new[] { scenario.ResolvedHead, git.OriginalHead, git.MainHead },
            Git(git.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' '));
        Assert.False(File.Exists(ConductorConflictIntegrationValidateTests.MarkerPath(git.Worktree)));
        Assert.False(File.Exists(Path.Combine(git.Repository, ".orchestrator", "operator-intents.db")));
    }

    [Theory]
    [InlineData(false, "CS0103", 1)]
    [InlineData(true, "src/Other.cs", 0)]
    public void ResolutionFailure_EscalatesOnceAndRestoresCandidate(bool outside, string detail, int builds)
    {
        using var scenario = new Scenario(failBuild: !outside, outside: outside);
        scenario.BeginAndDispatch();
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Tick().Outcome);
        var reason = Assert.Single(scenario.Escalations);
        Assert.StartsWith("pre-landing rebase conflict (src/Widget.cs)", reason);
        Assert.DoesNotContain("workspace rebase", reason);
        Assert.Contains(detail, reason);
        Assert.Equal(builds, scenario.Fixture.BuildCalls);
        scenario.Fixture.GitFixture.AssertRestored();
        Assert.Equal(0, scenario.TesterDispatches);
        Assert.Equal(0, scenario.AcceptanceRuns);
    }

    [Fact]
    public void FailedRound_UnchangedCandidate_RefusesAnotherRetry()
    {
        using var scenario = new Scenario(failBuild: true);
        scenario.BeginAndDispatch();
        scenario.Tick();
        Assert.Single(scenario.Escalations);
        scenario.Escalations.Clear();
        PassVerification(scenario.Fixture.Kernel, scenario.Fixture.Goal, scenario.Fixture.Tester);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Tick().Outcome);
        Assert.Contains("conductor integration refused: repeat conflict on unchanged candidate", Assert.Single(scenario.Escalations));
        Assert.DoesNotContain("workspace rebase", scenario.Escalations[0]);
        Assert.Equal(1, scenario.Fixture.RetryCalls);
        Assert.Equal(0, scenario.TesterDispatches);
        scenario.Fixture.GitFixture.AssertRestored();
    }

    [Fact]
    public void HandRetriedDeveloper_NoRecord_KeepsExactGuardRefusal()
    {
        using var scenario = new Scenario(handRetry: true);
        var result = scenario.Tick();
        Assert.Equal(ConductorConflictIntegrationAdmissionTests.GuardText(scenario.Fixture.Goal),
            Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Reason);
        Assert.Equal(ConductorConflictIntegrationAdmissionTests.GuardText(scenario.Fixture.Goal), Assert.Single(scenario.Escalations));
        Assert.Equal(0, scenario.DeveloperDispatches);
        Assert.Equal(0, scenario.TesterDispatches);
        scenario.Fixture.GitFixture.AssertRestored();
    }
}
