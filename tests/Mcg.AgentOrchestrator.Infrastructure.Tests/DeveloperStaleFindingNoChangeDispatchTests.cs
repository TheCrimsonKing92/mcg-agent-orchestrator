using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperStaleFindingNoChangeDispatchTests : WorkerDispatchTestSupport
{
    // Verbatim WORKER_RESULT from the measured cda5e26a round.
    private const string RecordedWorkerResult = """
        WORKER_RESULT:
        files: none
        commands: git status --short; git diff --stat main...HEAD; git diff --unified=0 main...HEAD; git diff --check main...HEAD; .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-WorkerBuildCheck.ps1 tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
        tests: deferred - ProgressiveReviewGlanceModelRepointTests, SparkLaneModelRepointDispatchTests, WorkerDispatchTestsModelSelection, WorkerDispatchTestsModelSelectionEnvMutation, CliCommandTestsSubscriptionDispatchCommands, BudgetAwareRoutingTests, ProgressiveReviewGlanceTests, SemanticAcceptanceTests, TesterCascadeDispatchTests, RealWorkerProcessGuardTests, ClaudeSubscriptionEffortArgumentTests
        commit: none
        blockers: none
        assigned_scope_complete: true
        model_fit: OpenAI/gpt-6.1-sol - adequate - scoped candidate verification - confirmed existing implementation and fresh build evidence
        skills: dotnet-windows-build-hygiene, orchestrator-worker-verification, verification-before-completion
        confidence: high
        END_WORKER_RESULT
        """;

    private static string RecognizedOutput =>
        "Invoke-WorkerBuildCheck.ps1 build check finished with exit code 0.\n" + RecordedWorkerResult;

    [Xunit.Fact]
    public void StaleTesterFindingRetryCompletesDeferredNoChangeRound()
    {
        var scenario = CreateScenario();
        var tester = RecordRaisingRound(scenario, AgentRole.Tester,
            ReadGit(scenario.Process.WorkingDirectory, ["rev-parse", "HEAD^"]));
        RetryFinding(scenario, tester);
        Xunit.Assert.Null(tester.LastVerification);
        scenario.StartRound(RecordedWorkerResult);
        Xunit.Assert.False(TesterReady(scenario.Goal, tester));

        scenario.Refresh();

        AssertCompleted(scenario);
        Xunit.Assert.True(TesterReady(scenario.Goal, tester));
    }

    [Xunit.Fact]
    public void CurrentHeadTesterFindingRetryStillRejectsMissingFindingClasses()
    {
        var scenario = CreateScenario();
        var tester = RecordRaisingRound(scenario, AgentRole.Tester, scenario.Candidate);
        RetryFinding(scenario, tester);
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "no-finding-classes");
    }

    [Xunit.Fact]
    public void StaleRetryWithUndeclaredFindingClassStillRejects()
    {
        var scenario = CreateScenario();
        var tester = RecordRaisingRound(scenario, AgentRole.Tester,
            ReadGit(scenario.Process.WorkingDirectory, ["rev-parse", "HEAD^"]));
        RetryFinding(scenario, tester);
        DeveloperDeferredNoChangeDeclineCodeTests.RecordFinding(scenario.Kernel, scenario.Goal,
            "UndeclaredGammaTests");
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "finding-class-undeclared");
    }

    [Xunit.Fact]
    public void RaisingRoundWithoutReviewedCommitStillRejectsMissingFindingClasses()
    {
        var scenario = CreateScenario();
        var tester = RecordRaisingRound(scenario, AgentRole.Tester, null);
        Xunit.Assert.Null(tester.LastVerification!.ReviewedCommit);
        RetryFinding(scenario, tester);
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "no-finding-classes");
    }

    [Xunit.Fact]
    public void StaleReviewerNeedsWorkRetryCompletesDeferredNoChangeRound()
    {
        var scenario = CreateScenario();
        var reviewer = RecordRaisingRound(scenario, AgentRole.Reviewer,
            ReadGit(scenario.Process.WorkingDirectory, ["rev-parse", "HEAD^"]));
        RetryFinding(scenario, reviewer);
        scenario.StartRound(RecordedWorkerResult);

        scenario.Refresh();

        AssertCompleted(scenario);
    }

    [Xunit.Fact]
    public void NewestNonConvergenceFeedbackDoesNotReuseOlderStaleRetry()
    {
        var scenario = CreateScenario();
        var tester = RecordRaisingRound(scenario, AgentRole.Tester,
            ReadGit(scenario.Process.WorkingDirectory, ["rev-parse", "HEAD^"]));
        RetryFinding(scenario, tester);
        scenario.Retry("ACTIONABLE_CANDIDATE_RED: current evidence requires a source repair.");
        scenario.StartRound(RecognizedOutput);

        scenario.Refresh();

        AssertRejected(scenario, "no-finding-classes");
    }

    private static TaskSpec RecordRaisingRound(Scenario scenario, AgentRole role, string? reviewedCommit)
    {
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        var raising = scenario.Kernel.AddTask(scenario.Goal.Id, role, "Check the candidate.",
            AgentCatalog.Default().Agents);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, raising.Id,
            new TaskDispatchRecord("codex-cli", "fixture", scenario.Process.WorkingDirectory,
                scenario.Clock.UtcNow, BaseCommit: reviewedCommit));
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        var output = WorkerResultBlock("none", "fixture check", "fail - candidate finding",
            commit: "none", blockers: "candidate finding remains");
        if (role == AgentRole.Reviewer) output = output.Replace("END_WORKER_RESULT",
            "verdict: needs-work\nEND_WORKER_RESULT", StringComparison.Ordinal);
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, raising.Id,
            new TaskVerificationRecord("fixture", scenario.Process.WorkingDirectory, 1, output,
                string.Empty, scenario.Clock.UtcNow, WorkerResultPresent: true));
        Xunit.Assert.Equal(WorkTaskStatus.Failed, raising.Status);
        Xunit.Assert.Equal(reviewedCommit, raising.LastVerification!.ReviewedCommit);
        if (role == AgentRole.Reviewer)
            Xunit.Assert.Equal("needs-work", UnchangedCandidateVerdict.Derive(scenario.Goal, raising, raising.LastVerification));
        return raising;
    }

    private static void RetryFinding(Scenario scenario, TaskSpec raising) => scenario.Retry(
        $"auto-review-retry round 1 convergence brief: {raising.RequiredRole} task {raising.Id.Value[..8]} " +
        (raising.RequiredRole == AgentRole.Reviewer ? "verdict=needs-work" : "WORKER_RESULT blocker") +
        "; retry upstream Developer task.");

    private static void AssertCompleted(Scenario scenario)
    {
        var task = scenario.Task;
        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed,
            $"Actual status: {task.Status}; verification: {task.LastVerification?.StandardError}");
        Xunit.Assert.Equal("deferred-no-change-round", task.LastVerification!.CompletionVerdictRule);
        Xunit.Assert.Equal(scenario.Candidate, task.LastDispatch!.ResultCommit);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_DECLINED", task.LastVerification.StandardError,
            StringComparison.Ordinal);
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
        public void Retry(string message)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Kernel.RetryTaskAutomatically(Goal.Id, Task.Id, message, RetryCause.NewTestFinding);
            Xunit.Assert.Equal(RetryCause.NewTestFinding, Task.PendingRetryCause);
        }

        public void StartRound(string output)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var logs = Path.GetDirectoryName(Process.StandardOutputPath)!;
            var next = Process with
            {
                StandardOutputPath = Path.Combine(logs, "stale-round.out.log"),
                StandardErrorPath = Path.Combine(logs, "stale-round.err.log"),
                ExitCodePath = Path.Combine(logs, "stale-round.exit.txt"),
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

