using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: directly constructed events and supplied lookups; no external state.
public sealed class OwnerAcceptanceNarrationTests
{
    [Fact]
    public void TryNarrate_SinglePassed_ReportsLandingNext()
    {
        OwnerConductEvent[] events =
            [new(DateTimeOffset.UnixEpoch, "acceptance", "11111111", "ACCEPTANCE result=passed")];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name,
            _ => throw new InvalidOperationException("Passed acceptance must not request failure evidence."), null, out var result));

        Assert.Equal("Search: passed its tests, landing next", result.Sentence);
        Assert.Equal("The required checks passed.", result.Why);
        Assert.Equal("The conductor will land this work.", result.Next);
        Assert.Equal("No. You can let the conductor continue.", result.Act);
        Assert.Null(result.GoalPrefixes);
        Assert.Equal(new OwnerConsoleTitleSpan(0, 6, 0), Assert.Single(result.Spans.Titles));
        Assert.Equal(8, result.Spans.OutcomeStart);
        Assert.Equal(6, result.Spans.OutcomeLength);
    }

    [Fact]
    public void TryNarrate_OwnNewTestFailed_ReturnsWorkToDeveloper()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance", "11111111",
            "ACCEPTANCE result=failed tests=NewTests.Behavior")];
        var evidenceCalls = 0;

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name, source =>
        {
            Assert.Same(events[0], source);
            evidenceCalls++;
            return new(["NewTests.Behavior"], [], true);
        }, null, out var result));

        Assert.Equal("Search: failed its tests (its own new test NewTests.Behavior failed); sent back to the Developer", result.Sentence);
        Assert.Equal("its own new test NewTests.Behavior failed.", result.Why);
        Assert.Equal("sent back to the Developer.", result.Next);
        Assert.Equal("No. The conductor is handling the next step.", result.Act);
        Assert.Equal(1, evidenceCalls);
        Assert.Null(result.GoalPrefixes);
        Assert.Equal(8, result.Spans.OutcomeStart);
        Assert.Equal(6, result.Spans.OutcomeLength);
    }

    [Fact]
    public void TryNarrate_JointPassed_PreservesMemberOrderAndTitleSpans()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "ACCEPTANCE_COHORT members=11111111,22222222 outcome=passed")];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name,
            _ => throw new InvalidOperationException("Passed joint run must not request failure evidence."), null, out var result));

        Assert.Equal("Joint test run for Search, Export: passed", result.Sentence);
        Assert.Equal("The joint checks passed.", result.Why);
        Assert.Equal("The conductor will land the work.", result.Next);
        Assert.Equal("No. You can let the conductor continue.", result.Act);
        Assert.Equal(new[] { "11111111", "22222222" }, result.GoalPrefixes);
        Assert.Equal(new[] { new OwnerConsoleTitleSpan(19, 6, 0), new OwnerConsoleTitleSpan(27, 6, 0) }, result.Spans.Titles);
        Assert.Equal(35, result.Spans.OutcomeStart);
        Assert.Equal(6, result.Spans.OutcomeLength);
    }

    [Fact]
    public void TryNarrate_InteractionOnlyFailure_DoesNotBlamePassingMembers()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "ACCEPTANCE_COHORT members=11111111,22222222 outcome=interaction-only attribution=InteractionOnly partitions=11111111:Passed,22222222:Passed")];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name, null, null, out var result));

        Assert.Equal("Joint test run for Search, Export: failed (the changes failed when tested together); awaiting the conductor's next step", result.Sentence);
        Assert.Equal("the changes failed when tested together.", result.Why);
        Assert.Equal("awaiting the conductor's next step.", result.Next);
        Assert.Equal("No. The conductor is handling the next step.", result.Act);
        Assert.Equal(new[] { "11111111", "22222222" }, result.GoalPrefixes);
    }

    [Fact]
    public void TryNarrate_SupersededCohortChild_OnlyNarratesLaterOutcome()
    {
        OwnerConductEvent[] events =
        [
            new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
                "ACCEPTANCE_COHORT_CHILD_COMPLETED members=11111111+22222222 verdict=failed"),
            new(DateTimeOffset.UnixEpoch.AddSeconds(1), "acceptance-cohort", null,
                "ACCEPTANCE_COHORT members=22222222,11111111 outcome=passed")
        ];

        Assert.False(OwnerAcceptanceNarration.TryNarrate(events, 0, Name,
            _ => throw new InvalidOperationException("Superseded child must not request failure evidence."), null, out _));
        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 1, Name, null, null, out var result));

        Assert.Equal("Joint test run for Export, Search: passed", result.Sentence);
        Assert.Equal("The joint checks passed.", result.Why);
        Assert.Equal("The conductor will land the work.", result.Next);
        Assert.Equal("No. You can let the conductor continue.", result.Act);
        Assert.Equal(new[] { "22222222", "11111111" }, result.GoalPrefixes);
    }

    [Fact]
    public void TryNarrate_UnresolvedMemberQuestion_RequestsOwnerAnswer()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "ACCEPTANCE_COHORT members=11111111,22222222 outcome=failed attribution=SecondMemberFailed tests=Export.NewBehavior")];
        OwnerAttentionObservation[] attention = [new(new("q1", "22222222-full", OwnerQuestionKind.StewardHold,
            "Keep the export format?"), DateTimeOffset.UnixEpoch)];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name, null, attention, out var result));

        Assert.Equal("Joint test run for Search, Export: failed (its own new test Export.NewBehavior failed); needs you", result.Sentence);
        Assert.Equal("its own new test Export.NewBehavior failed.", result.Why);
        Assert.Equal("needs you.", result.Next);
        Assert.Equal("Yes. Review the failed check and answer the question in DECISIONS.", result.Act);
        Assert.Equal(new[] { "11111111", "22222222" }, result.GoalPrefixes);
    }

    private static string Name(string? id) => id == "22222222" ? "Export" : "Search";
}
