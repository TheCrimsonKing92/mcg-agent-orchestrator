using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its stream and rotated files; no goal-events directory exists.
public sealed class OwnerConsoleStreamRefreshTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-console-stream-refresh-");
    private string Log => Path.Combine(_root.FullName, ChangeStreamWriter.FileName);
    private static readonly string[] Board =
        ["abcdef01111111111111111111111111", "abcdef02222222222222222222222222"];
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch;

    [Fact(DisplayName = "Stream polls rebuild once per batch and recover after unknown schema")]
    public async Task PollOnce_BatchesRecordsAndRecoversWithoutReplay()
    {
        var scopes = new List<OwnerConsoleRefreshScope>();
        var refresh = Refresh(scopes);
        var writer = new ChangeStreamWriter(Log);

        Assert.Equal(ChangeStreamRecord.GoalStage,
            writer.Append("goal-lifecycle", Board[0], "finished", Timestamp)!.ChangeKind);
        await refresh.PollOnceAsync();
        Assert.Equal([Board[0]], Assert.Single(scopes).Resolve(Board));

        Assert.Equal(ChangeStreamRecord.GoalTransition,
            writer.Append("goal-landing", Board[0][..8], "landed", Timestamp)!.ChangeKind);
        await refresh.PollOnceAsync();
        Assert.Equal(2, scopes.Count);
        Assert.Equal([Board[0]], scopes[1].Resolve(Board));

        writer.Append("goal-lifecycle", Board[0], "next stage", Timestamp);
        writer.Append("goal-landing", Board[0][..8].ToUpperInvariant(), "next landing", Timestamp);
        await refresh.PollOnceAsync();
        Assert.Equal(3, scopes.Count);
        Assert.Equal([Board[0]], scopes[2].Resolve(Board));
        await refresh.PollOnceAsync();
        Assert.Equal(3, scopes.Count);

        var unknown = new ChangeStreamRecord(99, 5, Timestamp, ChangeStreamRecord.GoalStage,
            Board[0], "goal-lifecycle", "unknown schema");
        File.AppendAllText(Log, JsonSerializer.Serialize(unknown, ChangeStreamRecord.JsonOptions) + "\n");
        await refresh.PollOnceAsync();
        Assert.Equal(4, scopes.Count);
        Assert.Same(OwnerConsoleRefreshScope.All, scopes[3]);
        Assert.Equal(Board, scopes[3].Resolve(Board));

        writer.Append("goal-lifecycle", Board[1], "after discontinuity", Timestamp);
        await refresh.PollOnceAsync();
        Assert.Equal(5, scopes.Count);
        Assert.Equal([Board[1]], scopes[4].Resolve(Board));
        await refresh.PollOnceAsync();
        Assert.Equal(5, scopes.Count);
        Assert.Empty(_root.GetDirectories());
    }

    [Fact(DisplayName = "Rotation requests one full refresh and reanchors future deltas")]
    public async Task PollOnce_ReanchorsAfterRotation()
    {
        var writer = new ChangeStreamWriter(Log);
        writer.Append("goal-lifecycle", Board[0], "existing", Timestamp);
        var scopes = new List<OwnerConsoleRefreshScope>();
        var refresh = Refresh(scopes);
        File.Move(Log, Path.Combine(_root.FullName, "change-stream-moved.log"));
        writer.Append("goal-landing", Board[0][..8], "replacement", Timestamp);

        await refresh.PollOnceAsync();
        Assert.Same(OwnerConsoleRefreshScope.All, Assert.Single(scopes));
        await refresh.PollOnceAsync();
        Assert.Single(scopes);
        writer.Append("goal-lifecycle", Board[1], "new delta", Timestamp);
        await refresh.PollOnceAsync();
        Assert.Equal(2, scopes.Count);
        Assert.Equal([Board[1]], scopes[1].Resolve(Board));
    }

    private OwnerConsoleStreamRefresh Refresh(List<OwnerConsoleRefreshScope> scopes) =>
        new(new ChangeStreamFileReader(Log), scope => { scopes.Add(scope); return Task.CompletedTask; }, TimeProvider.System);

    public void Dispose() => _root.Delete(true);
}
