using Mcg.AgentOrchestrator.Core;

public sealed class DuplicateHumanInputRoundGuardTests
{
    [Xunit.Fact]
    public void Clean_rounds_after_one_answered_raise_never_suppress_or_fail()
    {
        var (clock, kernel, goal, task) = CreateScenario();
        const string question = "Confirm the authoritative retry floor?";
        RecordRound(clock, kernel, goal, task, question, "exact-blocker - operator decision required");
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");

        for (var index = 0; index < 7; index++)
        {
            if (task.Status == WorkTaskStatus.Completed)
            {
                kernel.RetryTask(goal.Id, task.Id, $"Replay clean round {index + 1}.");
            }

            RecordRound(clock, kernel, goal, task, humanInputQuestion: null, blockers: "none");
        }

        Assert.Equal(0, request.SuppressionCount);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);
    }

    [Xunit.Fact]
    public void Three_consecutive_genuine_reraises_fail_with_round_and_log_receipt()
    {
        var (clock, kernel, goal, task) = CreateScenario();
        const string question = "Confirm the authoritative retry floor?";
        const string blocker = "exact-blocker - operator decision required";
        RecordRound(clock, kernel, goal, task, question, blocker);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");

        RecordRound(clock, kernel, goal, task, question, blocker);
        RecordRound(clock, kernel, goal, task, question, blocker);
        RecordRound(clock, kernel, goal, task, question, blocker);

        Assert.Equal(3, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal([1, 2, 3], goal.Timeline
            .Where(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed)
            .Select(item => ReadCount(item.Message)));
        var failure = Assert.Single(goal.Timeline.Where(item =>
            item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains(request.Id.Value[..8], StringComparison.Ordinal)));
        Assert.Contains("round=4", failure.Message, StringComparison.Ordinal);
        Assert.Contains("round-4.out.log", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Clean_round_resets_streak_and_new_question_is_isolated()
    {
        var (clock, kernel, goal, task) = CreateScenario();
        const string firstQuestion = "Confirm the authoritative retry floor?";
        const string firstBlocker = "exact-blocker - operator decision required";
        RecordRound(clock, kernel, goal, task, firstQuestion, firstBlocker);
        var first = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(first.Id, "Use five seconds.");
        RecordRound(clock, kernel, goal, task, firstQuestion, firstBlocker);
        RecordRound(clock, kernel, goal, task, firstQuestion, firstBlocker);
        Assert.Equal(2, first.SuppressionCount);

        RecordRound(clock, kernel, goal, task, humanInputQuestion: null, blockers: "none");
        Assert.Equal(0, first.SuppressionCount);
        kernel.RetryTask(goal.Id, task.Id, "Ask a genuinely new question.");
        RecordRound(clock, kernel, goal, task, "Which output format should be used?", "none");

        Assert.Equal(0, first.SuppressionCount);
        var second = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, second.SuppressionCount);
    }

    [Xunit.Fact]
    public void Missing_worker_result_leaves_streak_unchanged_and_emits_inconclusive_event()
    {
        var (clock, kernel, goal, task) = CreateScenario();
        const string question = "Confirm the authoritative retry floor?";
        const string blocker = "exact-blocker - operator decision required";
        RecordRound(clock, kernel, goal, task, question, blocker);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");
        RecordRound(clock, kernel, goal, task, question, blocker);
        var suppressionsBefore = goal.Timeline.Count(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed);

        RecordRound(
            clock,
            kernel,
            goal,
            task,
            question,
            blocker,
            workerResultPresent: false,
            exitCode: 1);

        Assert.Equal(1, request.SuppressionCount);
        Assert.Equal(suppressionsBefore, goal.Timeline.Count(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed));
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.HumanInputRoundEvaluationInconclusive);
    }

    [Xunit.Fact]
    public void Human_input_emission_wins_over_blockers_none_and_records_contradiction()
    {
        var (clock, kernel, goal, task) = CreateScenario();
        const string question = "Confirm the authoritative retry floor?";
        RecordRound(clock, kernel, goal, task, question, "none");
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");

        RecordRound(clock, kernel, goal, task, question, "none");

        Assert.Equal(1, request.SuppressionCount);
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed);
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.HumanInputWorkerResultContradiction);
    }

    [Xunit.Fact]
    public void Quoted_stderr_directive_and_blocker_do_not_count_as_current_worker_output()
    {
        var (clock, kernel, goal, task) = CreateScenario();
        const string question = "Confirm the authoritative retry floor?";
        const string blocker = "exact-blocker - operator decision required";
        RecordRound(clock, kernel, goal, task, question, blocker);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");
        RecordRound(clock, kernel, goal, task, question, blocker);
        var suppressionsBefore = goal.Timeline.Count(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed);

        RecordRound(
            clock,
            kernel,
            goal,
            task,
            humanInputQuestion: null,
            blockers: "none",
            standardError: $"Upstream context: blockers: {blocker}{Environment.NewLine}HUMAN_INPUT: {question}");

        Assert.Equal(0, request.SuppressionCount);
        Assert.Equal(suppressionsBefore, goal.Timeline.Count(item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    [Xunit.Fact]
    public async Task Api_runs_count_consecutive_reraises_with_round_receipts()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var agents = DefaultAgents();
        var goal = kernel.CreateGoal("Guard duplicate human input in API runs.");
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
        const string question = "Confirm the authoritative retry floor?";
        var provider = new FakeModelProvider("OpenAI", WorkerResult("none") +
            $"{Environment.NewLine}HUMAN_INPUT: {question}");
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([provider]), clock);

        await runner.RunAsync(goal.Id, task.Id, TestContext.Current.CancellationToken);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");
        for (var round = 2; round <= 4; round++)
        {
            kernel.RetryTask(goal.Id, task.Id, $"Replay API round {round}.");
            clock.Advance();
            await runner.RunAsync(goal.Id, task.Id, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(3, goal.Timeline.Count(evt => evt.Kind == ProgressKind.HumanInputWorkerResultContradiction));
        var failure = Assert.Single(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains(request.Id.Value[..8], StringComparison.Ordinal));
        Assert.Contains("round=4", failure.Message, StringComparison.Ordinal);
        Assert.Contains("worker_result_log=api-run:OpenAI/", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task Clean_api_run_resets_answered_reraise_streak()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var agents = DefaultAgents();
        var goal = kernel.CreateGoal("Reset duplicate human input streak in API runs.");
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
        const string question = "Confirm the authoritative retry floor?";
        var blockedRunner = new AgentTaskRunner(
            kernel,
            agents,
            new InMemoryModelProviderRegistry([new FakeModelProvider(
                "OpenAI",
                WorkerResult("exact-blocker - operator decision required") + $"{Environment.NewLine}HUMAN_INPUT: {question}")]),
            clock);

        await blockedRunner.RunAsync(goal.Id, task.Id, TestContext.Current.CancellationToken);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");
        kernel.RetryTask(goal.Id, task.Id, "Replay one answered re-raise.");
        await blockedRunner.RunAsync(goal.Id, task.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, request.SuppressionCount);

        kernel.RetryTask(goal.Id, task.Id, "Run a clean API round.");
        var cleanRunner = new AgentTaskRunner(
            kernel,
            agents,
            new InMemoryModelProviderRegistry([new FakeModelProvider("OpenAI", WorkerResult("none"))]),
            clock);
        await cleanRunner.RunAsync(goal.Id, task.Id, TestContext.Current.CancellationToken);

        Assert.Equal(0, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
    }

    private static (FakeClock Clock, AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CreateScenario()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement the scoped change.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Guard duplicate human input by completed round.", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (clock, kernel, goal, task);
    }

    private static void RecordRound(
        FakeClock clock,
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string? humanInputQuestion,
        string blockers,
        bool workerResultPresent = true,
        int exitCode = 0,
        string standardError = "")
    {
        if (task.LastVerification is not null)
        {
            kernel.RetryTask(goal.Id, task.Id, $"Replay completed round {task.VerificationHistory.Count + 1}.");
        }

        clock.Advance();
        var round = task.VerificationHistory.Count + 1;
        var command = $"develop-round-{round}";
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("developer", command, "C:\\repo", clock.UtcNow));
        var output = WorkerResult(blockers);
        if (humanInputQuestion is not null)
        {
            output += $"{Environment.NewLine}HUMAN_INPUT: {humanInputQuestion}";
        }

        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                command,
                "C:\\repo",
                exitCode,
                output,
                standardError,
                clock.UtcNow,
                StandardOutputPath: $"C:\\logs\\round-{round}.out.log",
                WorkerResultPresent: workerResultPresent,
                HumanInputQuestion: humanInputQuestion));
    }

    private static string WorkerResult(string blockers) =>
        $$"""
        WORKER_RESULT:
        files: none
        commands: focused verification
        tests: pass - focused verification passed
        commit: none
        blockers: {{blockers}}
        model_fit: OpenAI/gpt-5.6-sol - adequate - focused implementation
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private static int ReadCount(string message)
    {
        var start = message.IndexOf("count=", StringComparison.Ordinal) + "count=".Length;
        var end = message.IndexOf(' ', start);
        return int.Parse(message[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }
}
