using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperAnsweredClarificationNoChangeDispatchTests : WorkerDispatchTestSupport
{
    private const string ConvergenceRetry =
        "auto-review-retry round 1 convergence brief: Tester task ad2ef076 WORKER_RESULT blocker; retry upstream Developer task.";

    // Verbatim WORKER_RESULT from the measured 76f933e4 round.
    private const string RecordedWorkerResult = """
        WORKER_RESULT:
        files: none
        commands: git diff main --check; git status --short; git diff main -- scoped files; .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-WorkerBuildCheck.ps1 tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
        tests: deferred - WorkerDispatchTestsSeededRepositoryFixtureGitCommitLaunchCount, WorkerDispatchTestsSeededRepositoryFactoryLaunchCount, WorkerDispatchTestsSeededRepositoryFactoryTests, WorkerDispatchTestsLineEndingFixtureDeterminism, WorkerDispatchTestsDispatchPreparation, WorkerDispatchTestsWorkerResultClassification, WorkerDispatchTestsSandboxLowIntegrity, WorkerDispatchTestsSubscriptionPreflight, WorkerDispatchTestsModelSelection, GoalWorktreeTestsGitCommitLaunchCount, RedirectedStreamDrainSourceGuardTests
        commit: none
        blockers: none
        assigned_scope_complete: true
        model_fit: OpenAI/gpt-6.1-sol - adequate - scoped candidate verification - confirmed existing implementation and authorized diff with fresh build evidence
        skills: dotnet-windows-build-hygiene, orchestrator-worker-verification, verification-before-completion
        confidence: high
        END_WORKER_RESULT
        """;

    // The recorded stdout had verification text outside the block (verification_recognized=true).
    // Negative rounds need that recognition to produce reason=no-change-evidence.
    private static string RecognizedOutput =>
        "Invoke-WorkerBuildCheck.ps1 build check finished with exit code 0.\n" + RecordedWorkerResult;

    [Xunit.Fact]
    public void AnsweredConvergenceRetryCompletesUnchangedCandidateAndReleasesTester()
    {
        var scenario = CreateScenario();
        scenario.Retry();
        scenario.Answer();
        var tester = scenario.Kernel.AddTask(scenario.Goal.Id, AgentRole.Tester,
            "Verify the unchanged candidate.", AgentCatalog.Default().Agents);
        scenario.StartRound(RecordedWorkerResult);
        Xunit.Assert.False(TesterReady(scenario.Goal, tester));

        scenario.Refresh();

        var task = scenario.Task;
        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed,
            $"Actual status: {task.Status}; verification: {task.LastVerification?.StandardError}");
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal("deferred-no-change-round", task.LastVerification.CompletionVerdictRule);
        Xunit.Assert.Equal(scenario.Candidate, task.LastDispatch!.BaseCommit);
        Xunit.Assert.Equal(task.LastDispatch.BaseCommit, task.LastDispatch.ResultCommit);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_DECLINED", task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.True(DeferredNoChangeOutcome.TryParse(task.LastVerification.StandardError, out var outcome));
        Xunit.Assert.Equal(scenario.Candidate, outcome.CandidateSha);
        Xunit.Assert.True(WorkerResultParser.TryParseResult(RecordedWorkerResult, out var result, out _));
        Xunit.Assert.Equal(DeveloperDeferredTestClassNames.Parse(result.Fields["tests"]), outcome.TestClasses);
        Xunit.Assert.True(TesterReady(scenario.Goal, tester));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void OpenUndeclaredFindingRejectsRoundWithOrWithoutAnswer(bool answer)
    {
        var scenario = CreateScenario();
        scenario.Retry();
        if (answer) scenario.Answer();
        DeveloperDeferredNoChangeDeclineCodeTests.RecordFinding(
            scenario.Kernel, scenario.Goal, "UndeclaredGammaTests");
        var finding = Xunit.Assert.Single(scenario.Goal.Tasks.SelectMany(other =>
            other.LastVerification?.MergedReviewFindings ?? []));
        Xunit.Assert.Equal(ReviewFindingState.Open, finding.State);
        Xunit.Assert.Equal(FindingSeverity.Blocking, finding.Severity);
        Xunit.Assert.Equal(AgentRole.Developer, ReviewFindingRouting.Project([finding])[0].TargetRole);
        Xunit.Assert.Equal("UndeclaredGammaTests", Xunit.Assert.Single(finding.EvidenceRequest!.Selections).TestClass);
        if (!answer) Xunit.Assert.DoesNotContain(scenario.Goal.Timeline, item =>
            item.TaskId == scenario.Task.Id && item.Kind == ProgressKind.HumanInputReceived);
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "finding-class-undeclared");
    }

    [Xunit.Fact]
    public void AnswerAfterGateRetryStillRejectsUndeclaredFailingTestClass()
    {
        var scenario = CreateScenario();
        scenario.Retry("Acceptance criteria unmet; retrying task with feedback; failing_tests=Suite.UndeclaredTests.Fails");
        scenario.Answer();
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "failing-test-class-undeclared");
    }

    [Xunit.Fact]
    public void AnswerBeforeHeadMovesRejectsDispatchBasedOnOldCandidate()
    {
        var scenario = CreateScenario();
        scenario.Retry();
        scenario.Answer();
        File.WriteAllText(Path.Combine(scenario.Process.WorkingDirectory, "seed.txt"), "new goal head");
        RunGit(scenario.Process.WorkingDirectory, ["add", "seed.txt"], scenario.Clock.UtcNow);
        RunGit(scenario.Process.WorkingDirectory, ["commit", "-m", "Fixture moved head"], scenario.Clock.UtcNow);
        // Commit timestamps are outside the dispatch window, so this pins candidate-not-head.
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        scenario.StartRound(RecognizedOutput);
        Xunit.Assert.NotEqual(scenario.Task.LastDispatch!.BaseCommit,
            ReadGit(scenario.Process.WorkingDirectory, ["rev-parse", "HEAD"]));

        scenario.Refresh();

        AssertRejected(scenario, "candidate-not-head");
    }

    [Xunit.Fact]
    public void RetryFeedbackAfterAnswerRejectsRoundBecauseAnswerIsStale()
    {
        var scenario = CreateScenario();
        scenario.Retry();
        scenario.Answer();
        scenario.Retry("auto-review-retry round 2 convergence brief: retry upstream Developer task.");
        var answer = Xunit.Assert.Single(scenario.Goal.Timeline, item =>
            item.TaskId == scenario.Task.Id && item.Kind == ProgressKind.HumanInputReceived);
        var retry = scenario.Goal.Timeline.Last(item => item.TaskId == scenario.Task.Id &&
            item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskRetryFeedbackUpdated);
        Xunit.Assert.Equal(ProgressKind.TaskRetryFeedbackUpdated, retry.Kind);
        Xunit.Assert.True(retry.OccurredAt > answer.OccurredAt);
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "no-finding-classes");
    }

    [Xunit.Fact]
    public void ConvergenceRetryWithoutAnswerStillRejectsMissingFindingClasses()
    {
        var scenario = CreateScenario();
        scenario.Retry();
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "no-finding-classes");
    }

    private static bool TesterReady(Goal goal, TaskSpec tester) =>
        DispatchReadinessRules.BuildReadyTaskParallelPlan(goal, AgentCatalog.Default().Agents)
            .Batches.FirstOrDefault()?.IntentIds.Contains(tester.Id.Value) == true;

    private static void AssertRejected(Scenario scenario, string code)
    {
        var task = scenario.Task;
        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var decline = Xunit.Assert.Single(task.LastVerification.StandardError.Split('\n'),
            line => line.StartsWith("DEFERRED_NO_CHANGE_DECLINED", StringComparison.Ordinal));
        Xunit.Assert.Equal("DEFERRED_NO_CHANGE_DECLINED reason=" + code, decline.TrimEnd('\r'));
        var failure = scenario.Goal.Timeline.Last(item =>
            item.TaskId == task.Id && item.Kind == ProgressKind.TaskFailed).Message;
        Xunit.Assert.Contains("DISPATCH_REJECTED", failure, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=no-change-evidence", failure, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    private static Scenario CreateScenario()
    {
        var root = CreateSeededDispatchRepository();
        // Only the origin follows the seeded fixture's clock; all event ordering uses this injected clock.
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddMinutes(2));
        var commitAt = clock.UtcNow.AddSeconds(1);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, WorkerResultBlock("seed.txt", "fixture verification",
                "pass - fixture verification completed"), string.Empty, clock,
            mutateWorktree: worktree =>
            {
                File.WriteAllText(Path.Combine(worktree, "seed.txt"), "candidate repair");
                RunGit(worktree, ["add", "seed.txt"], commitAt);
                RunGit(worktree, ["commit", "-m", "Fixture candidate repair"], commitAt);
            });
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single(other => other.Id == task.Id);
        var candidate = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD"]);
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id,
            ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD^"]));
        var runner = new BackgroundDispatchRunner(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(candidate, task.LastDispatch!.ResultCommit);
        Xunit.Assert.NotEqual(task.LastDispatch.BaseCommit, task.LastDispatch.ResultCommit);
        return new Scenario(kernel, goal, task, clock, process, runner, candidate);
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task,
        MutableClock Clock, TaskProcessRecord Process, BackgroundDispatchRunner Runner, string Candidate)
    {
        public void Retry(string message = ConvergenceRetry)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Kernel.RetryTaskAutomatically(Goal.Id, Task.Id, message, RetryCause.NewTestFinding);
            Xunit.Assert.Equal(RetryCause.NewTestFinding, Task.PendingRetryCause);
        }

        public void Answer()
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var request = Kernel.RequestHumanInput(Goal.Id, Task.Id, "Confirm the approved boundary.",
                HumanWaitKind.SpecClarification);
            Kernel.SubmitHumanInput(request.Id, "Yes, three. The implementation meets the approved boundary.");
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, Task.Status);
            Xunit.Assert.Equal(RetryCause.NewTestFinding, Task.PendingRetryCause);
        }

        public void StartRound(string output)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var logs = Path.GetDirectoryName(Process.StandardOutputPath)!;
            var next = Process with
            {
                StandardOutputPath = Path.Combine(logs, "answer-round.out.log"),
                StandardErrorPath = Path.Combine(logs, "answer-round.err.log"),
                ExitCodePath = Path.Combine(logs, "answer-round.exit.txt"),
                StartedAt = Clock.UtcNow,
                CompletedAt = null,
                ExitCode = null
            };
            File.WriteAllText(next.StandardOutputPath, output);
            File.WriteAllText(next.StandardErrorPath, string.Empty);
            File.WriteAllText(next.ExitCodePath, "0");
            Kernel.RecordTaskDispatch(Goal.Id, Task.Id, new TaskDispatchRecord("codex-cli", Process.Command,
                Process.WorkingDirectory, Clock.UtcNow, BaseCommit: Candidate));
            Kernel.RecordTaskProcessStarted(Goal.Id, Task.Id, next);
            Xunit.Assert.Equal(string.Empty, ReadGit(Process.WorkingDirectory, ["status", "--porcelain"]));
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        public void Refresh() => Runner.RefreshLatestProcess(Kernel, Goal.Id, Task.Id);
    }
}
