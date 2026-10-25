using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private directories, injected process enumeration/setters and async-flow switch overrides.
public sealed class BuildLaunchQosSweepTests
{
    [Fact]
    public async Task Begin_Enabled_DeduplicatesCapsAndLogsFinalCounts()
    {
        using var fixture = new Fixture(true);
        using var scope = AcceptanceChildQos.PushForTests(false);
        var calls = new ConcurrentQueue<(int Pid, uint Control, uint State)>();
        var thirdPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = 0;
        var largePass = Enumerable.Range(1000, 300).ToArray();
        using var sweep = BuildLaunchQosSweep.Begin("fixture", fixture.StatePath, "goal", 100,
            _ =>
            {
                var pass = Interlocked.Increment(ref passes);
                if (pass == 1) return [10, 11];
                if (pass == 2) return [11, 12];
                thirdPass.TrySetResult();
                return largePass;
            },
            (pid, control, state) => { calls.Enqueue((pid, control, state)); return true; },
            TimeSpan.FromMilliseconds(10));
        Assert.NotNull(sweep);
        Assert.Equal((100, 1u, 0u), calls.First()); // Root is set synchronously, before any member pass.
        try { await thirdPass.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new TimeoutException("QoS sweep never reached its third member pass."); }
        sweep.Dispose();
        var recorded = calls.ToArray();
        sweep.Dispose();
        Assert.Equal(recorded, calls.ToArray());
        Assert.False(AcceptanceChildQos.Enabled);
        Assert.Equal(recorded.Length, recorded.Select(call => call.Pid).Distinct().Count());
        Assert.All(recorded, call => Assert.Equal((1u, 0u), (call.Control, call.State)));
        Assert.Equal(new[] { 10, 11, 12, 100 }.Concat(Enumerable.Range(1000, 256)),
            recorded.Select(call => call.Pid).Order());
        Assert.Equal(256, recorded.Count(call => call.Pid >= 1000));
        fixture.AssertEvent("fixture", "goal", recorded.Length, recorded.Length);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("unreadable")]
    public void Begin_DisabledOrUnavailablePolicy_HasNoSideEffects(string policy)
    {
        using var fixture = new Fixture(false);
        using var scope = AcceptanceChildQos.PushForTests(true);
        var path = fixture.PolicyPath;
        if (policy == "missing") File.Delete(path);
        if (policy == "invalid") File.WriteAllText(path, "not json");
        if (policy == "null")
        {
            var json = File.ReadAllText(path).Replace("\"acceptanceChildrenOptOutExecutionThrottling\": false",
                "\"acceptanceChildrenOptOutExecutionThrottling\": null", StringComparison.Ordinal);
            Assert.Contains("\"acceptanceChildrenOptOutExecutionThrottling\": null", json, StringComparison.Ordinal);
            Assert.False(ConductorAutonomyPolicy.ParseJson(json).AcceptanceChildrenOptOutExecutionThrottling);
            File.WriteAllText(path, json);
        }
        // Deny sharing while the enabled policy is read, without changing file permissions.
        if (policy == "unreadable") fixture.WritePolicy(true);
        using var locked = policy == "unreadable" ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var enumerations = 0;
        var setters = 0;
        using var sweep = BuildLaunchQosSweep.Begin("fixture", fixture.StatePath, "goal", 100,
            _ => { enumerations++; return [101]; }, (_, _, _) => { setters++; return true; });
        Assert.Null(sweep);
        Assert.Equal(0, enumerations);
        Assert.Equal(0, setters);
        Assert.False(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void ExplicitSweep_Failures_CountAttemptsWithoutRetrying()
    {
        using var scope = AcceptanceChildQos.PushForTests(false);
        var calls = new List<int>();
        var pass = 0;
        using var sweep = AcceptanceChildQos.StartSweep(true, 100, Timeout.InfiniteTimeSpan,
            _ => ++pass switch
            {
                1 => throw new IOException("Enumeration unavailable."),
                2 => null,
                _ => [100, 101, 102, 103]
            },
            (pid, _, _) =>
            {
                calls.Add(pid);
                if (pid == 101) throw new IOException("Setter unavailable.");
                return pid != 102;
            });
        Assert.NotNull(sweep);
        Assert.Equal(100, Assert.Single(calls));
        for (var index = 0; index < 4; index++) sweep.RunPass();
        sweep.Dispose();
        sweep.RunPass();
        Assert.Equal([100, 101, 102, 103], calls);
        Assert.Equal(4, sweep.TriedCount);
        Assert.Equal(2, sweep.OptedOutCount);
        Assert.False(AcceptanceChildQos.Enabled);
        Assert.Null(AcceptanceChildQos.StartSweep(false, 100, TimeSpan.Zero,
            _ => throw new InvalidOperationException("Disabled sweep enumerated."),
            (_, _, _) => throw new InvalidOperationException("Disabled sweep set QoS.")));
    }

    [Fact]
    public void RunLocalWithExecutionQos_Enabled_SweepsUntilBuildReturns()
    {
        using var fixture = new Fixture(true);
        var calls = new ConcurrentQueue<(int Pid, uint Control, uint State)>();
        using var secondPass = new ManualResetEventSlim();
        var passes = 0;
        var builds = 0;
        var operation = fixture.Operation();
        var result = CompletionBuildPlacement.RunLocalWithExecutionQos(operation, fixture.StatePath,
            fixture.ReceiptPath, TimeSpan.FromMinutes(1), () =>
            {
                builds++;
                if (!secondPass.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Completion QoS sweep never reached its second member pass.");
                return new WorkerProcessRunResult(1, "FAIL build: 1 error(s)", "compiler error");
            },
            root =>
            {
                if (Interlocked.Increment(ref passes) == 1) return [root, 501, 502];
                secondPass.Set();
                return [root, 502, 503];
            },
            (pid, control, state) => { calls.Enqueue((pid, control, state)); return true; },
            TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, builds);
        Assert.Equal(1, result.BuildExitCode);
        Assert.Equal(CompletionBuildEvidencePhase.CompilerFailure, result.Phase);
        var recorded = calls.ToArray();
        Assert.Equal(new[] { Environment.ProcessId, 501, 502, 503 }.Order(),
            recorded.Select(call => call.Pid).Order());
        Assert.All(recorded, call => Assert.Equal((1u, 0u), (call.Control, call.State)));
        fixture.AssertEvent("completion-build", operation.GoalId, 4, 4);
    }

    [Fact]
    public void RunLocalWithExecutionQos_Disabled_PreservesLocalResult()
    {
        using var fixture = new Fixture(false);
        var enumerations = 0;
        var setters = 0;
        var builds = 0;
        WorkerProcessRunResult Build() { builds++; return new(1, "FAIL build: 1 error(s)", "compiler error"); }
        var operation = fixture.Operation();
        var expected = CompletionBuildPlacement.RunLocal(operation, fixture.ReceiptPath, TimeSpan.FromMinutes(1), Build);
        var actual = CompletionBuildPlacement.RunLocalWithExecutionQos(operation, fixture.StatePath,
            fixture.ReceiptPath, TimeSpan.FromMinutes(1), Build,
            _ => { enumerations++; return [501]; }, (_, _, _) => { setters++; return true; });
        Assert.Equal(2, builds);
        Assert.Equal(expected, actual);
        Assert.Equal(0, enumerations);
        Assert.Equal(0, setters);
        Assert.False(File.Exists(fixture.LogPath));
    }

    [Fact]
    public void Dispose_UnwritableLog_DoesNotChangeBuildResult()
    {
        using var fixture = new Fixture(true);
        File.WriteAllText(Path.GetDirectoryName(fixture.LogPath)!, "blocks log directory creation");
        var calls = 0;
        var result = CompletionBuildPlacement.RunLocalWithExecutionQos(fixture.Operation(), fixture.StatePath,
            fixture.ReceiptPath, TimeSpan.FromMinutes(1), () => new(1, "FAIL build: 1 error(s)", ""),
            _ => null, (_, _, _) => { calls++; throw new IOException("Setter unavailable."); });
        Assert.Equal(1, calls);
        Assert.Equal(1, result.BuildExitCode);
        Assert.Equal(CompletionBuildEvidencePhase.CompilerFailure, result.Phase);
        Assert.False(File.Exists(fixture.LogPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "build-qos-" + Guid.NewGuid().ToString("N"));
        internal string StatePath => Path.Combine(root, "state.db");
        internal string PolicyPath => Path.Combine(root, "conductor-policy.json");
        internal string ReceiptPath => Path.Combine(root, "receipt.json");
        internal string LogPath => Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);

        internal Fixture(bool enabled) { Directory.CreateDirectory(root); WritePolicy(enabled); }
        internal void WritePolicy(bool enabled) => File.WriteAllText(PolicyPath,
            (ConductorAutonomyPolicy.Conservative with { AcceptanceChildrenOptOutExecutionThrottling = enabled }).ToJson());

        internal CompletionBuildEvidenceOperation Operation() => new("operation", "goal", "task",
            DateTimeOffset.UnixEpoch, "dispatch", root, "tree", ["project.csproj"],
            new(1, "build", root, "stdout", "stderr", "exit", DateTimeOffset.UnixEpoch, null, null),
            DateTimeOffset.MaxValue, CompletionBuildEvidencePhase.Pending, StatePath, root,
            Path.Combine(root, "result.json"), 1, DateTimeOffset.UnixEpoch);

        internal void AssertEvent(string kind, string goal, int optedOut, int tried)
        {
            using var line = JsonDocument.Parse(Assert.Single(File.ReadAllLines(LogPath)));
            Assert.Equal("build-qos", line.RootElement.GetProperty("eventKind").GetString());
            Assert.Equal(goal, line.RootElement.GetProperty("goalId").GetString());
            Assert.Equal($"BUILD_QOS kind={kind} opted_out={optedOut} tried={tried}",
                line.RootElement.GetProperty("detail").GetString());
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
