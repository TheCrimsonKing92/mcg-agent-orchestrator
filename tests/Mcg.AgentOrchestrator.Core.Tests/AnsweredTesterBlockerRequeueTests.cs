using Mcg.AgentOrchestrator.Core;

// Parallel-safe: only in-memory kernel state and synthetic process records are used.
public sealed class AnsweredTesterBlockerRequeueTests
{
    private const string Question = "The objective names JSON fields in `snake_case` but also requires `JsonSerializerDefaults.Web`, which emits `camelCase`. Should the JSON keys be snake_case or camelCase?";
    private const string Answer = "Use camelCase JSON keys. Serialize the report with `new JsonSerializerOptions(JsonSerializerDefaults.Web)` and set no `PropertyNamingPolicy` and no `[JsonPropertyName]` attributes.";
    private const string Blocker = "exact-blocker - JSON key contract needs operator clarification; execution evidence is pending";
    private const string Completion = "Task completed after required human input was answered.";

    [Xunit.Fact]
    public void Answered_succeeded_tester_blocker_requeues_with_contract_feedback()
    {
        var (kernel, goal, task, request, verification) = QuestionRound();
        Assert.True(verification.Succeeded);
        Assert.Equal(VerificationGateReason.TesterWorkerResultBlocker,
            Assert.Single(kernel.BuildVerificationGate(goal.Id).Tasks).Reason);

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertRequeued(kernel, goal, task, verification);
        Assert.True(request.IsCompleted);
        var timeline = goal.Timeline.ToList();
        var received = timeline.FindIndex(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputReceived);
        var retried = timeline.FindIndex(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskRetried);
        Assert.True(received >= 0 && retried > received);
        Assert.DoesNotContain(timeline, item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskCompleted && item.Message == Completion);
    }

    [Xunit.Fact]
    public void Answered_failed_tester_blocker_requeues_without_passing_verification()
    {
        var (kernel, goal, task, request, verification) = QuestionRound(exitCode: 1);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Same Tester needs the answer.");
        Assert.False(verification.Succeeded);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertRequeued(kernel, goal, task, verification);
    }

    [Xunit.Fact]
    public void Answered_tester_failing_tests_keeps_existing_completion_and_gate()
    {
        var (kernel, goal, task, request, verification) = QuestionRound(tests: "fail - Suite.SomeTests.Fails");

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertExistingCompletion(goal, task, verification);
        Assert.Equal(VerificationGateStatus.FailedVerification,
            Assert.Single(kernel.BuildVerificationGate(goal.Id).Tasks).GateStatus);
    }

    [Xunit.Fact]
    public void Answered_complete_developer_keeps_verification_without_retry()
    {
        var (kernel, goal, task, request, verification) = QuestionRound(role: AgentRole.Developer);

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertExistingCompletion(goal, task, verification);
    }

    [Xunit.Fact]
    public void Answered_tester_without_blocker_keeps_existing_completion()
    {
        var (kernel, goal, task, request, verification) = QuestionRound(blockers: "none");

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertExistingCompletion(goal, task, verification);
    }

    [Xunit.Fact]
    public void Answered_retry_cause_question_does_not_retry_worker_blocker()
    {
        var (kernel, goal, task, request, verification) = QuestionRound();
        var causeRequest = kernel.RequestHumanInput(goal.Id, task.Id, "Which retry cause?",
            HumanWaitKind.SpecClarification);
        kernel.SubmitHumanInput(request.Id, Answer);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);

        kernel.SubmitHumanInput(causeRequest.Id, nameof(RetryCause.ContractClarification));

        AssertExistingCompletion(goal, task, verification);
    }

    [Xunit.Fact]
    public void Answer_after_existing_downstream_retry_adds_no_second_retry()
    {
        var developer = new TaskSpec(TaskId.New(), "Provide the candidate.", AgentRole.Developer);
        var (kernel, goal, task, request, verification) = QuestionRound(upstream: developer);
        kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Candidate ready.");
        Assert.Throws<InvalidOperationException>(() =>
            kernel.RetryTask(goal.Id, task.Id, Answer, RetryCause.ContractClarification));
        kernel.RetryTask(goal.Id, developer.Id, Answer, RetryCause.ContractClarification);
        var retry = Assert.Single(goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(RetryCause.ContractClarification, task.PendingRetryCause);
        var timeline = goal.Timeline.ToList();
        Assert.True(timeline.IndexOf(retry) > timeline.FindLastIndex(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputRequested));

        // Retain blocker evidence to prove suppression is keyed on the retry event, not its clearing.
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
        Assert.NotNull(task.LastVerification);
        kernel.SubmitHumanInput(request.Id, Answer);

        Assert.Same(retry, Assert.Single(goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskRetried)));
    }

    [Xunit.Fact]
    public void Answer_with_running_downstream_keeps_existing_completion_and_gate()
    {
        var (kernel, goal, task, request, verification) = QuestionRound();
        var reviewer = kernel.AddTask(goal.Id, AgentRole.Reviewer, "Review the candidate.",
            availableAgents: DefaultAgents());
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id,
            new TaskDispatchRecord("reviewer", "reviewer-worker.exe", "synthetic-review", verification.CompletedAt));
        kernel.RecordTaskProcessStarted(goal.Id, reviewer.Id,
            Process("reviewer-worker.exe", "synthetic-review", verification.CompletedAt));
        Assert.True(reviewer.LastProcess!.IsRunning);

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertExistingCompletion(goal, task, verification);
        Assert.Equal(VerificationGateStatus.FailedVerification,
            kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == task.Id).GateStatus);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Answer_with_running_tester_keeps_existing_completion(bool runningProcess)
    {
        var (kernel, goal, task, request, verification) = QuestionRound();
        if (runningProcess)
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                Process(verification.Command, verification.WorkingDirectory, verification.CompletedAt));
        else
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Worker is running.");

        kernel.SubmitHumanInput(request.Id, Answer);

        AssertExistingCompletion(goal, task, verification);
    }

    private static void AssertRequeued(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        TaskVerificationRecord verification)
    {
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(RetryCause.ContractClarification, task.PendingRetryCause);
        var retry = Assert.Single(goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(Answer, retry.Message);
        Assert.Null(task.LastVerification);
        Assert.Contains(verification, task.VerificationHistory);
        Assert.NotEqual(VerificationGateReason.TesterWorkerResultBlocker,
            kernel.BuildVerificationGate(goal.Id).Tasks.Single(item => item.TaskId == task.Id).Reason);
    }

    private static void AssertExistingCompletion(Goal goal, TaskSpec task, TaskVerificationRecord verification)
    {
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Same(verification, task.LastVerification);
        Assert.DoesNotContain(goal.Timeline, item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried);
        Assert.Contains(goal.Timeline, item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskCompleted && item.Message == Completion);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, HumanInputRequest Request,
        TaskVerificationRecord Verification) QuestionRound(AgentRole role = AgentRole.Tester,
        string tests = "deferred - Suite.SomeTests", string blockers = Blocker, int exitCode = 0,
        TaskSpec? upstream = null)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Verify the JSON contract.", role);
        var goal = kernel.CreateGoal("Resume answered blocker verification.",
            upstream is null ? [task] : [upstream, task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var recordedAt = clock.UtcNow;
        const string command = "worker.exe";
        const string directory = "synthetic-worker";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", command, directory, recordedAt));
        var process = Process(command, directory, recordedAt);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            process with { CompletedAt = recordedAt, ExitCode = exitCode }, null);
        var output = $$"""
            WORKER_RESULT:
            files: none
            commands: none
            tests: {{tests}}
            commit: none
            blockers: {{blockers}}
            assigned_scope_complete: true
            model_fit: OpenAI/gpt-6.1-sol - adequate - verification
            skills: none
            confidence: high
            END_WORKER_RESULT
            HUMAN_INPUT: {{Question}}
            """;
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(command, directory, exitCode, output, string.Empty, recordedAt,
                WorkerResultPresent: true, HumanInputQuestion: Question, AssignedScopeComplete: true));
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.StartsWith(Question, request.Question, StringComparison.Ordinal);
        return (kernel, goal, task, request, task.LastVerification!);
    }

    private static TaskProcessRecord Process(string command, string directory, DateTimeOffset recordedAt) =>
        new(12345, command, directory, "out.log", "err.log", "exit.txt", recordedAt, null, null);
}
