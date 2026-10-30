using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all histories and clocks belong to the test; no processes or shared state.
public sealed class ProviderBudgetHoldScheduleTests
{
    private const string Exhausted = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-17T11:07:00Z");

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 360)]
    [InlineData(6, 360)]
    public void ExhaustionsDoubleBackoffToSixHourCap(int attempts, int minutes)
    {
        var fixture = new Fixture();
        for (var i = 0; i < attempts; i++)
            fixture.Exhaust(fixture.NewGoal(), Start.AddMinutes(i * 2));
        var schedule = fixture.Schedule();
        Assert.Equal(attempts, schedule.Attempt);
        Assert.Equal(schedule.Hold.PlacedAt.AddMinutes(minutes), schedule.NextProbeAt);
        Assert.Equal("backoff", schedule.ResetSource);
        Assert.Null(schedule.ResetAt);
        Assert.Equal("none", schedule.LastProbeOutcome);
    }

    [Theory]
    [InlineData("; resets in 3h 20m", "2026-10-17T14:28:00Z")]
    [InlineData("; try again in 45 minutes", "2026-10-17T11:53:00Z")]
    [InlineData("; resets Oct 20, 9:03pm (America/Chicago)", "2026-10-21T02:03:00Z")]
    [InlineData("; resets at 2026-10-21T02:03:00Z", "2026-10-21T02:03:00Z")]
    [InlineData("; resets on 2026-10-20T21:03:00-05:00", "2026-10-21T02:03:00Z")]
    public void ParsesOnlyResetOnTheExhaustionEvidenceLine(string suffix, string expected)
    {
        var verification = Verification(Exhausted + suffix, Start.AddMinutes(1));
        Assert.True(ProviderBudgetHoldSchedules.TryGetResetAt(verification, out var reset));
        Assert.Equal(DateTimeOffset.Parse(expected), reset);
        Assert.Equal(TimeSpan.Zero, reset.Offset);
    }

    [Theory]
    [InlineData(Exhausted)]
    [InlineData("{\"error\":{\"message\":\"Grok Build usage balance exhausted\",\"http_status\":402}}")]
    [InlineData(Exhausted + "; try again at 9:03 PM")]
    [InlineData(Exhausted + "; resets Oct 20, 9:03pm (Unknown/Zone)")]
    [InlineData(Exhausted + "; resets at 2026-10-17T11:07:00Z")]
    [InlineData(Exhausted + "; resets Oct 16, 9:03pm (America/Chicago)")]
    [InlineData(Exhausted + "; resets at 2026-10-21T02:03:00")]
    [InlineData(Exhausted + "\nunrelated text: resets in 3h 20m")]
    public void UnknownOrPastResetDoesNotBecomeAKnownReset(string stderr)
    {
        Assert.False(ProviderBudgetHoldSchedules.TryGetResetAt(Verification(stderr, Start.AddMinutes(1)), out _));
    }

    [Fact]
    public void CalendarYearUsesNearestInstantAcrossNewYear()
    {
        Assert.True(ProviderBudgetHoldSchedules.TryGetResetAt(Verification(
            Exhausted + "; resets Jan 1, 12am (America/Chicago)", DateTimeOffset.Parse("2026-12-31T23:00:00Z")), out var reset));
        Assert.Equal(DateTimeOffset.Parse("2027-01-01T06:00:00Z"), reset);
    }

    [Fact]
    public void OperatorMarkerAfterLatestExhaustionOverridesParsedResetAndEarlierMarkerIsIgnored()
    {
        var fixture = new Fixture();
        var goal = fixture.NewGoal();
        fixture.Clock.UtcNow = Start;
        fixture.Kernel.RecordTaskNote(goal.Id, goal.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatReset(
            new("xai::<provider-default>", Start.AddDays(2), "before")));
        fixture.Exhaust(goal, Start, Exhausted + "; resets in 3h 20m");
        Assert.Equal("parsed", fixture.Schedule().ResetSource);
        fixture.Clock.UtcNow = Start.AddMinutes(2);
        fixture.Kernel.RecordTaskNote(goal.Id, goal.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatReset(
            new("xai::<provider-default>", Start.AddDays(3), "after")));
        var schedule = fixture.Schedule();
        Assert.Equal("operator", schedule.ResetSource);
        Assert.Equal(Start.AddDays(3), schedule.ResetAt);
        Assert.Equal(schedule.ResetAt, schedule.NextProbeAt);
        fixture.Exhaust(fixture.NewGoal(), Start.AddMinutes(3));
        Assert.Equal("backoff", fixture.Schedule().ResetSource);
    }

    [Fact]
    public void ReservationBecomesInFlightThenInconclusiveAndBacksOffFromCompletion()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        var probe = fixture.NewGoal();
        var due = fixture.Schedule().NextProbeAt;
        fixture.Clock.UtcNow = due;
        fixture.Kernel.RecordTaskNote(probe.Id, probe.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatProbe(
            new("xai::<provider-default>", due, 1, "backoff")));
        var reserved = fixture.Schedule();
        Assert.Equal(probe.Id, reserved.AdmittedGoalId);
        Assert.Equal(probe.Tasks.Single().Id, reserved.AdmittedTaskId);
        Assert.False(reserved.ProbeInFlight);
        fixture.Dispatch(probe, due);
        Assert.True(fixture.Schedule().ProbeInFlight);
        Assert.Equal("running", fixture.Schedule().LastProbeOutcome);
        Assert.Null(fixture.Schedule().AdmittedTaskId);
        fixture.Kernel.RecordTaskVerification(probe.Id, probe.Tasks.Single().Id,
            Verification("Task failed independently of the provider", due.AddMinutes(1)) with { DispatchStartedAt = due });
        var completed = fixture.Schedule();
        Assert.False(completed.ProbeInFlight);
        Assert.Equal("inconclusive", completed.LastProbeOutcome);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal(due.AddMinutes(61), completed.NextProbeAt);
    }

    [Fact]
    public void ExhaustedProbeCountsOnceAndStartsTheNextBackoff()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        var probe = fixture.NewGoal();
        var due = fixture.Schedule().NextProbeAt;
        fixture.Clock.UtcNow = due;
        fixture.Kernel.RecordTaskNote(probe.Id, probe.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatProbe(
            new("xai::<provider-default>", due, 1, "backoff")));
        fixture.Exhaust(probe, due);
        var schedule = fixture.Schedule();
        Assert.Equal(2, schedule.Attempt);
        Assert.Equal(due.AddMinutes(61), schedule.NextProbeAt);
        Assert.Equal("exhausted", schedule.LastProbeOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedReservationsDoNotPreventAnotherAdmission(bool cancelGoal)
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start);
        var probe = fixture.NewGoal();
        var due = fixture.Schedule().NextProbeAt;
        fixture.Clock.UtcNow = due;
        fixture.Kernel.RecordTaskNote(probe.Id, probe.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatProbe(
            new("xai::<provider-default>", due, 1, "backoff")));
        Assert.NotNull(fixture.Schedule().AdmittedTaskId);
        if (cancelGoal) fixture.Kernel.CancelGoal(probe.Id, "Abandon reservation.");
        else fixture.Kernel.ReassignTaskAgent(probe.Id, probe.Tasks.Single().Id, Fixture.Agent("alternate", "OpenAI"));
        Assert.Null(fixture.Schedule().AdmittedTaskId);
        Assert.False(fixture.Schedule().ProbeInFlight);
    }

    [Fact]
    public void CredentialBindingsScheduleIndependentlyAndRecoveryResetsAttemptCount()
    {
        var fixture = new Fixture();
        fixture.Exhaust(fixture.NewGoal(), Start, provider: "Anthropic", credential: "C:\\profiles\\a");
        fixture.Exhaust(fixture.NewGoal(), Start.AddHours(1), provider: "Anthropic", credential: "C:\\profiles\\b");
        Assert.True(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(fixture.Kernel.Goals, "anthropic::c:\\profiles\\a", out var first));
        Assert.True(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(fixture.Kernel.Goals, "anthropic::c:\\profiles\\b", out var second));
        Assert.Equal(Start.AddMinutes(31), ProviderBudgetHoldSchedules.Evaluate(fixture.Kernel.Goals, first, fixture.Clock.UtcNow).NextProbeAt);
        Assert.Equal(Start.AddMinutes(91), ProviderBudgetHoldSchedules.Evaluate(fixture.Kernel.Goals, second, fixture.Clock.UtcNow).NextProbeAt);
        var success = fixture.NewGoal();
        fixture.Dispatch(success, Start.AddHours(2), "Anthropic", "C:\\profiles\\a");
        fixture.Kernel.RecordTaskVerification(success.Id, success.Tasks.Single().Id,
            Verification("", Start.AddHours(2).AddMinutes(1)) with { ExitCode = 0 });
        fixture.Exhaust(fixture.NewGoal(), Start.AddHours(3), provider: "Anthropic", credential: "C:\\profiles\\a");
        Assert.True(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(fixture.Kernel.Goals, first.BindingKey, out var current));
        Assert.Equal(1, ProviderBudgetHoldSchedules.Evaluate(fixture.Kernel.Goals, current, fixture.Clock.UtcNow).Attempt);
    }

    [Fact]
    public void ExplicitBindingRecoveryResetsTheNextExhaustionAttemptCount()
    {
        var fixture = new Fixture();
        var source = fixture.NewGoal();
        fixture.Exhaust(source, Start);
        fixture.Kernel.ReportTaskProgress(source.Id, source.Tasks.Single().Id, WorkTaskStatus.Failed, "Exhausted.");
        fixture.Clock.UtcNow = Start.AddMinutes(2);
        fixture.Kernel.RetryTask(source.Id, source.Tasks.Single().Id, "Operator replenished balance.", RetryCause.ProviderBudgetRecovery);
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(fixture.Kernel.Goals, "xAI", null, out _));
        fixture.Exhaust(fixture.NewGoal(), Start.AddMinutes(3));
        Assert.Equal(1, fixture.Schedule().Attempt);
        Assert.Equal(Start.AddMinutes(34), fixture.Schedule().NextProbeAt);
    }

    [Fact]
    public void MarkersRoundTripUtcAndOnlyTaskNotesAreRead()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var offset = DateTimeOffset.Parse("2026-10-20T21:03:00-05:00");
        var probe = new ProviderBudgetHoldSchedules.ProbeMarker("anthropic::c:\\user profiles\\a", offset.ToUniversalTime(), 2, "operator");
        var reset = new ProviderBudgetHoldSchedules.ResetMarker(probe.BindingKey, offset.ToUniversalTime(), "intent-id");
        var probeText = ProviderBudgetHoldSchedules.FormatProbe(probe);
        var resetText = ProviderBudgetHoldSchedules.FormatReset(reset);
        Assert.Contains("2026-10-21T02:03:00.0000000+00:00", probeText);
        Assert.Contains("2026-10-21T02:03:00.0000000+00:00", resetText);
        foreach (var kind in Enum.GetValues<ProgressKind>())
        {
            var evt = new ProgressEvent(goalId, taskId, kind, probeText, Start);
            Assert.Equal(kind == ProgressKind.TaskNote, ProviderBudgetHoldSchedules.TryParseProbe(evt, out var parsedProbe));
            Assert.Equal(kind == ProgressKind.TaskNote, ProviderBudgetHoldSchedules.TryParseReset(evt with { Message = resetText }, out var parsedReset));
            if (kind != ProgressKind.TaskNote) continue;
            Assert.Equal(probe, parsedProbe);
            Assert.Equal(reset, parsedReset);
        }
    }

    private static TaskVerificationRecord Verification(string stderr, DateTimeOffset completed) =>
        new("fixture", "C:\\fixture", 1, "", stderr, completed, ProviderFailureKind:
            stderr.StartsWith("API error", StringComparison.Ordinal) ? ProviderFailureKind.BudgetExhausted : ProviderFailureKind.Unknown);

    private sealed class Clock : IClock { public DateTimeOffset UtcNow { get; set; } = Start; }
    private sealed class Fixture
    {
        internal readonly Clock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal Fixture() => Kernel = new(Clock);
        internal Goal NewGoal()
        {
            var goal = Kernel.CreateGoal("Schedule fixture", [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, [Agent("primary", "xAI")]);
            return goal;
        }
        internal void Dispatch(Goal goal, DateTimeOffset at, string provider = "xAI", string? credential = null) =>
            Kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id, new("grok-cli", "fixture", "C:\\fixture", at,
                ProviderName: provider, ClaudeCredentialSourceDirectory: credential));
        internal void Exhaust(Goal goal, DateTimeOffset at, string stderr = Exhausted, string provider = "xAI", string? credential = null)
        {
            Dispatch(goal, at, provider, credential);
            Clock.UtcNow = at.AddMinutes(1);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id, Verification(stderr, Clock.UtcNow) with { DispatchStartedAt = at });
        }
        internal ProviderBudgetHoldSchedule Schedule()
        {
            Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(Kernel.Goals, "xAI", null, out var hold));
            return ProviderBudgetHoldSchedules.Evaluate(Kernel.Goals, hold, Clock.UtcNow);
        }
        internal static AgentDefinition Agent(string id, string provider) => new(new(id), id, AgentRole.Tester,
            new(provider, "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey));
    }
}
