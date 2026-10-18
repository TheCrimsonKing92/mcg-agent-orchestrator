using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory histories with a private clock and no external resources.
public sealed class ProviderUsageHoldViewTests
{
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-17T11:07:00Z");

    [Fact]
    public void NoExhaustion_ReturnsNoRows()
    {
        var fixture = new Fixture();
        fixture.NewGoal();
        Assert.Empty(fixture.Read());
    }

    [Fact]
    public void SameBindingExhaustedTwice_ReturnsOneLatestHold()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        var latest = fixture.NewGoal();
        fixture.Exhaust(latest, Start.AddMinutes(2));
        var row = Assert.Single(fixture.Read());
        Assert.Equal("xai::<provider-default>", row.Hold.BindingKey);
        Assert.Equal(latest.Id, row.Hold.SourceGoalId);
        Assert.Equal(2, row.Schedule!.Attempt);
    }

    [Fact]
    public void SeparateClaudeLogins_ReturnDistinctRowsInOrdinalIgnoreCaseOrder()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start, "Anthropic", @"C:\User Profiles\Z");
        fixture.Exhaust(fixture.NewGoal(), Start.AddMinutes(2), "Anthropic", @"C:\User Profiles\A");
        var rows = fixture.Read();
        Assert.Equal(new[] { @"anthropic::c:\user profiles\a", @"anthropic::c:\user profiles\z" },
            rows.Select(row => row.Hold.BindingKey));
        Assert.All(rows, row => Assert.NotNull(row.Schedule));
    }

    [Fact]
    public void LaterSuccessfulDispatch_ClearsTheBindingRow()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        Assert.Single(fixture.Read());
        var success = fixture.NewGoal();
        fixture.Dispatch(success, Start.AddMinutes(2));
        fixture.Kernel.RecordTaskVerification(success.Id, success.Tasks.Single().Id,
            Verification(Start.AddMinutes(3)) with { ExitCode = 0, StandardError = "", ProviderFailureKind = ProviderFailureKind.Unknown });
        Assert.Empty(fixture.Read());
    }

    [Fact]
    public void UnpairedScheduleHistory_RendersUnavailableInsteadOfThrowing()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        var orphan = new Fixture();
        var goal = orphan.NewGoal();
        orphan.Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id, Verification(Start.AddMinutes(1)));
        // Consistent public history pairs the same way for hold and schedule. Split scopes exercise
        // the defensive projection catch using a real unpaired exhaustion, without a fake evaluator.
        var hold = Assert.Single(fixture.Read()).Hold;
        Assert.Throws<InvalidOperationException>(() => ProviderBudgetHoldSchedules.Evaluate(orphan.Kernel.Goals, hold, Start));
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.Goals, orphan.Kernel.Goals, Start));
        Assert.Null(row.Schedule);
        Assert.Equal($"  xai::<provider-default> | schedule=unavailable | source={hold.SourceGoalId.Value[..8]}/{hold.SourceTaskId.Value[..8]}",
            ProviderUsageHoldView.FormatStatusLine(row));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("parsed")]
    [InlineData("backoff")]
    public void ScheduleSource_FormatsExactStatusTextWithUtcInstant(string source)
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        var row = Assert.Single(fixture.Read());
        var schedule = row.Schedule! with
        {
            Attempt = 2, ResetSource = source, LastProbeOutcome = "exhausted",
            NextProbeAt = DateTimeOffset.Parse("2026-10-20T21:03:00-05:00")
        };
        Assert.Equal($"  xai::<provider-default> | attempt=2 | next_probe=2026-10-21T02:03:00.0000000+00:00 | reset_source={source} | last_probe=exhausted | source={row.Hold.SourceGoalId.Value[..8]}/{row.Hold.SourceTaskId.Value[..8]}",
            ProviderUsageHoldView.FormatStatusLine(row with { Schedule = schedule }));
    }

    private static TaskVerificationRecord Verification(DateTimeOffset at) =>
        new("fixture", @"C:\fixture", 1, "", Exhausted, at, ProviderFailureKind: ProviderFailureKind.BudgetExhausted);

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Start; }
    private sealed class Fixture
    {
        internal readonly Clock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal Fixture() => Kernel = new(Clock);
        internal Goal NewGoal()
        {
            var goal = Kernel.CreateGoal("Hold view fixture", [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, [new AgentDefinition(new("primary"), "primary", AgentRole.Tester,
                new("xAI", "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey))]);
            return goal;
        }
        internal void Dispatch(Goal goal, DateTimeOffset at, string provider = "xAI", string? credential = null) =>
            Kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id, new("grok-cli", "fixture", @"C:\fixture", at,
                ProviderName: provider, ClaudeCredentialSourceDirectory: credential));
        internal void Exhaust(Goal goal, DateTimeOffset at, string provider = "xAI", string? credential = null)
        {
            Dispatch(goal, at, provider, credential);
            Clock.UtcNow = at.AddMinutes(1);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id, Verification(Clock.UtcNow) with { DispatchStartedAt = at });
        }
        internal IReadOnlyList<ProviderUsageHoldRow> Read() => ProviderUsageHoldView.Read(Kernel.Goals, Clock.UtcNow);
    }
}
