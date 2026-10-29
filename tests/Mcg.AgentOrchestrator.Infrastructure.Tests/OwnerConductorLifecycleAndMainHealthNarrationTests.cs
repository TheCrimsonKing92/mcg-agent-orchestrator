using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: narration uses only supplied events, lookups and evidence.
public sealed class OwnerConductorLifecycleAndMainHealthNarrationTests
{
    private const string Continue = "No. You can let the conductor continue.";
    private const string Repair = "Yes. Check conductor status and restart it after addressing the failure.";
    private const string Review = "Yes. Review conductor status and address the startup failure.";

    [Theory]
    [InlineData("loop-stop", "LOOP_STOP reason=stop-file", "Conductor stopped",
        "The conductor finished or was stopped.", "Work waits until the conductor starts.")]
    [InlineData("loop-stop", "LOOP_STOP reason=max-duration", "Conductor stopped",
        "The conductor finished or was stopped.", "Work waits until the conductor starts.")]
    [InlineData("loop-start", "LOOP_START", "Conductor started",
        "The conductor was started.", "It will continue the queued work.")]
    [InlineData("loop-start", "LOOP_START selfCheck=false", "Conductor started",
        "The conductor was started.", "It will continue the queued work.")]
    public void Narrate_StandaloneLifecycle_PreservesTextAndMetadata(
        string kind, string detail, string phrase, string why, string next)
    {
        var source = Event(kind, detail);
        var line = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Search"));
        AssertItem(line, source, "", "Search", phrase, why, next, Continue);
        Assert.Null(line.Spans);
        Assert.Null(line.GoalPrefixes);
    }

    [Theory]
    [InlineData("ACTIVATION_FAILED_BOTH continuing=true reason=publish_failed")]
    [InlineData("LOOP_HANDOFF_FAILED continuing=false reason=publish_failed")]
    [InlineData("LOOP_HANDOFF_FAILED reason=publish_failed")]
    public void Narrate_FailedHandoffWithoutUsableConductor_PreservesStoppedOutput(string detail)
    {
        var source = Event("loop-handoff", detail);
        var line = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Search"));
        AssertItem(line, source, "", "Search", "Conductor stopped",
            "The conductor could not start the new version (the new version could not be built).",
            "Work waits for the conductor to be started.", Repair);
        Assert.Null(line.Spans);
        Assert.Null(line.GoalPrefixes);
    }

    [Theory]
    [InlineData("ACTIVATION_REVERTED reason=startup_timeout", "the new version did not become ready")]
    [InlineData("LOOP_HANDOFF_FAILED continuing=true reason=process_exit",
        "the new process exited before becoming ready")]
    [InlineData("ACTIVATION_REVERTED reason=receipt_missing", "the new version could not start")]
    public void Narrate_RevertedHandoff_PreservesRunningOutput(string detail, string reason)
    {
        var source = Event("loop-handoff", detail);
        var line = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Search"));
        AssertItem(line, source, "", "Search",
            "Conductor could not switch to the new code; still running the previous version (" + reason + ")",
            "The new version could not be activated.", "The previous version continues running.", Review);
        Assert.Null(line.Spans);
        Assert.Null(line.GoalPrefixes);
    }

    [Theory]
    [InlineData("self-relaunch-handoff", "loop-start", "LOOP_START")]
    [InlineData("self-relaunch-handoff", "loop-start", "LOOP_START selfCheck=true")]
    [InlineData("max-duration", "loop-start", "LOOP_START")]
    [InlineData("max-duration", "loop-handoff", "ACTIVATION_ADOPTED reason=none")]
    public void Narrate_RestartHistory_SuppressesStopAndStart(
        string reason, string successorKind, string successorDetail)
    {
        var stop = Event("loop-stop", "LOOP_STOP reason=" + reason);
        var successor = Event(successorKind, successorDetail, 1);
        var adopted = Event("loop-handoff", "ACTIVATION_ADOPTED reason=none", 2);
        // Reverse input proves traversal and lookahead use timestamp order.
        Assert.Empty(OwnerActivityNarrator.Narrate([adopted, successor, stop], _ => "Search"));
    }

    [Fact]
    public void Narrate_StartAfterRestart_ClearsStoppedAndRestartingState()
    {
        var stop = Event("loop-stop", "LOOP_STOP reason=max-duration");
        var start = Event("loop-start", "LOOP_START", 1);
        var handoff = Event("loop-handoff", "LOOP_HANDOFF_FAILED continuing=true reason=publish_failed", 2);
        var laterStart = Event("loop-start", "LOOP_START", 3);
        var lines = OwnerActivityNarrator.Narrate([handoff, laterStart, start, stop], _ => "Search");
        Assert.Equal(2, lines.Count);
        AssertItem(lines[0], laterStart, "", "Search", "Conductor started",
            "The conductor was started.", "It will continue the queued work.", Continue);
        AssertItem(lines[1], handoff, "", "Search",
            "Conductor could not switch to the new code; still running the previous version (the new version could not be built)",
            "The new version could not be activated.", "The previous version continues running.", Review);
    }

    [Fact]
    public void Narrate_StoppedHistory_DoesNotClaimFailedHandoffIsStillRunning()
    {
        var stop = Event("loop-stop", "LOOP_STOP reason=self-relaunch-handoff");
        var handoff = Event("loop-handoff", "LOOP_HANDOFF_FAILED continuing=true reason=publish_failed", 1);
        var line = Assert.Single(OwnerActivityNarrator.Narrate([handoff, stop], _ => "Search"));
        AssertItem(line, handoff, "", "Search", "Conductor stopped",
            "The conductor could not start the new version (the new version could not be built).",
            "Work waits for the conductor to be started.", Repair);
    }

    [Fact]
    public void Narrate_SeparateInvocations_StartWithFreshLifecycleState()
    {
        Assert.Empty(OwnerActivityNarrator.Narrate(
            [Event("loop-stop", "LOOP_STOP reason=self-relaunch-handoff")], _ => "Search"));
        var start = Event("loop-start", "LOOP_START");
        var line = Assert.Single(OwnerActivityNarrator.Narrate([start], _ => "Search"));
        AssertItem(line, start, "", "Search", "Conductor started",
            "The conductor was started.", "It will continue the queued work.", Continue);
        var handoff = Event("loop-handoff", "LOOP_HANDOFF_FAILED continuing=true reason=publish_failed");
        line = Assert.Single(OwnerActivityNarrator.Narrate([handoff], _ => "Search"));
        AssertItem(line, handoff, "", "Search",
            "Conductor could not switch to the new code; still running the previous version (the new version could not be built)",
            "The new version could not be activated.", "The previous version continues running.", Review);
    }

    [Fact]
    public void Narrate_StartBeforeMaxDurationStop_DoesNotSuppressTheStop()
    {
        var start = Event("loop-start", "LOOP_START");
        var stop = Event("loop-stop", "LOOP_STOP reason=max-duration", 1);
        var lines = OwnerActivityNarrator.Narrate([stop, start], _ => "Search");
        Assert.Equal(2, lines.Count);
        AssertItem(lines[0], stop, "", "Search", "Conductor stopped",
            "The conductor finished or was stopped.", "Work waits until the conductor starts.", Continue);
        AssertItem(lines[1], start, "", "Search", "Conductor started",
            "The conductor was started.", "It will continue the queued work.", Continue);
    }

    [Theory]
    [InlineData("loop-start", "LOOP_START selfCheck=true")]
    [InlineData("loop-handoff", "ACTIVATION_ADOPTED reason=none")]
    [InlineData("loop-handoff", "LOOP_HANDOFF reason=none")]
    [InlineData("canary-gate", "CANARY_GATE result=passed")]
    [InlineData("canary-gate", "CANARY_GATE result=unverified")]
    [InlineData("canary-gate", "CANARY_GATE result=deferred")]
    [InlineData("state-log-divergence", "STATE_LOG_DIVERGENCE lost=0 repeated=0")]
    public void Narrate_UnreportedOutcomes_RemainsSilent(string kind, string detail)
    {
        Assert.Empty(OwnerActivityNarrator.Narrate([Event(kind, detail)], _ => "Search"));
    }

    [Theory]
    [InlineData(0, "Main broke after landing recent work: Check.Main")]
    [InlineData(1, "Main broke after landing Search: Check.Main")]
    [InlineData(2, "Main broke after landing Search, Export: Check.Main")]
    public void Narrate_CanaryFailure_PreservesLandingTextSpansAndPrefixes(int count, string phrase)
    {
        var events = new List<OwnerConductEvent>();
        if (count > 0) events.Add(Event("loop-relaunch", "LOOP_RELAUNCH_SCHEDULED tick=7", 0, "11111111aaaa"));
        if (count > 1) events.Add(Event("loop-relaunch", "LOOP_RELAUNCH_SCHEDULED tick=7", 1, "22222222bbbb"));
        var failure = Event("canary-gate", "CANARY_GATE result=failed tests=Check.Main", 2);
        events.Add(failure);
        var lines = OwnerActivityNarrator.Narrate(events.AsEnumerable().Reverse(),
            id => id == "11111111aaaa" ? "Search" : id == "22222222bbbb" ? "Export" : "Main");
        Assert.Equal(count == 0 ? 1 : 2, lines.Count);
        var line = lines[0];
        AssertItem(line, failure, "", "Main", phrase,
            "A check of main failed after the work landed.",
            "Further landings wait while the failure is investigated.",
            "Yes. Review the failure and decide how to repair main.");
        OwnerConsoleTitleSpan[] titles = count switch
        {
            0 => [],
            1 => [new(25, 6, 0)],
            _ => [new(25, 6, 0), new(33, 6, 0)]
        };
        AssertSpans(line, titles);
        if (count == 2) Assert.Equal(new[] { "11111111", "22222222" }, line.GoalPrefixes);
        else Assert.Null(line.GoalPrefixes);
    }

    [Theory]
    [InlineData("CANARY_GATE result=failed tests=Check.Main", "Check.Main")]
    [InlineData("CANARY_GATE result=failed", "Build main")]
    public void Narrate_CanaryCheckSources_PreservesFieldBeforeEvidence(string detail, string check)
    {
        var source = Event("canary-gate", detail);
        var line = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Main",
            _ => new([], ["Build main"])));
        AssertItem(line, source, "", "Main", "Main broke after landing recent work: " + check,
            "A check of main failed after the work landed.",
            "Further landings wait while the failure is investigated.",
            "Yes. Review the failure and decide how to repair main.");
        AssertSpans(line, []);
        Assert.Null(line.GoalPrefixes);
    }

    [Fact]
    public void Narrate_CanaryWithoutCheck_PreservesFallbackText()
    {
        var source = Event("canary-gate", "CANARY_GATE result=failed");
        var line = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Main"));
        AssertItem(line, source, "", "Main", "Main broke after landing recent work: the full test run on main",
            "A check of main failed after the work landed.",
            "Further landings wait while the failure is investigated.",
            "Yes. Review the failure and decide how to repair main.");
        AssertSpans(line, []);
        Assert.Null(line.GoalPrefixes);
    }

    [Theory]
    [InlineData("STATE_LOG_DIVERGENCE lost=1 repeated=0")]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=1")]
    public void Narrate_StateLogDivergence_PreservesTextAndTitleSpan(string detail)
    {
        var source = Event("state-log-divergence", detail, goal: "11111111aaaa");
        var line = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Search: filters"));
        AssertItem(line, source, "11111111", "Search: filters", "11111111 history needs review: Search: filters",
            "Some recorded progress is missing or repeated.", "The operator can check the goal's history.", Continue);
        AssertSpans(line, [new(31, 15, 7)]);
        Assert.Null(line.GoalPrefixes);
    }

    private static OwnerConductEvent Event(string kind, string detail, int seconds = 0, string? goal = null) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), kind, goal, detail);

    private static void AssertItem(OwnerConsoleActivityItem actual, OwnerConductEvent source,
        string prefix, string title, string phrase, string why, string next, string act)
    {
        var expected = new OwnerConsoleActivityItem(source.Timestamp, source.EventKind, "outcome",
            prefix, source.Detail, title, phrase, why, next, act, Subject: why);
        // Compare every scalar field, including fields that should remain null.
        Assert.Equal(expected, actual with { Spans = null, GoalPrefixes = null });
    }

    private static void AssertSpans(OwnerConsoleActivityItem line, OwnerConsoleTitleSpan[] titles)
    {
        var spans = Assert.IsType<OwnerConsoleLineSpans>(line.Spans);
        Assert.Equal(titles, spans.Titles);
        Assert.Equal(-1, spans.OutcomeStart);
        Assert.Equal(0, spans.OutcomeLength);
    }
}
