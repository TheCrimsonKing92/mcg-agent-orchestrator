using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProgressiveReviewGlanceTests
{
    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_triggers_on_file_count_elapsed_budget_and_concurrent_cap")]
    public void TriggersOnFileCountElapsedBudgetAndConcurrentCap()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        var runner = new ControlledGlanceRunner();
        var events = new RecordingGlanceEvents();
        var coordinator = NewCoordinator(
            runner,
            events,
            utcNow: () => now,
            options: new ProgressiveReviewGlanceOptions(
                FirstElapsedThreshold: TimeSpan.FromMinutes(15),
                ElapsedInterval: TimeSpan.FromMinutes(15)),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));

        var first = runner.EnqueuePending();
        var result = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.Single(runner.Calls);
        Xunit.Assert.Contains(result.ProgressLines, line => line.Contains("result=started", StringComparison.Ordinal));

        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Single(runner.Calls);

        first.SetResult(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "diff aligns", 10, 5));
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Single(events.Receipts);
        Xunit.Assert.Equal("ChangedFiles", events.Receipts[0].Trigger);
        Xunit.Assert.Equal("OnTrack", events.Receipts[0].Verdict);
        Xunit.Assert.Equal(15, events.Receipts[0].TotalTokens);

        now += TimeSpan.FromMinutes(16);
        var second = runner.EnqueuePending();
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Equal(2, runner.Calls.Count);
        second.SetResult(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok again", "still aligned", 6, 4));
        _ = coordinator.Observe(kernel, [goal]);

        now += TimeSpan.FromMinutes(16);
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Equal(2, runner.Calls.Count);
        Xunit.Assert.Equal(2, events.Summaries.Last().TotalGlances);
        Xunit.Assert.Equal(25, events.Summaries.Last().TotalTokens);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_suppresses_rounds_with_small_historical_estimate")]
    public void SuppressesRoundsWithSmallHistoricalEstimate()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, _) = RunningDeveloperRound(now, description: "Implement a tiny label change.");
        var runner = new ControlledGlanceRunner();
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now + TimeSpan.FromMinutes(20),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));
        var stats = new[]
        {
            new TaskDurationStatsRecord(
                AgentRole.Developer,
                TaskComplexity.Simple,
                null,
                null,
                TaskCount: 3,
                AttemptCount: 3,
                FailedAttemptCount: 0,
                MedianLegitimateRuntime: TimeSpan.FromMinutes(5),
                P90LegitimateRuntime: TimeSpan.FromMinutes(6),
                MedianFailureInterventionOverhead: TimeSpan.Zero,
                FailureRate: 0)
        };

        _ = coordinator.Observe(kernel, [goal], stats);

        Xunit.Assert.Empty(runner.Calls);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_dispatch_uses_read_only_profile_selection_and_bounded_inputs")]
    public void DispatchUsesReadOnlyProfileSelectionAndBoundedInputs()
    {
        var sparkSelection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(WorkerProfileCatalog.Default());
        Xunit.Assert.Equal("codex-spark", sparkSelection.ProfileName);
        Xunit.Assert.Equal("gpt-5.3-codex-spark", sparkSelection.ModelAlias);

        var fallbackSelection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(
            new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired("codex-cli")]));
        Xunit.Assert.Equal("codex-cli", fallbackSelection.ProfileName);
        Xunit.Assert.Equal("gpt-5.5", fallbackSelection.ModelAlias);

        var command = SubscriptionCliCompleter.SubstitutePlaceholders(
            WorkerProfileCatalog.Default().GetRequired("codex-spark").CommandTemplate,
            "codex-spark",
            "prompt.md",
            "gpt-5.3-codex-spark",
            "low",
            @"C:\work");
        Xunit.Assert.Contains("--sandbox 'read-only'", command, StringComparison.Ordinal);
        Xunit.Assert.Contains("--model 'gpt-5.3-codex-spark'", command, StringComparison.Ordinal);

        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now, description: "Do work\n\nACCEPTANCE\n- Include correction overlay");
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["Correct criterion B before retry."]);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "bounded", 1, 1));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now,
            options: new ProgressiveReviewGlanceOptions(DiffCharacterLimit: 30, TranscriptCharacterLimit: 20),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
            diffReader: (_, _) => new string('d', 100),
            transcriptReader: _ => new string('t', 100));

        _ = coordinator.Observe(kernel, [goal]);

        var inputs = runner.Calls.Single();
        Xunit.Assert.Contains("ACCEPTANCE", inputs.AcceptanceSection, StringComparison.Ordinal);
        Xunit.Assert.Contains("Correct criterion B", inputs.CriteriaCorrectionOverlay.Single(), StringComparison.Ordinal);
        Xunit.Assert.True(inputs.DiffExcerpt.Length < 90);
        Xunit.Assert.True(inputs.TranscriptTail.Length < 90);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_concern_surfaces_only_after_same_round_failure")]
    public void ConcernSurfacesOnlyAfterSameRoundFailure()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.Concern,
            "Acceptance criterion B appears untouched.",
            "diff lacks criterion B",
            5,
            5));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Empty(task.CriterionRetryFeedback);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("verify", "C:\\work", 1, "failed", "", now.AddMinutes(1)));
        var result = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.True(result.MutatedTaskState);
        Xunit.Assert.Contains(task.CriterionRetryFeedback, item => item.Contains("Progressive review glance concern", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_misdirection_raises_one_deduped_attention_item")]
    public void MisdirectionRaisesOneDedupedAttentionItem()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, _) = RunningDeveloperRound(now);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Task asks for forbidden session resume work.",
            "diff adds session resume primitive",
            5,
            5));
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Task still asks for forbidden session resume work.",
            "diff adds second session resume primitive",
            5,
            5));
        var coordinator = new ProgressiveReviewGlanceCoordinator(
            runner,
            new RecordingGlanceEvents(),
            store,
            new ProgressiveReviewGlanceOptions(FirstElapsedThreshold: TimeSpan.Zero),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
            (_, _) => "diff",
            _ => "transcript");

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);

        var items = store.ListAsync(goal.Id.Value).GetAwaiter().GetResult();
        Xunit.Assert.Single(items);
        Xunit.Assert.Equal(CollaborationItemType.Decision, items.Single().Type);
        Xunit.Assert.Contains("cancel plus resume-with-guidance", items.Single().Body, StringComparison.Ordinal);
    }

    private static ProgressiveReviewGlanceCoordinator NewCoordinator(
        ControlledGlanceRunner runner,
        RecordingGlanceEvents events,
        ProgressiveReviewGlanceOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string?, string?, DispatchLiveChangeSnapshot>? liveChanges = null,
        Func<string, string?, string>? diffReader = null,
        Func<TaskProcessRecord?, string>? transcriptReader = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        return new ProgressiveReviewGlanceCoordinator(
            runner,
            events,
            new CollaborationItemStore(Path.Combine(root, "items.db")),
            options,
            utcNow,
            liveChanges,
            diffReader ?? ((_, _) => "diff"),
            transcriptReader ?? (_ => "transcript"));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningDeveloperRound(
        DateTimeOffset dispatchedAt,
        string description = "Implement feature.\n\nACCEPTANCE\n- Pass focused tests")
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(new TaskId("developer-task-0001"), description, AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("goal-progressive-review-0001"), "Progressive review goal objective", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                @"C:\work",
                dispatchedAt,
                BaseCommit: "base"));
        return (kernel, goal, task);
    }

    private sealed class ControlledGlanceRunner : IProgressiveReviewGlanceRunner
    {
        private readonly Queue<Task<ProgressiveReviewGlanceDispatchResult>> _responses = [];

        public List<ProgressiveReviewGlanceInputs> Calls { get; } = [];

        public TaskCompletionSource<ProgressiveReviewGlanceDispatchResult> EnqueuePending()
        {
            var source = new TaskCompletionSource<ProgressiveReviewGlanceDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Enqueue(source.Task);
            return source;
        }

        public void EnqueueCompleted(ProgressiveReviewGlanceDispatchResult result) =>
            _responses.Enqueue(Task.FromResult(result));

        public Task<ProgressiveReviewGlanceDispatchResult> RunAsync(
            ProgressiveReviewGlanceInputs inputs,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(inputs);
            return _responses.Count == 0
                ? Task.FromResult(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "ok", 1, 1))
                : _responses.Dequeue();
        }
    }

    private sealed class RecordingGlanceEvents : IGoalLifecycleEventWriter
    {
        public List<Receipt> Receipts { get; } = [];
        public List<Summary> Summaries { get; } = [];

        public void AppendTimelineEvent(ProgressEvent progressEvent) { }
        public void AppendGoalCreated(GoalId goalId, string objective) { }
        public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
        public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
        public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
        public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
        public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
        public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
        public void AppendCleanedUp(GoalId goalId) { }

        public void AppendProgressiveReviewGlanceReceipt(
            GoalId goalId,
            TaskId taskId,
            string trigger,
            string inputsHash,
            string verdict,
            string note,
            int inputTokens,
            int outputTokens,
            int totalTokens,
            TimeSpan wallTime,
            string? model,
            string? profile) =>
            Receipts.Add(new Receipt(trigger, inputsHash, verdict, note, inputTokens, outputTokens, totalTokens, wallTime, model, profile));

        public void AppendProgressiveReviewGlanceSummary(
            GoalId goalId,
            int totalGlances,
            int onTrack,
            int concern,
            int fundamentalMisdirection,
            int invalid,
            int totalTokens) =>
            Summaries.Add(new Summary(totalGlances, onTrack, concern, fundamentalMisdirection, invalid, totalTokens));
    }

    private sealed record Receipt(
        string Trigger,
        string InputsHash,
        string Verdict,
        string Note,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        TimeSpan WallTime,
        string? Model,
        string? Profile);

    private sealed record Summary(
        int TotalGlances,
        int OnTrack,
        int Concern,
        int FundamentalMisdirection,
        int Invalid,
        int TotalTokens);
}
