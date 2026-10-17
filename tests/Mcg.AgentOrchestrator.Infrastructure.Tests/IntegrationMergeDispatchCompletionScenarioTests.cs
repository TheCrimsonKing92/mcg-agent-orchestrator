using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorConflictIntegrationRecordTests;
using static ConductorDriverTests;
using static IntegrationMergeChangedPathsTests;

// Parallel-safe: private repositories/kernels, scripted worker exit artifacts and injected builds.
public sealed class IntegrationMergeDispatchCompletionScenarioTests
{
    private sealed class Scenario : IDisposable
    {
        internal Fixture Fixture { get; } = new();
        internal ConductorDriver Driver { get; }
        internal List<string> Escalations { get; } = [];
        internal int DeveloperDispatches { get; private set; }
        internal int TesterDispatches { get; private set; }
        internal DateTimeOffset DispatchedAt { get; private set; }

        internal Scenario(bool marker = false, bool outside = false)
        {
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
                        Assert.Equal(RetryCause.MainDriftConflict, Fixture.Developer.PendingRetryCause);
                        Assert.Equal(git.MainHead, Git(git.Worktree, "rev-parse", "MERGE_HEAD").Trim());
                        Assert.True(ConductorConflictIntegrationMarker.Admits(Fixture.Developer, git.Worktree));
                        DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
                        RecordDispatch(Fixture.Kernel, Fixture.Goal, Fixture.Developer, git, DispatchedAt);
                        var head = ResolveAndCommit(git, DispatchedAt, marker, outside);
                        AssertOnlyMergeAfter(git.Worktree, DispatchedAt);
                        RecordExitedWorker(Fixture.Kernel, Fixture.Goal, Fixture.Developer, git, DispatchedAt, head);
                    }
                    else
                    {
                        Assert.Equal(WorkTaskStatus.Assigned, Fixture.Tester.Status);
                        TesterDispatches++;
                    }
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, note, kind, cause) =>
                    Fixture.Kernel.RetryTaskAutomatically(goalId, taskId, note, cause, retryRoundKind: kind),
                rebaseOntoMain: goal => RatchetConflictDispatchIntegration.MergeMainBeforeLanding(Fixture.Kernel, git.Repository, goal,
                    ConductorDriver.CreateAdditiveConflictMergeOptions(Fixture.Kernel, goal, Path.Combine(git.Repository, "events.log")), "main"),
                runAcceptance: _ => throw new InvalidOperationException("Tester must run before acceptance."),
                writeEscalation: (_, _, reason) => Escalations.Add(reason), executionDirectory: git.Repository);
            Driver.ConfigureDeveloperCompletionStructuralPreflightRetry(Fixture.Kernel, Fixture.Coordinator);
            Driver.OverrideDeveloperCompletionStructuralPreflightForTests(_ => new(false, "no structural violations"), TimeSpan.FromSeconds(30));
        }

        internal ConductorAdvanceResult Tick() => Driver.AdvanceOnce(Fixture.Goal, ConductorAutonomyPolicy.Conservative);

        internal void DispatchAndComplete()
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(Tick().Outcome);
            Fixture.GitFixture.AssertRestored();
            Assert.Equal(WorkTaskStatus.Assigned, Fixture.Developer.Status);
            Assert.Equal(RetryCause.MainDriftConflict, Fixture.Developer.PendingRetryCause);
            Tick();
            Assert.Equal(1, DeveloperDispatches);
            Assert.Equal(0, TesterDispatches);
            Assert.Empty(Escalations);
            Assert.True(File.Exists(ConductorConflictIntegrationValidateTests.MarkerPath(Fixture.GitFixture.Worktree)));
            CompleteWorker(Fixture.Kernel, Fixture.Goal, Fixture.Developer);
            AssertAcceptedDispatch(Fixture.Developer);
        }

        public void Dispose() => Fixture.Dispose();
    }

    [Fact(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    public void Completion_RecordedIntegrationMerge_AdvancesToTester()
    {
        using var scenario = new Scenario();
        scenario.DispatchAndComplete();
        var fixture = scenario.Fixture;
        var git = fixture.GitFixture;
        var evidence = new DispatchWorktreeCommitter().InspectGoalWorktree(git.Worktree, fixture.Goal.Id, scenario.DispatchedAt);
        Assert.True(evidence.IsAvailable, evidence.GitReceipt);
        Assert.Equal(new[] { WidgetPath }, evidence.Evidence.ChangedPaths);
        Assert.Equal(1, evidence.Evidence.CommitsAfterDispatch);
        Assert.True(evidence.Evidence.HasRelevantCommitAfterDispatch);

        scenario.Tick();

        Assert.Equal(GoalOperationStatus.Completed, fixture.Entry.Status);
        Assert.False(File.Exists(ConductorConflictIntegrationValidateTests.MarkerPath(git.Worktree)));
        Assert.Equal(1, fixture.BuildCalls);
        Assert.Equal(1, scenario.TesterDispatches);
        Assert.NotEqual(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal, new(WorkspaceExists: true)));
        Assert.Empty(scenario.Escalations);
    }

    [Fact]
    public void Completion_OrdinaryContentlessMerge_StillRejectsMissingChangeEvidence()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Ordinary Developer merge", [new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var developer = Assert.Single(goal.Tasks);
        using var git = new AdditiveConflictGitFixture([new(WidgetPath, "base\n", "same\n", "same\n")], goal.Id);
        Assert.NotEqual(RetryCause.MainDriftConflict, developer.PendingRetryCause);
        Assert.False(File.Exists(ConductorConflictIntegrationValidateTests.MarkerPath(git.Worktree)));
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        RecordDispatch(kernel, goal, developer, git, dispatchedAt);
        Git(git.Worktree, "merge", "--no-ff", "--no-commit", git.MainHead);
        DatedGit(git.Worktree, dispatchedAt.AddMinutes(1), "commit", "-m", "Contentless merge");
        Assert.Equal("", Git(git.Worktree, "diff", "--name-only", git.OriginalHead, "HEAD"));
        AssertOnlyMergeAfter(git.Worktree, dispatchedAt);
        RecordExitedWorker(kernel, goal, developer, git, dispatchedAt, Git(git.Worktree, "rev-parse", "HEAD").Trim());

        CompleteWorker(kernel, goal, developer);

        Assert.Equal(WorkTaskStatus.Failed, developer.Status);
        var verification = Assert.IsType<TaskVerificationRecord>(developer.LastVerification);
        Assert.Equal(1, verification.ExitCode);
        Assert.Contains("changed_paths=none", verification.StandardError);
        Assert.Contains("post_dispatch_commits=1", verification.StandardError);
        Assert.Contains("rule=required-file-change-evidence-missing",
            DispatchFailureClassifier.Classify(developer, verification).ClassifierReceipt);
    }

    [Theory(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    [InlineData(true, false, "conflict marker check: src/Widget.cs")]
    [InlineData(false, true, "scope check: changed outside recorded paths: src/Other.cs")]
    public void Completion_UnsafeResolution_PassesEvidenceButRestoresAndEscalates(
        bool marker, bool outside, string reason)
    {
        using var scenario = new Scenario(marker, outside);
        scenario.DispatchAndComplete();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Tick().Outcome);

        Assert.Contains(reason, Assert.Single(scenario.Escalations));
        Assert.Equal(GoalOperationStatus.Failed, scenario.Fixture.Entry.Status);
        scenario.Fixture.GitFixture.AssertRestored();
        Assert.Equal(0, scenario.TesterDispatches);
        Assert.Equal(0, scenario.Fixture.BuildCalls);
        Assert.False(File.Exists(ConductorConflictIntegrationValidateTests.MarkerPath(scenario.Fixture.GitFixture.Worktree)));
    }

    private static void AssertAcceptedDispatch(TaskSpec developer)
    {
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        var verification = Assert.IsType<TaskVerificationRecord>(developer.LastVerification);
        Assert.Equal(0, verification.ExitCode);
        Assert.True(verification.HasCommittedChanges);
        var outcome = DispatchFailureClassifier.Classify(developer, verification);
        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.DoesNotContain("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt);
        Assert.DoesNotContain("changed_paths=none", verification.StandardError);
    }

    private static void RecordDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        AdditiveConflictGitFixture git, DateTimeOffset dispatchedAt) =>
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "scripted developer", git.Worktree, dispatchedAt, BaseCommit: git.OriginalHead));

    private static void RecordExitedWorker(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        AdditiveConflictGitFixture git, DateTimeOffset dispatchedAt, string head)
    {
        var logs = Path.Combine(git.Repository, ".orchestrator", "logs", "integration-worker");
        Directory.CreateDirectory(logs);
        var stdout = Path.Combine(logs, "worker.out.log");
        var stderr = Path.Combine(logs, "worker.err.log");
        var exit = Path.Combine(logs, "worker.exit.txt");
        File.WriteAllText(stdout, $"""
            WORKER_RESULT:
            files: {WidgetPath}
            commands: git commit
            tests: deferred - IntegrationMergeDispatchCompletionScenarioTests
            commit: {head}
            blockers: none
            assigned_scope_complete: true
            model_fit: OpenAI/gpt-6.1-sol - adequate - scripted integration worker
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        File.WriteAllText(stderr, "");
        File.WriteAllText(exit, "0");
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            999999, "scripted developer", git.Worktree, stdout, stderr, exit, dispatchedAt, null, null));
    }

    private static void CompleteWorker(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        new BackgroundDispatchRunner(isStillRunning: _ => false,
            runOrchestratorBuildCheck: _ => new(true, 0, "PASS build: 0 errors (scripted build seam)"))
            .RefreshLatestProcess(kernel, goal.Id, task.Id);
}
