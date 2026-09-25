using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class HumanInputResumeDispatchTests
{
    [Xunit.Fact]
    public void Answered_incomplete_developer_round_dispatches_with_answer_without_retry()
    {
        var (kernel, goal, task, request, verification) = FinishedQuestionRound(scopeComplete: false);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-human-input-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        const string answer = "Use the bounded restore path.";
        var retryCount = task.CriterionRetryCount;
        var emptyOutputRetryCount = task.EmptyOutputRetryCount;
        var latestRetryAt = task.LatestRetryAt;
        var retryCause = task.PendingRetryCause;
        var acceptanceRetryCount = goal.AutomaticAcceptanceRetryCount;
        var retryEvents = goal.Timeline.Count(item => item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskFailed);

        kernel.SubmitHumanInput(request.Id, answer);

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        string? dispatchedPrompt = null;
        var dispatches = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: currentGoal =>
            {
                dispatches++;
                var currentTask = currentGoal.FindTask(task.Id);
                var prepared = WorkerProfileDispatcher.PrepareTask(
                    kernel,
                    currentGoal,
                    currentTask,
                    new WorkerProfile("codex-cli", "codex exec --cd {workingDirectory}"),
                    Path.Combine(root, "prompts"),
                    root,
                    DateTimeOffset.UtcNow,
                    providerName: "OpenAI",
                    modelName: AgentCatalog.OpenAiSubscriptionModelAlias);
                dispatchedPrompt = File.ReadAllText(prepared.PromptPath);
                return DispatchStartOutcome.Started([currentTask]);
            });

        var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(advance.Outcome);
        Assert.Equal(1, dispatches);
        Assert.NotNull(task.LastDispatch);
        Assert.Null(task.LastVerification);
        Assert.Contains(verification, task.VerificationHistory);
        Assert.Contains("## Resolved Human Input", dispatchedPrompt, StringComparison.Ordinal);
        Assert.Contains(answer, dispatchedPrompt, StringComparison.Ordinal);
        Assert.Equal(retryCount, task.CriterionRetryCount);
        Assert.Equal(emptyOutputRetryCount, task.EmptyOutputRetryCount);
        Assert.Equal(latestRetryAt, task.LatestRetryAt);
        Assert.Equal(retryCause, task.PendingRetryCause);
        Assert.Equal(acceptanceRetryCount, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(retryEvents, goal.Timeline.Count(item => item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskFailed));
    }

    [Xunit.Fact]
    public void Answered_complete_developer_round_completes_without_clearing_verification()
    {
        var (kernel, goal, task, request, verification) = FinishedQuestionRound(scopeComplete: true);

        kernel.SubmitHumanInput(request.Id, "Use the existing completion path.");

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Same(verification, task.LastVerification);
        Assert.Contains(goal.Timeline, item =>
            item.Kind == ProgressKind.TaskCompleted &&
            item.Message == "Task completed after required human input was answered.");
    }

    [Xunit.Fact]
    public void Answered_round_with_pending_process_record_keeps_running_verification()
    {
        var (kernel, _, task, request, verification) = FinishedQuestionRound(
            scopeComplete: false,
            recordFinishedProcess: false);

        kernel.SubmitHumanInput(request.Id, "Continue the recorded dispatch.");

        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Same(verification, task.LastVerification);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, HumanInputRequest Request,
        TaskVerificationRecord Verification) FinishedQuestionRound(bool scopeComplete, bool recordFinishedProcess = true)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the scoped slice.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Resume an answered Developer round.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var now = DateTimeOffset.UtcNow;
        const string command = "developer-worker.exe";
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"mcg-human-input-round-{Guid.NewGuid():N}");
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("developer", command, workingDirectory, now));
        if (recordFinishedProcess)
        {
            var process = new TaskProcessRecord(
                12345,
                command,
                workingDirectory,
                Path.Combine(workingDirectory, "out.log"),
                Path.Combine(workingDirectory, "err.log"),
                Path.Combine(workingDirectory, "exit.txt"),
                now,
                null,
                null);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
                process with { CompletedAt = now.AddSeconds(1), ExitCode = 0 }, null);
        }
        var output = $$"""
            WORKER_RESULT:
            files: none
            commands: none
            tests: deferred - HumanInputResumeDispatchTests
            commit: none
            blockers: exact-blocker - operator decision required
            assigned_scope_complete: {{scopeComplete.ToString().ToLowerInvariant()}}
            model_fit: OpenAI/gpt-6-sol - adequate - focused implementation
            skills: none
            confidence: high
            END_WORKER_RESULT
            HUMAN_INPUT: Which restore path should be used?
            """;
        var verification = new TaskVerificationRecord(
            command,
            workingDirectory,
            0,
            output,
            string.Empty,
            now.AddSeconds(1),
            WorkerResultPresent: true,
            HumanInputQuestion: "Which restore path should be used?",
            AssignedScopeComplete: scopeComplete);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.True(task.LastVerification?.Succeeded);
        return (kernel, goal, task, request, task.LastVerification!);
    }
}
