using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure values only; excerpt shapes are copied into deterministic fixtures.
public sealed class StateLogDivergenceComparerTests
{
    private const string Task = "3d5de16706e245d2852538aac35c797f";
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-04T04:27:53.4330126+00:00");

    [Fact]
    public void LostFindingCycleHasEachMeasuredKindAndCursor()
    {
        // store-refs/lost-mutation-excerpt.md: cursors 109-111 and 115-117, same
        // task/message across cycles, but no stored counterpart for these kinds.
        string[] kinds = ["FindingEvidenceRequestRecorded", "FindingEvidenceRunRecorded", "TaskRetried"];
        var logged = kinds.SelectMany((kind, index) => new[]
        {
            Parse(109 + index, kind, Time.AddTicks(index), "finding evidence-on-demand " + kind, Task),
            Parse(115 + index, kind, Time.AddHours(1).AddTicks(index), "finding evidence-on-demand " + kind, Task)
        }).ToArray();
        var clean = new StateLogEntry("GoalCreated", Time.UtcTicks, "created", null);
        var report = StateLogDivergenceComparer.Compare([clean], logged.Append(new(1, clean)));
        Assert.Equal(6, report.Lost);
        Assert.Equal(0, report.Repeated);
        Assert.Equal(0, report.StoredOnly);
        Assert.All(report.Items, item => Assert.Equal(StateLogDivergenceClass.Lost, item.Class));
        Assert.All(kinds, kind => Assert.Equal(2, report.Items.Count(item => item.Entry.Kind == kind)));
        Assert.Equal(109, report.FirstLogCursor);
        Assert.Contains("kinds=FindingEvidenceRequestRecorded:2,FindingEvidenceRunRecorded:2,TaskRetried:2", report.FormatDetail("d0ffaaaf5826420abf53fe82a5b7687f"));
    }

    [Fact]
    public void RepeatedHarvestMatchesKindTaskMessageAtAnotherTime()
    {
        const string message = "Tester WORKER_RESULT rejected: merged structured finding state still has open blocking stable_id(s): f67b2348-criterion-1-negative-control; automatic ownership routing required.";
        const string task = "ba05e306ec584994ae3e3847170a86e7";
        var first = Parse(40, "TaskFailed", DateTimeOffset.Parse("2026-10-04T15:23:22.6953036+00:00"), message, task);
        var second = Parse(45, "TaskFailed", DateTimeOffset.Parse("2026-10-04T15:23:54.5582918+00:00"), message, task);
        var report = StateLogDivergenceComparer.Compare([first.Entry], [first, second]);
        Assert.Equal(1, report.Repeated);
        Assert.Equal(0, report.Lost);
        Assert.Equal(0, report.StoredOnly);
        var repeated = Assert.Single(report.Items);
        Assert.Equal(StateLogDivergenceClass.Repeated, repeated.Class);
        Assert.Equal(45, repeated.Cursor);
    }

    [Fact]
    public void OnlyHeldAtDispositionIsExcluded()
    {
        var held = Parse(1, "GoalPolicyDecision", Time, "Batch loop tick 7: held at WorkspaceReady — waiting", null);
        var persisted = Parse(2, "GoalPolicyDecision", Time, "Batch loop tick 7: held: waiting", null);
        var wrongKind = Parse(3, "TaskNote", Time, held.Entry.Message, null);
        var wrongTick = Parse(4, "GoalPolicyDecision", Time, "Batch loop tick seven: held at WorkspaceReady", null);
        var report = StateLogDivergenceComparer.Compare([], [held, persisted, wrongKind, wrongTick]);
        Assert.Equal(1, report.ByDesign);
        Assert.Equal(StateLogDivergenceClass.ByDesign, report.Items.Single(item => item.Cursor == 1).Class);
        Assert.Equal(3, report.Lost);
        var onlyHeld = StateLogDivergenceComparer.Compare([], [held]);
        Assert.False(onlyHeld.HasDivergence);
    }

    [Fact]
    public void ExactMatchUsesUtcTicksAndMessageButRepeatedRequiresTask()
    {
        var stored = new StateLogEntry("TaskFailed", Time.UtcTicks, "failed", "task-a");
        var exact = Parse(1, stored.Kind, Time.ToOffset(TimeSpan.FromHours(-5)), stored.Message, "task-b");
        Assert.Empty(StateLogDivergenceComparer.Compare([stored], [exact]).Items);
        var otherTask = Parse(2, stored.Kind, Time.AddTicks(1), stored.Message, "task-b");
        var report = StateLogDivergenceComparer.Compare([stored], [exact, otherTask]);
        Assert.Equal(1, report.Lost);
        Assert.Equal(0, report.Repeated);
        var otherMessage = Parse(3, stored.Kind, Time.AddTicks(1), "different", "task-a");
        Assert.Equal(1, StateLogDivergenceComparer.Compare([stored], [exact, otherMessage]).Lost);
    }

    [Fact]
    public void StoredOnlyHasNumericZeroCursorAndProjectionLinesAreIgnored()
    {
        var entry = new StateLogEntry("TaskDispatchRecorded", Time.UtcTicks, "dispatch", Task);
        var report = StateLogDivergenceComparer.Compare([entry], []);
        Assert.Equal(StateLogDivergenceClass.StoredOnly, Assert.Single(report.Items).Class);
        Assert.Equal("STATE_LOG_DIVERGENCE goal=abcd1234 lost=0 repeated=0 stored_only=1 kinds=TaskDispatchRecorded:1 first_log_cursor=0 by_design=0", report.FormatDetail("abcd1234"));
        Assert.Null(StateLogDivergenceComparer.ParseLine("{\"eventType\":\"GoalCreated\"}"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NonNegativeLogCursorIsPreservedAsDivergenceEvidence(long cursor)
    {
        // GoalLifecycleEventWriter.cs::Append starts at CountExistingLines, which
        // returns zero for a new log; zero is a valid first timeline cursor.
        var line = Parse(cursor, "TaskRetried", Time, "finding evidence-on-demand", Task);
        Assert.Equal(cursor, line.Cursor);
        var report = StateLogDivergenceComparer.Compare([], [line]);
        Assert.Equal(StateLogDivergenceClass.Lost, Assert.Single(report.Items).Class);
        Assert.Equal(cursor, report.FirstLogCursor);
    }

    [Fact]
    public void NegativeLogCursorCannotBecomeDivergenceEvidence() =>
        Assert.Throws<FormatException>(() => Parse(-1, "TaskRetried", Time, "finding evidence-on-demand", Task));

    [Theory]
    [InlineData("{\"progressKind\":42}")]
    [InlineData("{\"progressKind\":\"TaskFailed\",\"occurredAt\":\"invalid\"}")]
    [InlineData("[]")]
    public void MalformedTimelineFieldsCannotBecomeDivergenceEvidence(string line) =>
        Assert.Throws<FormatException>(() => StateLogDivergenceComparer.ParseLine(line));

    internal static StateLogLine Parse(long cursor, string kind, DateTimeOffset time, string message, string? task) =>
        StateLogDivergenceComparer.ParseLine(JsonSerializer.Serialize(new
        { cursor, progressKind = kind, occurredAt = time, message, taskId = task }))!;
}
