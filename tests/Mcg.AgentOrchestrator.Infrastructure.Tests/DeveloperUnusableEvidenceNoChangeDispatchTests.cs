using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperUnusableEvidenceNoChangeDispatchTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void UnusableEvidenceRetryQualifiesAgainWithoutReviewFindings()
    {
        var (kernel, goal, task, _, runner, candidate) = Scenario();
        Xunit.Assert.Empty(goal.Tasks.SelectMany(other => other.LastVerification?.MergedReviewFindings ?? []));

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        AssertCompleted(task, candidate);
        var classifier = goal.Timeline.Last(item => item.TaskId == task.Id &&
            item.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal));
        Xunit.Assert.Contains("rule=deferred-no-change-round", classifier.Message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.OccurredAt >= task.LatestRetryAt), item =>
            item.Message.Contains("DISPATCH_REJECTED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void UnusableEvidenceRetryStillRequiresOpenFindingClass()
    {
        var (kernel, goal, task, _, runner, _) = Scenario();
        DeveloperDeferredNoChangeDeclineCodeTests.RecordFinding(kernel, goal, "UndeclaredGammaTests");
        var finding = Xunit.Assert.Single(goal.Tasks.SelectMany(other =>
            other.LastVerification?.MergedReviewFindings ?? []));
        Xunit.Assert.Equal(ReviewFindingState.Open, finding.State);
        Xunit.Assert.Equal(FindingSeverity.Blocking, finding.Severity);
        Xunit.Assert.Equal(AgentRole.Developer, ReviewFindingRouting.Project([finding])[0].TargetRole);
        Xunit.Assert.Equal("UndeclaredGammaTests", Xunit.Assert.Single(finding.EvidenceRequest!.Selections).TestClass);

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        AssertDeclined(task, "finding-class-undeclared");
        var classifier = goal.Timeline.Last(item => item.TaskId == task.Id &&
            item.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal));
        Xunit.Assert.Contains("DEFERRED_NO_CHANGE_DECLINED reason=finding-class-undeclared", classifier.Message,
            StringComparison.Ordinal);
        Xunit.Assert.Contains(goal.Timeline, item => item.TaskId == task.Id &&
            item.Message.Contains("DISPATCH_REJECTED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void DirtyNoChangeRoundRecordsOneDirtyDecline()
    {
        var (kernel, goal, task, _, runner, _) = Scenario(dirty: true);

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        AssertDeclined(task, "dirty");
        Xunit.Assert.Contains("left the worktree dirty", task.LastVerification!.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RoundWithoutNoChangeRationaleRecordsNoDeclineLine()
    {
        var (kernel, goal, task, _, runner, _) = Scenario(rationale: false);

        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_DECLINED", task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    private static void AssertCompleted(TaskSpec task, string candidate)
    {
        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed,
            $"Actual status: {task.Status}; verification: {task.LastVerification?.StandardError}");
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal("deferred-no-change-round", task.LastVerification.CompletionVerdictRule);
        Xunit.Assert.Equal(candidate, task.LastDispatch!.ResultCommit);
        Xunit.Assert.True(DeferredNoChangeOutcome.TryParse(task.LastVerification.StandardError, out var outcome));
        Xunit.Assert.Equal(candidate, outcome.CandidateSha);
        Xunit.Assert.Equal(["DeferredAlphaTests", "DeferredBetaTests"], outcome.TestClasses);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_DECLINED", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    private static void AssertDeclined(TaskSpec task, string code)
    {
        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var line = Xunit.Assert.Single(task.LastVerification.StandardError.Split('\n')
            .Where(line => line.StartsWith("DEFERRED_NO_CHANGE_DECLINED", StringComparison.Ordinal)));
        Xunit.Assert.Equal("DEFERRED_NO_CHANGE_DECLINED reason=" + code, line.TrimEnd('\r'));
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, MutableClock Clock,
        BackgroundDispatchRunner Runner, string Candidate) Scenario(bool dirty = false, bool rationale = true)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddMinutes(2));
        var commitAt = clock.UtcNow.AddSeconds(1);
        // Round 1 really commits a relevant file; its base is the parent of that commit.
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, WorkerResultBlock("seed.txt", "fixture verification",
                "pass - fixture verification completed"), string.Empty, clock,
            mutateWorktree: worktree =>
            {
                File.WriteAllText(Path.Combine(worktree, "seed.txt"), "candidate repair");
                RunGit(worktree, ["add", "seed.txt"], commitAt);
                RunGit(worktree, ["commit", "-m", "Fixture candidate repair"], commitAt);
            });
        // The shared fixture creates its kernel with the system clock; keep later events on the explicit clock.
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single(other => other.Id == task.Id);
        var candidate = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD"]);
        var parent = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD^"]);
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, parent);
        var runner = new BackgroundDispatchRunner(clock);
        clock.Advance(TimeSpan.FromSeconds(2));
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(candidate, task.LastDispatch!.ResultCommit);
        Xunit.Assert.NotEqual(task.LastDispatch.BaseCommit, task.LastDispatch.ResultCommit);

        // Round 2 completes as a deferred no-change round on the committed candidate.
        kernel.RetryTask(goal.Id, task.Id, "Review the unchanged candidate after evidence.");
        StartRound(2, Output(), false);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        AssertCompleted(task, candidate);

        // Round 3 follows the conductor's unusable-evidence retry, without failing_tests=.
        kernel.RetryTaskAutomatically(goal.Id, task.Id,
            "DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE candidate_sha=" + candidate +
            "; receipt_id=unusable; not_run=; requested_classes=DeferredAlphaTests,DeferredBetaTests",
            RetryCause.NewTestFinding, retryRoundKind: RetryRoundKind.Mechanical);
        Xunit.Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
        var retry = goal.Timeline.Last(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried);
        Xunit.Assert.StartsWith("DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE candidate_sha=", retry.Message,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("failing_tests=", retry.Message, StringComparison.Ordinal);
        if (dirty) File.WriteAllText(Path.Combine(process.WorkingDirectory, "dirty.txt"), "uncommitted");
        StartRound(3, Output(rationale), dirty);
        Xunit.Assert.Equal(candidate, task.LastDispatch!.BaseCommit);
        Xunit.Assert.Equal(dirty, ReadGit(process.WorkingDirectory, ["status", "--porcelain"]).Length > 0);
        return (kernel, goal, task, clock, runner, candidate);

        void StartRound(int round, string output, bool lowIntegrity)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var logs = Path.GetDirectoryName(process.StandardOutputPath)!;
            var next = process with
            {
                StandardOutputPath = Path.Combine(logs, $"round-{round}.out.log"),
                StandardErrorPath = Path.Combine(logs, $"round-{round}.err.log"),
                ExitCodePath = Path.Combine(logs, $"round-{round}.exit.txt"),
                StartedAt = clock.UtcNow,
                CompletedAt = null,
                ExitCode = null
            };
            File.WriteAllText(next.StandardOutputPath, output);
            File.WriteAllText(next.StandardErrorPath, string.Empty);
            File.WriteAllText(next.ExitCodePath, "0");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", process.Command,
                process.WorkingDirectory, clock.UtcNow, BaseCommit: candidate, SandboxLowIntegrity: lowIntegrity));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, next);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private static string Output(bool rationale = true) =>
        (rationale ? "NO_CHANGE: the candidate already contains the repair.\n" : string.Empty) +
        WorkerResultBlock(rationale ? "none" : "Alpha.cs", "none", "deferred - DeferredAlphaTests, DeferredBetaTests", commit: "none");
}
