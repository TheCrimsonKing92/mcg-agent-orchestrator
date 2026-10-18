using Mcg.AgentOrchestrator.Core;

// Parallel-safe: private kernels and fixed clocks, with no external resources.
public sealed class ProviderHoldHistoryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-17T11:07:00Z");

    [Fact]
    public void Retain_OnlyExhaustion_KeepsSourceAndCopiesIndependently()
    {
        var source = Observed("a", false);
        var history = new ProviderHoldHistory();
        history.Retain(source, []);
        Assert.Equal(source.Id, Assert.Single(history.Retained).Id);
        Assert.True(Held(history.Scope([])));
        var copy = new ProviderHoldHistory();
        copy.CopyFrom(history);
        history.Retain([], [Observed("z", true, minutes: 20)]);
        Assert.Empty(history.Retained);
        Assert.Equal(source.Id, Assert.Single(copy.Retained).Id);
        Assert.True(Held(copy.Scope([])));
    }

    [Fact]
    public void Retain_LaterSameBindingSuccess_DropsExhaustion()
    {
        var history = new ProviderHoldHistory();
        history.Retain(Observed("a", false), [Observed("z", true, minutes: 20)]);
        Assert.Empty(history.Retained);
    }

    [Fact]
    public void Retain_ClaudeLoginDirectories_KeepsOnlyUnclearedBinding()
    {
        var first = Observed("a", false, "Anthropic", @"C:\login-a");
        var second = Observed("b", false, "Anthropic", @"C:\login-b");
        var history = new ProviderHoldHistory();
        history.Retain([first, second], [Observed("z", true, "Anthropic", @"C:\login-a", 20)]);
        Assert.Equal(second.Id, Assert.Single(history.Retained).Id);
        Assert.False(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(history.Scope([]), "Anthropic", @"C:\login-a", out _));
        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(history.Scope([]), "Anthropic", @"C:\login-b", out _));
    }

    [Fact]
    public void Scope_ReingestedId_UsesWorkingCopyExactlyOnce()
    {
        var source = Observed("a", false);
        var history = new ProviderHoldHistory();
        history.Retain(source, []);
        var replacement = Observed("a", true, minutes: 20);
        var scope = history.Scope([replacement]);
        Assert.Same(replacement, Assert.Single(scope));
        Assert.False(Held(scope));
        history.Retain([], [replacement]);
        Assert.Empty(history.Retained);
        IReadOnlyCollection<Goal> working = [replacement];
        Assert.Same(working, history.Scope(working));
    }

    [Fact]
    public void Retain_BatchOrder_PrunesToSameFixpoint()
    {
        var source = Observed("a", false);
        var clearing = Observed("z", true, minutes: 20);
        var forward = new ProviderHoldHistory();
        var reverse = new ProviderHoldHistory();
        forward.Retain([source, clearing], []);
        reverse.Retain([clearing, source], []);
        Assert.Empty(forward.Retained);
        Assert.Equal(forward.Retained, reverse.Retained);
        forward.Retain([source, Observed("b", false, "Anthropic", @"C:\login")], []);
        reverse.Retain([Observed("b", false, "Anthropic", @"C:\login"), source], []);
        Assert.Equal(new[] { "a", "b" }, forward.Retained.Select(goal => goal.Id.Value));
        Assert.Equal(forward.Retained.Select(goal => goal.Id), reverse.Retained.Select(goal => goal.Id));
    }

    [Fact]
    public void SeedCandidates_IndexedHistory_SelectsNewestLaterProviderSuccess()
    {
        var rows = new[]
        {
            Row("b", "xAI", 1, exhausted: true),
            Row("a", "XAI", 10, exhausted: true),
            Row("older", "xAI", 9), Row("later", "xAI", 11), Row("z", "xai", 12),
            Row("unrelated", "OpenAI", 15),
            Row("working", "Anthropic", 3, exhausted: true),
            Row("c", "Anthropic", 2)
        };
        Assert.Equal(new[] { "a", "b", "z" }, ProviderHoldHistory.SeedCandidates(rows,
            new HashSet<string>(["working"], StringComparer.Ordinal)));
        Assert.Equal(new[] { "a", "b", "working" }, ProviderHoldHistory.SeedCandidates(rows,
            new HashSet<string>(["z"], StringComparer.Ordinal)));
        Assert.Empty(ProviderHoldHistory.SeedCandidates([Row("success", "xAI", 20)], new HashSet<string>()));
    }

    [Fact]
    public void RefreshTrackedGoals_CancelledExhaustion_RetainsPersistedSource()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var source = Observed("a", false, kernel: kernel);
        var snapshot = kernel.ExportSnapshot();
        kernel.RefreshTrackedGoals(snapshot with { Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Cancelled }).ToArray() });
        Assert.Empty(kernel.Goals);
        Assert.Equal(GoalStatus.Cancelled, Assert.Single(kernel.ProviderHoldHistory.Retained).Status);
        Assert.True(DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(kernel.ProviderHoldScope, "xAI", null, out var hold));
        Assert.Equal(source.Id, hold.SourceGoalId);
        Assert.Empty(kernel.ExportSnapshot().Goals);
    }

    private static bool Held(IReadOnlyCollection<Goal> scope) =>
        DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(scope, "xAI", null, out _);

    private static ModelFitHistoryRow Row(string id, string provider, int minute, bool exhausted = false) =>
        new(id, "task", AgentRole.Tester, provider, "fixture", null, null,
            exhausted ? WorkTaskStatus.Failed : WorkTaskStatus.Completed, "adequate", Start.AddMinutes(minute),
            exhausted ? "provider-budget-exhausted" : "succeeded-dispatch-completion-evidence",
            exhausted ? TaskOutcomeClass.Environmental : TaskOutcomeClass.Success);

    private static Goal Observed(string id, bool success, string provider = "xAI", string? credential = null,
        int minutes = 0, AgentOrchestratorKernel? kernel = null)
    {
        kernel ??= new AgentOrchestratorKernel(new FixedClock());
        var goal = kernel.CreateGoal(new GoalId(id), "Hold history fixture",
            [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [new(new("tester"), "Tester", AgentRole.Tester,
            new(provider, "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey))]);
        var at = Start.AddMinutes(minutes);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new("fixture", "fixture", @"C:\fixture", at,
            ProviderName: provider, ClaudeCredentialSourceDirectory: credential));
        kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", @"C:\fixture", success ? 0 : 1,
            "", success ? "" : "Provider budget exhausted.", at.AddMinutes(1),
            ProviderFailureKind: success ? ProviderFailureKind.Unknown : ProviderFailureKind.BudgetExhausted,
            DispatchStartedAt: at));
        return goal;
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Start; }
}
