using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorStewardModelRoundCommandTests
{
    [Xunit.Fact]
    public async Task Command_limits_tools_and_time_and_records_the_session_id()
    {
        using var temp = TempDirectory.Create();
        WorkerProcessRunRequest? request = null;
        var sessionId = Guid.Parse("cb9e91a9-a28f-490c-b338-afee7823171c");
        var receiptDirectory = Path.Combine(temp.Path, "receipts");
        var round = NewRound(receiptDirectory, sessionId, (value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
        });

        await round.DispatchAsync(Trigger(), temp.Path, CancellationToken.None);

        Xunit.Assert.NotNull(request);
        Xunit.Assert.Contains("--permission-mode plan", request!.Command);
        Xunit.Assert.Contains("--tools 'Read,Grep,Glob'", request.Command);
        Xunit.Assert.Contains("--allowed-tools 'Read,Grep,Glob'", request.Command);
        Xunit.Assert.Contains($"--session-id {sessionId:D}", request.Command);
        foreach (var forbidden in new[] { "Bash", "Edit", "Write", "WebFetch" })
            Xunit.Assert.DoesNotContain(forbidden, request.Command);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(4), request.Timeout);
        Xunit.Assert.Equal(sessionId.ToString("D"), ReceiptSessionId(receiptDirectory));
    }

    [Xunit.Fact]
    public async Task Runner_exception_still_records_the_session_id()
    {
        using var temp = TempDirectory.Create();
        var sessionId = Guid.Parse("f5cfef59-fd8c-4841-b9c1-8336bbd5147c");
        var receiptDirectory = Path.Combine(temp.Path, "receipts");
        var round = NewRound(receiptDirectory, sessionId, (_, _) =>
            throw new IOException("process unavailable"));

        await Xunit.Assert.ThrowsAsync<IOException>(() =>
            round.DispatchAsync(Trigger(), temp.Path, CancellationToken.None));

        Xunit.Assert.Equal(sessionId.ToString("D"), ReceiptSessionId(receiptDirectory));
    }

    private static ClaudeConductorStewardModelRound NewRound(string receiptDirectory, Guid sessionId,
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runProcessAsync) =>
        new(receiptDirectory, runProcessAsync, new EmptyFiles(), (_, _) => true, () => sessionId);

    private static string ReceiptSessionId(string receiptDirectory)
    {
        var receipt = Xunit.Assert.Single(Directory.GetFiles(receiptDirectory, "*.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(receipt));
        return json.RootElement.GetProperty("sessionId").GetString()!;
    }

    private static ConductorStewardTrigger Trigger() => new(
        "goal", "task", "candidate-sha", ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed,
        DateTimeOffset.UtcNow, "Assertion failure", "", [], ["criterion"]);

    private sealed class EmptyFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;
        internal string Path { get; }
        internal static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "mcg-steward-command-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
