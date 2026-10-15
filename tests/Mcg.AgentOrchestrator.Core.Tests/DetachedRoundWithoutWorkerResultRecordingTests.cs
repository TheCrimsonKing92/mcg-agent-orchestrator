using System.Text;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: all dispatch/process evidence stays in memory and time uses FakeClock.
public sealed class DetachedRoundWithoutWorkerResultRecordingTests
{
    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void RecordDispatch_DetachedProseRound_RequeuesAndStampsHistory(AgentRole role)
    {
        var (kernel, goal, task, clock) = VerificationGoal(role);

        RecordRound(kernel, goal, task, clock, attempt: 1);

        Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
        Assert.Contains(task.Status, new[] { WorkTaskStatus.Assigned, WorkTaskStatus.Pending });
        Assert.Equal(RetryCause.ProviderInterruption, task.PendingRetryCause);
        Assert.Equal(clock.UtcNow, task.LatestRetryAt);
        Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(1), task.SubscriptionRetryAfter);
        Assert.Null(task.LastVerification);
        var history = Assert.Single(task.VerificationHistory);
        Assert.Equal("detached-without-worker-result", history.CompletionVerdictRule);
        Assert.Equal(false, history.CompletionVerdictVerifiedSuccess);
        Assert.Equal(1, DispatchFailureClassifier.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
        Assert.Equal(0, DispatchFailureClassifier.CountConsecutiveProviderInterruptionFailures(task));
        var retry = Assert.Single(goal.Timeline.Where(
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        Assert.Equal(
            "Dispatch was gracefully detached and exited without a WORKER_RESULT; task is ready to retry after bounded backoff (attempt 1/3): codex exec round 1",
            retry.Message);
        Assert.DoesNotContain(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith("CLASSIFIER ", StringComparison.Ordinal) &&
            evt.Message.Contains("verdict=VerifiedSuccess", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void RecordDispatch_FourthConsecutiveDetachedRound_FailsTask(AgentRole role)
    {
        var (kernel, goal, task, clock) = VerificationGoal(role);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            RecordRound(kernel, goal, task, clock, attempt);
            Assert.Equal(attempt,
                DispatchFailureClassifier.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
            Assert.Equal("detached-without-worker-result",
                task.VerificationHistory[^1].CompletionVerdictRule);
            Assert.Equal(false, task.VerificationHistory[^1].CompletionVerdictVerifiedSuccess);
            if (attempt <= 3)
            {
                Assert.Equal(WorkTaskStatus.Assigned, task.Status);
                Assert.Equal(RetryCause.ProviderInterruption, task.PendingRetryCause);
                Assert.Null(task.LastVerification);
                Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(attempt), task.SubscriptionRetryAfter);
                Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id &&
                    evt.Kind == ProgressKind.TaskRetried &&
                    evt.Message.Contains($"(attempt {attempt}/3)", StringComparison.Ordinal));
            }

            clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(3, goal.Timeline.Count(
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried));
        var failure = Assert.Single(goal.Timeline.Where(
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
        Assert.Equal(
            "Dispatch detached-without-worker-result repeated 4 consecutive time(s); automatic retry budget 3 exhausted: codex exec round 4",
            failure.Message);
        Assert.DoesNotContain(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("succeeded-dispatch-completion-evidence")]
    [InlineData("provider-interruption")]
    [InlineData("DETACHED-WITHOUT-WORKER-RESULT")]
    public void CountConsecutive_NonMatchingRule_StopsAtNewestRun(string? otherRule)
    {
        var clock = new FakeClock();
        var task = new TaskSpec(TaskId.New(), "Count detached rounds", AgentRole.Reviewer);
        Assert.Equal(0, DispatchFailureClassifier.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
        foreach (var rule in new[] { "detached-without-worker-result", otherRule,
                     "detached-without-worker-result", "detached-without-worker-result" })
        {
            clock.Advance();
            task.RecordVerification(new TaskVerificationRecord("round", "C:\\repo", 0,
                "The source has been inspected.", string.Empty, clock.UtcNow));
            task.RecordCompletionVerdict(false, rule);
        }

        Assert.Equal(2, DispatchFailureClassifier.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Tester, true)]
    public void DetachmentCannotReplaceMalformedOrStaleContract(AgentRole role, bool stale)
    {
        var (kernel, goal, task, clock) = VerificationGoal(role);
        const string command = "codex exec";
        var startedAt = clock.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex-cli", command, "C:\\repo", startedAt,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242, command, "C:\\repo", "out.log", "err.log",
                "exit.txt", startedAt, null, null));
        kernel.RecordTaskProcessGracefullyDetached(goal.Id, task.Id,
            task.LastProcess! with { WasGracefullyDetachedByConductor = true });
        clock.Advance();
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            task.LastProcess! with { CompletedAt = clock.UtcNow, ExitCode = 0 }, null);
        var verification = new TaskVerificationRecord(command, "C:\\repo", 0,
            "WORKER_RESULT:\nfiles: none", string.Empty, clock.UtcNow,
            DispatchStartedAt: stale ? null : startedAt);

        Assert.Equal(stale ? VerificationTerminalContractStatus.Stale : VerificationTerminalContractStatus.Malformed,
            WorkerResultBlockers.AssessSubscriptionVerification(task, verification).Status);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

        var history = Assert.Single(task.VerificationHistory);
        Assert.Equal(false, history.CompletionVerdictVerifiedSuccess);
        Assert.Equal("verification-terminal-contract-incomplete", history.CompletionVerdictRule);
        Assert.Equal(0, DispatchFailureClassifier.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
        Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, FakeClock Clock) VerificationGoal(AgentRole role)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Record detached reviewer rounds",
        [
            new TaskSpec(TaskId.New(), "Implement behavior", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Verify behavior", role)
        ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single(task => task.RequiredRole == role);
        Assert.NotNull(task.AssignedAgentId);
        return (kernel, goal, task, clock);
    }

    private static void RecordRound(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, FakeClock clock, int attempt)
    {
        var command = $"codex exec round {attempt}";
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex-cli", command, "C:\\repo", clock.UtcNow,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242 + attempt, command, "C:\\repo", "out.log", "err.log",
                "exit.txt", clock.UtcNow, null, null));
        kernel.RecordTaskProcessGracefullyDetached(goal.Id, task.Id,
            task.LastProcess! with { WasGracefullyDetachedByConductor = true });
        Assert.True(task.LastProcess!.WasGracefullyDetachedByConductor);
        Assert.True(task.LastProcess.IsRunning);
        clock.Advance();
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            task.LastProcess with { CompletedAt = clock.UtcNow, ExitCode = 0 }, null);
        var prose = string.Concat(Enumerable.Repeat("The current source has been inspected. ", 32))
            .PadRight(1248, ' ');
        Assert.Equal(1248, Encoding.UTF8.GetByteCount(prose));
        Assert.DoesNotContain("WORKER_RESULT", prose, StringComparison.Ordinal);

        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(command, "C:\\repo", 0, prose, string.Empty, clock.UtcNow,
                DispatchStartedAt: task.LastProcess.StartedAt));
    }
}
