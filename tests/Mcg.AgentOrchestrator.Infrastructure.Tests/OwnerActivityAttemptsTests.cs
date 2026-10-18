using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure event inputs and narration, with no shared mutable state.
public sealed class OwnerActivityAttemptsTests
{
    [Fact]
    public void Narrate_RetriedTask_LabelsStagesAndPreservesTitleSpanStarts()
    {
        var events = Events("TaskDispatched task=t1 role=Developer", "TaskFailed task=t1 role=Developer",
            "TaskDispatched task=t1 role=Developer", "TaskCompleted task=t1 role=Developer",
            "TaskDispatched task=t1 role=Developer", "TaskFailed task=t1 role=Developer");
        var items = OwnerActivityNarrator.Narrate(events, _ => "Search").OrderBy(item => item.Timestamp).ToArray();
        string[] phrases = ["Developer started on Search",
            "Developer sent Search back: the worker could not finish",
            "Developer started on Search (attempt 2)", "Developer passed Search (attempt 2)",
            "Developer started on Search (attempt 3)",
            "Developer sent Search back (attempt 3): the worker could not finish"];
        string[] leads = ["Developer started on ", "Developer sent ", "Developer started on ",
            "Developer passed ", "Developer started on ", "Developer sent "];

        Assert.Equal(phrases.Length, items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            Assert.Equal(phrases[index], items[index].Phrase);
            Assert.Equal(leads[index].Length, Assert.Single(items[index].Spans!.Titles).Start);
        }
    }

    [Theory]
    [InlineData("missing task")]
    [InlineData("different goals")]
    [InlineData("different tasks")]
    [InlineData("first completion")]
    [InlineData("orphan failure")]
    public void Narrate_NoRepeatedTaskDispatch_MatchesUnchangedDetailPhrase(string scenario)
    {
        var events = scenario switch
        {
            "missing task" => Events("TaskDispatched role=Developer", "TaskDispatched role=Developer"),
            "different goals" => [Event(1, "TaskDispatched task=t1 role=Developer"),
                Event(2, "TaskDispatched task=t1 role=Developer") with { GoalId = "22222222" }],
            "different tasks" => Events("TaskDispatched task=t1 role=Developer", "TaskDispatched task=t2 role=Developer"),
            "first completion" => Events("TaskDispatched task=t1 role=Developer", "TaskCompleted task=t1 role=Developer"),
            "orphan failure" => Events("TaskFailed task=t1 role=Developer"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var items = OwnerActivityNarrator.Narrate(events, _ => "Search").OrderBy(item => item.Timestamp).ToArray();

        Assert.Equal(events.Length, items.Length);
        for (var index = 0; index < events.Length; index++)
            Assert.Equal(OwnerActivityNarrator.DetailPhrase(events[index], "Search"), items[index].Phrase);
    }

    [Fact]
    public void Number_MixedEvents_CountsOnlyMatchingDispatchesThroughCurrentIndex()
    {
        OwnerConductEvent[] events = [
            Event(1, "TaskDispatched task=t1") with { GoalId = "abcdef12-first" },
            Event(2, "TaskDispatched task=T1") with { GoalId = "ABCDEF12-second" },
            Event(3, "TaskDispatched task=t1") with { GoalId = "abcdef12-first", EventKind = "other" },
            Event(4, "TaskDispatched task=t1") with { GoalId = "abcdef13-other" },
            Event(5, "TaskDispatched task=t1") with { GoalId = "ABCDEF12-second" },
            Event(6, "TaskCompleted task=t1") with { GoalId = "abcdef12-third" },
            Event(7, "TaskDispatched task=t1") with { GoalId = "abcdef12-first" }];

        Assert.Equal(2, OwnerActivityAttempts.Number(events, 5));
        Assert.Null(OwnerActivityAttempts.Number(events, 2));
        Assert.Null(OwnerActivityAttempts.Number([Event(1, "TaskFailed task=t1")], 0));
        Assert.Null(OwnerActivityAttempts.Number([Event(1, "TaskDispatched role=Developer")], 0));
    }

    private static OwnerConductEvent[] Events(params string[] details) =>
        details.Select((detail, index) => Event(index + 1, detail)).ToArray();

    private static OwnerConductEvent Event(int second, string detail) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(second), "goal-lifecycle", "11111111", detail);
}
