using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.JobAccounting)]
public sealed class AcceptanceCheckProcessInvokerEquivalenceTests
{
    // Baseline contract from main b3d3a30b9: running -> completed, exit 0, unchanged stdout;
    // focused capture-limit stop: exit -1, CaptureLimited true, TimedOut false, one marker.
    private const string BaselineStdout = "acceptance-process-equivalence";
    private const long CapBytes = 4096;
    private static readonly TimeSpan EventFailsafe = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Heartbeat_capture_and_cleanup_match_the_baseline_contract()
    {
        var root = Directory.CreateTempSubdirectory("mcg-invoker-equivalence-").FullName;
        var heartbeatPath = Path.Combine(root, "heartbeat.json");
        var releaseName = $"Local\\mcg-invoker-release-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(root, "heartbeat.json");
        var running = new TaskCompletionSource<GateHeartbeatSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new List<string>();
        string? capturedStdout = null;
        GoalAcceptanceVerifier.CommandResult? result = null;
        Task<(GoalAcceptanceVerifier.CommandResult Result, string HeartbeatPath)>? run = null;

        void ObserveHeartbeat()
        {
            if (running.Task.IsCompleted) return;
            try
            {
                var snapshot = ReadHeartbeat(heartbeatPath);
                if (snapshot.State == "running") running.TrySetResult(snapshot);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A subsequent file event/beat observes the atomic heartbeat publication.
            }
        }

        watcher.Created += (_, _) => ObserveHeartbeat();
        watcher.Changed += (_, _) => ObserveHeartbeat();
        watcher.Renamed += (_, _) => ObserveHeartbeat();
        watcher.Error += (_, args) => running.TrySetException(args.GetException());
        watcher.EnableRaisingEvents = true;
        try
        {
            // The child cannot exit until the test has observed the running heartbeat file.
            run = GoalAcceptanceVerifier.RunProcessWithHeartbeatForTestsAsync(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                    $"$gate = [System.Threading.EventWaitHandle]::OpenExisting('{releaseName}'); " +
                    $"try {{ [void]$gate.WaitOne(); [Console]::WriteLine('{BaselineStdout}') }} finally {{ $gate.Dispose() }}"],
                root, TimeSpan.FromMinutes(5), heartbeatPath,
                observation =>
                {
                    stages.Add(observation.Stage);
                    if (observation.Stage == "heartbeat-final")
                        capturedStdout = File.ReadAllText(ReadHeartbeat(heartbeatPath).StdoutPath!);
                },
                cancellationToken: cancellation.Token, registrationIdentityReader: null);

            var first = await AwaitEventAsync(running.Task, "running heartbeat file publication");
            release.Set();
            result = (await AwaitEventAsync(run, "heartbeat child exit and cleanup")).Result;
            var final = ReadHeartbeat(heartbeatPath);

            Assert.Equal(new[] { "running", "completed" }, new[] { first.State, final.State });
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, final.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.CaptureLimited);
            Assert.Equal(BaselineStdout, result.Output);
            Assert.Equal(BaselineStdout + Environment.NewLine, capturedStdout);
            Assert.Equal(new[] { "started", "heartbeat-final", "registration-released", "owned-child-disposed" }, stages);
        }
        finally
        {
            release.Set();
            await cancellation.CancelAsync();
            if (run is not null)
            {
                try { result ??= (await AwaitEventAsync(run, "heartbeat child cancellation cleanup")).Result; }
                catch (OperationCanceledException) { }
            }
            watcher.EnableRaisingEvents = false;
            DeleteCaptureFiles(result);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capture_limit_stop_matches_the_baseline_contract()
    {
        using var cancellation = new CancellationTokenSource();
        GoalAcceptanceVerifier.CommandResult? result = null;
        var run = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                "while ($true) { [Console]::WriteLine('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx') }"],
            Path.GetTempPath(), TimeSpan.FromMinutes(5), CapBytes,
            GateHeartbeatRunClass.FocusedEvidence, cancellationToken: cancellation.Token);
        try
        {
            result = await AwaitEventAsync(run, "capture-limit stop and child cleanup");

            Assert.Equal(-1, result.ExitCode);
            Assert.True(result.CaptureLimited);
            Assert.False(result.TimedOut);
            Assert.Equal(CapBytes, result.CaptureLimitBytes);
            Assert.Equal(1, result.Output.Split("ACCEPTANCE_CAPTURE_LIMIT_REACHED", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { result ??= await AwaitEventAsync(run, "capture-limited child cancellation cleanup"); }
            catch (OperationCanceledException) { }
            DeleteCaptureFiles(result);
        }
    }

    private static GateHeartbeatSnapshot ReadHeartbeat(string path)
    {
        // Reading must permit the production writer to atomically replace the heartbeat file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<GateHeartbeatSnapshot>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Heartbeat snapshot is missing: {path}");
    }

    private static async Task<T> AwaitEventAsync<T>(Task<T> task, string expectedEvent)
    {
        try { return await task.WaitAsync(EventFailsafe); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Expected event did not occur: {expectedEvent}.", ex);
        }
    }

    private static void DeleteCaptureFiles(GoalAcceptanceVerifier.CommandResult? result)
    {
        if (result?.StdoutPath is { } stdout) File.Delete(stdout);
        if (result?.StderrPath is { } stderr) File.Delete(stderr);
    }
}
