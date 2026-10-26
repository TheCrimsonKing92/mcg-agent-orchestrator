using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: the loader and every lifecycle file belong to this test's temporary root.
public sealed class OwnerConsoleActivityLoaderScopeTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-console-activity-scope-");
    private static readonly string[] Board =
        ["abcdef01111111111111111111111111", "abcdef02222222222222222222222222"];

    [Fact(DisplayName = "Scoped activity reads preserve peer cursors and periodic CLI fallback")]
    public async Task ReadNew_ScopesPreserveUnreadPeerActivity()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root.FullName, "goal-events")).FullName;
        await using var loader = new OwnerConsoleActivityLoader(Path.Combine(_root.FullName, "conduct.log"), directory, TimeProvider.System);
        Append(directory, Board[0], 1);
        Append(directory, Board[1], 2);
        Assert.False(File.Exists(Path.Combine(_root.FullName, ChangeStreamWriter.FileName)));
        AssertTimes(loader.ReadNew(Board, OwnerConsoleRefreshScope.All), 1, 2);

        Append(directory, Board[0], 3);
        Append(directory, Board[1], 4);
        AssertTimes(loader.ReadNew(Board, OwnerConsoleRefreshScope.For([Board[0][..8]])), 3);
        AssertTimes(loader.ReadNew(Board, OwnerConsoleRefreshScope.All), 4);
        Assert.Empty(loader.ReadNew(Board, OwnerConsoleRefreshScope.All));

        Append(directory, Board[0], 5);
        Append(directory, Board[1], 6);
        Assert.Empty(loader.ReadNew(Board, OwnerConsoleRefreshScope.None));
        AssertTimes(loader.ReadNew(Board, OwnerConsoleRefreshScope.All), 5, 6);
        Assert.Empty(loader.ReadNew(Board, OwnerConsoleRefreshScope.All));
    }

    private static void Append(string directory, string goalId, int seconds) =>
        File.AppendAllText(Path.Combine(directory, goalId + ".jsonl"), JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds), eventType = "TaskDispatched",
            taskId = "task-" + seconds, message = "operator-visible dispatch"
        }) + "\n");

    private static void AssertTimes(IReadOnlyList<OwnerConductEvent> events, params int[] seconds) =>
        Assert.Equal(seconds.Select(value => DateTimeOffset.UnixEpoch.AddSeconds(value)),
            events.Select(item => item.Timestamp).Order());

    public void Dispose() => _root.Delete(true);
}
