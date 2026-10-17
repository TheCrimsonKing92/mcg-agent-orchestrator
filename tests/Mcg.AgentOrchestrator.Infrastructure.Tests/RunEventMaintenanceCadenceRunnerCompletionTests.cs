using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each instance owns its journal and injected clock.
public sealed class RunEventMaintenanceCadenceRunnerCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-cadence-completion-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_root, "conduct.log");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operation_CompletesOrThrows_JournalsOneMeasuredCompletion(bool throws)
    {
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var runner = new RunEventMaintenanceCadenceRunner(LogPath, () =>
        {
            now = now.AddMilliseconds(90_000);
            if (throws) throw new InvalidOperationException("test failure");
        }, utcNow: () => now);

        Assert.True(runner.OnTick());
        await TestHangGuard.WaitAsync(runner.CurrentRun!, "cadence completion");

        Assert.Single(ReadEvents("storage-retention-sweep-started"));
        var completion = Assert.Single(ReadEvents("storage-retention-sweep-completed"));
        Assert.Equal($"STORAGE_RETENTION_SWEEP_COMPLETED elapsedMs=90000 outcome={(throws ? "failed" : "completed")}",
            completion.GetProperty("detail").GetString());
        Assert.Equal(throws ? 1 : 0, ReadEvents("run-events-maintenance-failed").Length);
    }

    [Fact]
    public async Task Operation_Blocked_NoCompletionUntilReleased()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var runner = new RunEventMaintenanceCadenceRunner(LogPath, () =>
        {
            entered.SetResult();
            TestHangGuard.WaitAsync(release.Task, "completion operation release").GetAwaiter().GetResult();
        }, utcNow: () => now);
        try
        {
            Assert.True(runner.OnTick());
            await TestHangGuard.WaitAsync(entered.Task, "completion operation entry");
            Assert.Single(ReadEvents("storage-retention-sweep-started"));
            Assert.Empty(ReadEvents("storage-retention-sweep-completed"));
            release.SetResult();
            await TestHangGuard.WaitAsync(runner.CurrentRun!, "released operation completion");
            Assert.Single(ReadEvents("storage-retention-sweep-completed"));
        }
        finally
        {
            release.TrySetResult();
            if (runner.CurrentRun is { } run)
                await TestHangGuard.WaitAsync(run, "completion operation cleanup");
        }
    }

    private JsonElement[] ReadEvents(string kind) => File.ReadAllLines(LogPath)
        .Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        })
        .Where(evt => evt.GetProperty("eventKind").GetString() == kind).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
