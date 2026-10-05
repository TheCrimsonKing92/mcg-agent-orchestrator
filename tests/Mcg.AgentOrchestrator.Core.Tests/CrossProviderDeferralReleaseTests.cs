using Mcg.AgentOrchestrator.Core;

public sealed class CrossProviderDeferralReleaseTests
{
    private const string ReleaseMarker = "Released subscription deferral";

    [Xunit.Fact]
    public void ReassignTaskAgent_DifferentProvider_ReleasesDeferral()
    {
        var (kernel, goal, task, clock, _) = DeferredTester();

        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));

        AssertReleased(goal, task, clock);
    }

    [Xunit.Fact]
    public void RedelegateTask_DifferentProvider_ReleasesDeferral()
    {
        var (kernel, goal, task, clock, _) = DeferredTester();

        kernel.RedelegateTask(goal.Id, task.Id, [Tester("Anthropic")]);

        AssertReleased(goal, task, clock);
    }

    [Xunit.Theory]
    [Xunit.InlineData("OpenAI")]
    [Xunit.InlineData("openai")]
    public void ReassignTaskAgent_SameProvider_KeepsDeferral(string provider)
    {
        var (kernel, goal, task, clock, _) = DeferredTester();
        var retryAfter = task.SubscriptionRetryAfter;

        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester(provider));

        Assert.Equal(retryAfter, task.SubscriptionRetryAfter);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        Assert.Null(task.SubscriptionDeferralReleasedAt);
        AssertNoReleaseNote(goal);
    }

    [Xunit.Fact]
    public void ReassignTaskAgent_UnchangedAssignment_KeepsDeferral()
    {
        var (kernel, goal, task, clock, agent) = DeferredTester();
        var retryAfter = task.SubscriptionRetryAfter;

        // Even different provider metadata cannot release without an agent-id change.
        kernel.ReassignTaskAgent(goal.Id, task.Id,
            agent with { Model = agent.Model with { ProviderName = "Anthropic" } });

        Assert.Equal(retryAfter, task.SubscriptionRetryAfter);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        Assert.Null(task.SubscriptionDeferralReleasedAt);
        AssertNoReleaseNote(goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    public void ReassignTaskAgent_UnknownSourceProvider_KeepsDeferral(string? provider)
    {
        var (kernel, goal, task, clock, _) = DeferredTester(provider);
        var retryAfter = task.SubscriptionRetryAfter;

        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));

        Assert.Equal(retryAfter, task.SubscriptionRetryAfter);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        Assert.Null(task.SubscriptionDeferralReleasedAt);
        AssertNoReleaseNote(goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ReassignTaskAgent_DerivedDeferral_UsesMatchingDispatch(bool exactMatch)
    {
        var (kernel, goal, task, clock, _) = DeferredTester();
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var taskSnapshot = snapshot.Tasks.Single();
        var newerDispatch = taskSnapshot.LastDispatch! with
        {
            ProviderName = "Anthropic",
            DispatchedAt = clock.UtcNow.AddSeconds(1)
        };
        var verification = taskSnapshot.VerificationHistory!.Single() with
        {
            DispatchStartedAt = exactMatch ? clock.UtcNow : null
        };
        snapshot = snapshot with
        {
            Tasks = [taskSnapshot with
            {
                SubscriptionRetryAfter = null,
                LastDispatch = newerDispatch,
                DispatchHistory = [taskSnapshot.LastDispatch!, newerDispatch],
                VerificationHistory = [verification]
            }]
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []), clock);
        goal = kernel.GetGoal(goal.Id);
        task = kernel.GetTask(goal.Id, task.Id);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));

        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));

        AssertReleased(goal, task, clock);
    }

    [Xunit.Fact]
    public void ReassignTaskAgent_NoDeferral_DoesNotRecordRelease()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("No subscription deferral", [new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [Tester("OpenAI")]);
        var task = goal.Tasks.Single();

        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));

        Assert.Null(task.SubscriptionDeferralReleasedAt);
        AssertNoReleaseNote(goal);
    }

    [Xunit.Fact]
    public void RecordDispatchExecutionResult_AfterRelease_DefersFreshLimit()
    {
        var (kernel, goal, task, clock, _) = DeferredTester();
        kernel.ReassignTaskAgent(goal.Id, task.Id, Tester("Anthropic"));
        AssertReleased(goal, task, clock);
        var boundary = task.SubscriptionDeferralReleasedAt;
        clock.Advance();
        RecordLimit(kernel, goal, task, clock, "Anthropic");

        Assert.Equal(boundary, task.SubscriptionDeferralReleasedAt);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        // A post-boundary verification must also defer when the stored value is absent.
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        snapshot = snapshot with { Tasks = [snapshot.Tasks.Single() with { SubscriptionRetryAfter = null }] };
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []), clock);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(
            restored.GetTask(goal.Id, task.Id), clock.UtcNow, out _));
    }

    private static (AgentOrchestratorKernel, Goal, TaskSpec, FakeClock, AgentDefinition) DeferredTester(
        string? dispatchProvider = "OpenAI")
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Release subscription deferral", [new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
        var agent = Tester("OpenAI");
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        RecordLimit(kernel, goal, task, clock, dispatchProvider);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 16, 58, 0, TimeSpan.Zero), task.SubscriptionRetryAfter);
        Assert.True(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        return (kernel, goal, task, clock, agent);
    }

    private static void RecordLimit(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        FakeClock clock, string? provider)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", clock.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli, ProviderName: provider));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec", "C:\\repo", 1, string.Empty,
            "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
            clock.UtcNow, DispatchStartedAt: clock.UtcNow));
    }

    private static AgentDefinition Tester(string provider) => new(
        AgentId.New(), "Tester", AgentRole.Tester,
        new ModelProfile(provider, "test-model", ModelCapability.Text, SubscriptionMode.ApiKey));

    private static void AssertReleased(Goal goal, TaskSpec task, FakeClock clock)
    {
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.False(DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, clock.UtcNow, out _));
        var note = Assert.Single(goal.Timeline.Where(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote && evt.Message.Contains(ReleaseMarker, StringComparison.Ordinal)));
        Assert.Contains("OpenAI", note.Message, StringComparison.Ordinal);
        Assert.Contains("2026-06-01T16:58:00.0000000+00:00", note.Message, StringComparison.Ordinal);
    }

    private static void AssertNoReleaseNote(Goal goal) => Assert.DoesNotContain(goal.Timeline,
        evt => evt.Message.Contains(ReleaseMarker, StringComparison.Ordinal));
}
