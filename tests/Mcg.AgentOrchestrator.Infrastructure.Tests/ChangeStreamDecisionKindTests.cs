using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its stream and path-scoped writer mutex.
public sealed class ChangeStreamDecisionKindTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-decision-kinds-");
    private string Log => Path.Combine(_root.FullName, ChangeStreamWriter.FileName);
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData("goal-decision-answered", "owner-decision-answered")]
    [InlineData("goal-decision-retired", "owner-decision-retired")]
    public void Classify_RequiresGoalId(string source, string kind)
    {
        Assert.True(ChangeStreamRecord.TryClassify(source, "g", out var actual));
        Assert.Equal(kind, actual);
        foreach (var id in new string?[] { null, "", "   " })
        {
            Assert.False(ChangeStreamRecord.TryClassify(source, id, out actual));
            Assert.Null(actual);
        }
    }

    [Theory]
    [InlineData("goal-decision-answered", "owner-decision-answered")]
    [InlineData("goal-decision-retired", "owner-decision-retired")]
    public void Parse_RoundTripsSchemaOneAndRejectsMismatchedKind(string source, string kind)
    {
        Assert.Equal(1, ChangeStreamRecord.CurrentSchemaVersion);
        var record = new ChangeStreamRecord(1, 1, Timestamp, kind, "g", source, "changed");
        Assert.True(ChangeStreamRecord.TryParse(Serialize(record), out var parsed));
        Assert.Equal(record, parsed);
        Assert.False(ChangeStreamRecord.TryParse(
            Serialize(record with { ChangeKind = ChangeStreamRecord.GoalStage }), out _));
    }

    [Fact]
    public void WriterAndReader_PreserveMixedKindsAndContiguousSequences()
    {
        var reader = new ChangeStreamFileReader(Log);
        var writer = new ChangeStreamWriter(Log);
        string[] sources = ["goal", "goal-decision-answered", "goal-lifecycle", "goal-decision-retired"];
        foreach (var source in sources)
            Assert.NotNull(writer.Append(source, "g", "changed", Timestamp));

        var records = reader.ReadAvailable(out var discontinuity);
        Assert.Null(discontinuity);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, records.Select(record => record.Sequence));
        Assert.Equal(sources, records.Select(record => record.SourceEventKind));
        Assert.Equal(new[] { "goal-transition", "owner-decision-answered", "goal-stage", "owner-decision-retired" },
            records.Select(record => record.ChangeKind));
    }

    [Fact]
    public async Task Refresh_AnsweredBatchRebuildsOnceForOnlyItsGoal()
    {
        string[] board = ["11111111111111111111111111111111", "22222222222222222222222222222222"];
        var scopes = new List<OwnerConsoleRefreshScope>();
        var refresh = new OwnerConsoleStreamRefresh(new ChangeStreamFileReader(Log),
            scope => { scopes.Add(scope); return Task.CompletedTask; }, new OwnerConsoleHarness().Clock);
        Assert.NotNull(new ChangeStreamWriter(Log).Append("goal-decision-answered", board[0], "answered", Timestamp));

        await refresh.PollOnceAsync();

        Assert.Equal(new[] { board[0] }, Assert.Single(scopes).Resolve(board));
        await refresh.PollOnceAsync();
        Assert.Single(scopes);
    }

    private static string Serialize(ChangeStreamRecord record) => JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions);
    public void Dispose() => _root.Delete(true);
}
