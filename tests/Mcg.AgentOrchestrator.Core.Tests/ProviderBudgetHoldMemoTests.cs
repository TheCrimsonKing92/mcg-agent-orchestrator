using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fixture owns its kernel and injected clock; no external resources.
public sealed class ProviderBudgetHoldMemoTests
{
    [Fact]
    public void TryGet_RepeatedBindings_EvaluatesEachKeyOnceAndMatchesDirectResults()
    {
        var fixture = new Fixture();
        for (var i = 0; i < 40; i++)
        {
            var provider = i % 3 == 0 ? (i % 2 == 0 ? "xAI" : " XAI ")
                : (i % 2 == 0 ? "Anthropic" : " anthropic ");
            var credential = i % 3 == 0 ? null
                : (i % 2 == 0 ? @"C:\profiles\" : @" C:\PROFILES\") + (i % 3 == 1 ? "a" : "b") + " ";
            fixture.Add(provider, credential, exhausted: i == 0);
        }

        var calls = 0;
        bool Evaluate(IEnumerable<Goal> goals, string provider, string? credential,
            out ProviderBudgetExhaustionHold hold)
        {
            Assert.Same(fixture.Kernel.Goals, goals);
            calls++;
            return ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(goals, provider, credential, out hold);
        }
        var memo = new ProviderBudgetHoldMemo(fixture.Kernel.Goals, Evaluate);
        ProviderBudgetExhaustionHold? first = null;
        var held = 0;
        var dispatches = fixture.Kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory).ToArray();
        Assert.Equal(40, dispatches.Length);
        foreach (var dispatch in dispatches)
        {
            var provider = dispatch.ProviderName ?? dispatch.WorkerName;
            var expected = ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(fixture.Kernel.Goals,
                provider, dispatch.ClaudeCredentialSourceDirectory, out var expectedHold);
            Assert.Equal(expected, memo.TryGet(provider, dispatch.ClaudeCredentialSourceDirectory, out var actualHold));
            Assert.Equal(expectedHold, actualHold); // Record equality covers every hold field.
            if (!expected) continue;
            held++;
            first ??= actualHold;
            Assert.Same(first, actualHold);
        }
        Assert.Equal(14, held);
        Assert.Equal(3, calls);
        Assert.Equal(3, memo.EvaluationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByBinding_RepeatedDispatches_EvaluatesOnceAndMatchesBaseline(bool held)
    {
        var fixture = new Fixture();
        for (var i = 0; i < 25; i++) fixture.Add("xAI", null, exhausted: held && i == 0);
        Assert.Equal(25, fixture.Kernel.Goals.SelectMany(goal => goal.Tasks).Sum(task => task.DispatchHistory.Count));
        var calls = 0;
        bool Evaluate(IEnumerable<Goal> goals, string provider, string? credential,
            out ProviderBudgetExhaustionHold hold)
        {
            calls++;
            return ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(goals, provider, credential, out hold);
        }
        const string binding = "XAI::<PROVIDER-DEFAULT>";
        var memo = new ProviderBudgetHoldMemo(fixture.Kernel.Goals, Evaluate);
        var actual = ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(fixture.Kernel.Goals, binding, out var actualHold, memo);
        var publicResult = ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(fixture.Kernel.Goals, binding, out var publicHold);
        var baseline = ReferenceLookup(fixture.Kernel.Goals, binding, out var baselineHold);
        Assert.Equal(held, actual);
        Assert.Equal(baseline, publicResult);
        Assert.Equal(publicResult, actual);
        Assert.Equal(baselineHold, publicHold);
        Assert.Equal(publicHold, actualHold);
        Assert.Equal(1, calls);
        Assert.Equal(1, memo.EvaluationCount);
    }

    [Fact]
    public void TryGet_IdenticalKeyTextWithDifferentScope_EvaluatesBothBindings()
    {
        var fixture = new Fixture();
        fixture.Add("xAI", null, exhausted: true);
        var memo = new ProviderBudgetHoldMemo(fixture.Kernel.Goals);
        Assert.True(memo.TryGet("xAI", null, out _));
        Assert.False(memo.TryGet("xAI", "<provider-default>", out _));
        Assert.True(memo.TryGet(" XAI ", " ", out _));
        Assert.False(memo.TryGet("xai", " <PROVIDER-DEFAULT> ", out _));
        Assert.Equal(2, memo.EvaluationCount);
    }

    [Fact]
    public void TryGet_EvaluatorThrows_CountsAttemptWithoutCachingException()
    {
        var fixture = new Fixture();
        var calls = 0;
        var failure = new InvalidOperationException("evaluation failed");
        bool Evaluate(IEnumerable<Goal> goals, string provider, string? credential,
            out ProviderBudgetExhaustionHold hold)
        {
            Assert.Same(fixture.Kernel.Goals, goals);
            Assert.Equal(" xAI ", provider);
            Assert.Equal(" profile ", credential);
            if (++calls == 1) throw failure;
            hold = null!;
            return false;
        }
        var memo = new ProviderBudgetHoldMemo(fixture.Kernel.Goals, Evaluate);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => memo.TryGet(" xAI ", " profile ", out _)));
        Assert.Equal(1, memo.EvaluationCount);
        Assert.False(memo.TryGet(" xAI ", " profile ", out _));
        Assert.False(memo.TryGet("XAI", "PROFILE", out _));
        Assert.Equal(2, calls);
        Assert.Equal(2, memo.EvaluationCount);
    }

    private static bool ReferenceLookup(IEnumerable<Goal> goals, string bindingKey, out ProviderBudgetExhaustionHold hold)
    {
        var all = goals.ToArray();
        foreach (var dispatch in all.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory))
            if (DispatchFailureClassifier.BuildProviderBindingKey(dispatch.ProviderName ?? dispatch.WorkerName,
                    dispatch.ClaudeCredentialSourceDirectory).Key.Equals(bindingKey, StringComparison.OrdinalIgnoreCase)
                && ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(all,
                    dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeCredentialSourceDirectory, out hold))
                return true;
        hold = null!;
        return false;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    }

    private sealed class Fixture
    {
        private readonly Clock _clock = new();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Fixture() => Kernel = new(_clock);

        internal void Add(string provider, string? credential, bool exhausted)
        {
            var goal = Kernel.CreateGoal("Memo fixture", [new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester)]);
            AgentDefinition agent = new(new("tester"), "tester", AgentRole.Tester,
                new("xAI", "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey));
            Kernel.ActivateGoal(goal.Id, [agent]);
            var at = _clock.UtcNow;
            var task = goal.Tasks.Single();
            Kernel.RecordTaskDispatch(goal.Id, task.Id, new("fixture", "fixture", @"C:\fixture", at,
                ProviderName: provider, ClaudeCredentialSourceDirectory: credential));
            _clock.UtcNow = at.AddMinutes(1);
            if (exhausted)
                Kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", @"C:\fixture", 1, "", "budget exhausted",
                    _clock.UtcNow, ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: at));
        }
    }
}
