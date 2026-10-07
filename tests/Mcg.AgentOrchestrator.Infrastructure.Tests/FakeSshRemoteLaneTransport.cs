using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;

// No child processes. Poll input and completion are explicit events owned by the test.
internal sealed class FakeSshRemoteLaneTransport : IAsyncDisposable
{
    internal sealed record Call(string[] Arguments, string Directory, TimeSpan Bound);
    internal sealed record Poll(Dictionary<string, object?>? Status, string Heartbeat = "executor-time", int Exit = 0,
        bool TimedOut = false, bool Malformed = false, string? Stderr = null);
    internal readonly ConcurrentQueue<Call> Calls = new();
    internal readonly Channel<Poll> Polls = Channel.CreateUnbounded<Poll>();
    private readonly Channel<SshPollObservation> _observations = Channel.CreateUnbounded<SshPollObservation>();
    internal readonly string Root = InfrastructureTestSupport.CreateTempDirectory();
    internal readonly ManualRemoteLaneClock Clock = new();
    internal int? FailStep;
    internal bool TimeoutStep;
    internal bool FetchFailure;
    internal string? FetchStderr;
    internal bool WritePartialFetch;
    internal readonly Dictionary<string, string> FetchContents = new();
    internal string Noise = "";
    internal SshRemoteLaneHandle? Handle;
    internal static RemoteLaneRequest Request => new("one", "attempt-one", "goal", "infrastructure tests: Cli / Lane",
        "tests/Infrastructure.csproj", "FullyQualifiedName~LaneTests", "filter-hash", "123456789abcdef", "tree", "main", "manifest");
    internal SshRemoteLaneExecutor Executor(bool ssh = true, bool prefix = true) => new(
        new([ssh ? new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor") : new("one", 60)],
            [Request.Lane], null), Root, prefix ? Path.Combine(Root, "attempt", "result") : null, Clock, Transport, Git,
        TimeSpan.Zero, observation => _observations.Writer.TryWrite(observation));
    internal GitCli.GitResult Git(string directory, int bound, string[] args)
    {
        Calls.Enqueue(new(args, directory, TimeSpan.FromMilliseconds(bound)));
        return new(FailStep == 0 ? -1 : 0, Noise, Noise);
    }
    internal async Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
    {
        if (args.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)))
        {
            var poll = await Polls.Reader.ReadAsync(token);
            Calls.Enqueue(new(args, directory, bound));
            var destination = Path.Combine(directory, args[^1]);
            Directory.CreateDirectory(destination);
            if (poll.Status is not null) File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(poll.Status), new System.Text.UTF8Encoding(true));
            if (poll.Malformed) File.WriteAllText(Path.Combine(destination, "status.json"), "{");
            File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), poll.Heartbeat);
            return new(poll.Exit, Noise, poll.TimedOut, Stderr: poll.Stderr ?? Noise);
        }
        Calls.Enqueue(new(args, directory, bound));
        if (args[3].EndsWith(".json", StringComparison.Ordinal))
            return new(FailStep == 1 && !TimeoutStep ? 1 : 0, Noise, FailStep == 1 && TimeoutStep, Stderr: Noise);
        if (args[0] == SshRemoteLaneExecutor.SshPath)
            return new(FailStep == 2 && !TimeoutStep ? 1 : 0, Noise, FailStep == 2 && TimeoutStep, Stderr: Noise);
        if (!FetchFailure || WritePartialFetch)
            foreach (var source in args.Skip(3).SkipLast(1))
            {
                var name = source.Split('/')[^1];
                var destination = args[^1] == "." ? name : args[^1];
                File.WriteAllText(Path.Combine(directory, destination), FetchContents.GetValueOrDefault(name, "trx-fixture"));
            }
        return new(FetchFailure ? 1 : 0, Noise, Stderr: FetchStderr ?? Noise);
    }
    internal async Task<SshRemoteLaneHandle> Submit()
    {
        var submission = await Executor().SubmitAsync(Request, default);
        Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        return Handle;
    }
    internal async Task<SshPollObservation> PollOnce(Poll poll)
    {
        await Polls.Writer.WriteAsync(poll);
        try { return await _observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: ssh poll completed"); }
    }
    internal static Dictionary<string, object?> Status(string state = "running") => new()
    {
        ["attemptId"] = Request.AttemptId, ["state"] = state, ["executorId"] = "reported-executor",
        ["lane"] = "reported-lane", ["filterHash"] = "reported-filter", ["commitSha"] = "reported-commit",
        ["treeSha"] = "reported-tree", ["mainSha"] = "reported-main", ["manifestIdentity"] = "reported-manifest",
        ["exitCode"] = 7, ["trx"] = new[] { "one.trx", "two.trx" }, ["error"] = "executor-checkout-failed"
    };
    public async ValueTask DisposeAsync()
    {
        if (Handle is not null) { Handle.Abandon(); await Handle.PollLoop; }
        Directory.Delete(Root, true);
    }
}
