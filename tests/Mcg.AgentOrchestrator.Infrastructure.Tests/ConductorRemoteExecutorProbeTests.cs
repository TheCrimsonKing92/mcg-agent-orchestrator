using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorRemoteExecutorProbeTests
{
    [Xunit.Fact]
    public async Task EvaluateStartsOneBoundedBackgroundCallAndUsesInjectedTenMinuteCadence()
    {
        using var fixture = new Fixture();
        var called = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<GoalAcceptanceVerifier.CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var probe = fixture.Probe((args, directory, bound, token) =>
        {
            Assert.Equal(fixture.Root, directory);
            Assert.Equal(TimeSpan.FromSeconds(30), bound);
            Interlocked.Increment(ref count);
            called.TrySetResult(args);
            return pending.Task;
        });
        try
        {
            var evaluate = Task.Run(probe.Evaluate);
            var args = await Event(called.Task, "probe transport started");
            await Event(evaluate, "tick returned while transport remains blocked");
            Assert.False(probe.CurrentProbe!.IsCompleted);
            Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                "admin-one", "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand" }, args[..^1]);
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            Assert.Contains("schtasks /query /tn mcg-executor-lane", script);
            Assert.Contains("BatteryStatus", script);
            probe.Evaluate();
            Assert.Equal(1, count);
            pending.SetResult(Result("Ready", true) with { Stderr = new string('e', 2200) });
            await Event(probe.CurrentProbe, "probe completed");
            var row = Assert.Single(fixture.Rows());
            Assert.Equal("Ready", row.TaskState);
            Assert.True(row.PowerOnline);
            Assert.True(row.Reachable);
            Assert.Equal(0, row.ExitCode);
            Assert.False(row.TimedOut);
            Assert.Equal(2048, row.StderrTail.Length);
            Assert.Equal(fixture.Clock.GetUtcNow(), row.ObservedAt);
            probe.Evaluate();
            Assert.Equal(1, count);
            fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
            probe.Evaluate();
            Assert.Equal(1, count);
            fixture.Clock.Advance(TimeSpan.FromTicks(1));
            probe.Evaluate();
            await Event(probe.CurrentProbe!, "second probe completed");
            Assert.Equal(2, count);
            Assert.Equal(2, fixture.Rows().Count);
            Assert.Empty(fixture.Events());
        }
        finally
        {
            pending.TrySetResult(Result("Ready", true));
            if (probe.CurrentProbe is { } task) await Event(task, "probe cleanup");
        }
    }

    [Xunit.Fact]
    public async Task OccupiedExecutorIsSkippedInConfigurationOrder()
    {
        using var fixture = new Fixture(two: true);
        RemoteExecutorOccupancy.Claim(fixture.Root, "one", "gate");
        string? admin = null;
        var probe = fixture.Probe((args, _, _, _) =>
        { admin = args[5]; return Task.FromResult(Result("Running", true)); });
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "idle executor probe completed");
        Assert.Equal("admin-two", admin);
        var row = Assert.Single(fixture.Rows());
        Assert.Equal("two", row.ExecutorId);
        Assert.Equal("Running", row.TaskState);
        Assert.True(row.PowerOnline);
        Assert.Empty(fixture.Events());
    }

    [Xunit.Theory]
    [Xunit.InlineData(255, false, "{}", "unreachable")]
    [Xunit.InlineData(0, true, "{}", "unreachable")]
    [Xunit.InlineData(0, false, "", "unreachable")]
    [Xunit.InlineData(17, false, "{}", "probe-failed")]
    [Xunit.InlineData(0, false, "{", "probe-failed")]
    public async Task TransportFaultEvidenceDeterminesReason(int exit, bool timedOut, string output, string reason)
    {
        using var fixture = new Fixture();
        var probe = fixture.Probe((_, _, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(exit, output, timedOut)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "fault probe completed");
        Assert.Equal(new[] { reason }, Assert.Single(fixture.Rows()).Reasons);
        Assert.Contains("REMOTE_EXECUTOR_WARNING executor=one", Assert.Single(fixture.Events()));
        Assert.Contains("reasons=" + reason, Assert.Single(fixture.Events()));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "repeat fault probe completed");
        Assert.Single(fixture.Events());
        Assert.Equal(2, fixture.Rows().Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Disabled", true, "task-not-ready")]
    [Xunit.InlineData(null, true, "task-not-ready")]
    [Xunit.InlineData("Ready", false, "on-battery")]
    public async Task ConditionsWarnOnceAndRecoverWithHealthyRunningTask(string? state, bool power, string reason)
    {
        using var fixture = new Fixture();
        var result = Result(state, power);
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "condition probe completed");
        Assert.Equal(new[] { reason }, Assert.Single(fixture.Rows()).Reasons);
        Assert.Contains("reasons=" + reason, Assert.Single(fixture.Events()));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        result = Result("Running", true);
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "recovery probe completed");
        Assert.Equal(2, fixture.Events().Count);
        Assert.Equal("REMOTE_EXECUTOR_RECOVERED executor=one task_state=Running power_online=true", fixture.Events()[1]);
        Assert.Empty(fixture.Rows()[1].Reasons);
    }

    [Xunit.Fact]
    public async Task ChangedReasonSetWarnsAndRelaunchDoesNotRepeatPersistedWarning()
    {
        using var fixture = new Fixture();
        var result = Result("Ready", false);
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "battery probe completed");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        result = Result("Disabled", false);
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "changed condition completed");
        Assert.Equal(new[] { "task-not-ready", "on-battery" }, fixture.Rows()[1].Reasons);
        Assert.Contains("reasons=task-not-ready,on-battery", fixture.Events()[1]);
        var relaunched = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        relaunched.Evaluate();
        await Event(relaunched.CurrentProbe!, "relaunch probe completed");
        Assert.Equal(2, fixture.Events().Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ThrowingTransportChangesNoLedgerOrConditionState(bool asynchronous)
    {
        using var fixture = new Fixture();
        var fail = false;
        var probe = fixture.Probe((_, _, _, _) =>
        {
            if (!fail) return Task.FromResult(Result("Ready", false));
            if (asynchronous) return Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("fixture"));
            throw new IOException("fixture");
        });
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "initial warning completed");
        var bytes = File.ReadAllBytes(fixture.Ledger);
        fail = true;
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(Xunit.Record.Exception(probe.Evaluate));
        await Event(probe.CurrentProbe!, "throwing probe swallowed");
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Ledger));
        Assert.Single(fixture.Events());
        fail = false;
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "unchanged warning completed");
        Assert.Single(fixture.Events());
    }

    [Xunit.Fact]
    public void AdvisoryEventsClassifyAsDecisionAndOutcome()
    {
        Assert.Equal(ConductEventOperatorClassifier.Decision,
            ConductEventOperatorClassifier.Classify("remote-executor-health", "REMOTE_EXECUTOR_WARNING executor=one reasons=unreachable"));
        Assert.Equal(ConductEventOperatorClassifier.Outcome,
            ConductEventOperatorClassifier.Classify("remote-executor-health", "REMOTE_EXECUTOR_RECOVERED executor=one"));
        Assert.Null(ConductEventOperatorClassifier.Classify("remote-executor-health", "REMOTE_EXECUTOR_WARNING_FAKE"));
    }

    private static GoalAcceptanceVerifier.CommandResult Result(string? state, bool? power) => new(0,
        JsonSerializer.Serialize(new { task = state is null ? null : $"\"mcg-executor-lane\",\"N/A\",\"{state}\"", powerOnline = power }));
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name) { await Event((Task)task, name); return await task; }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string Configuration => Path.Combine(Root, "executors.json");
        internal string Ledger => Path.Combine(Root, ".orchestrator", RemoteExecutorProbeLedger.FileName);
        private string EventsPath => Path.Combine(Root, "events.log");
        internal Fixture(bool two = false)
        {
            var ids = two ? new[] { "one", "two" } : new[] { "one" };
            File.WriteAllText(Configuration, JsonSerializer.Serialize(new { executors = ids.Select(id => new {
                id, transport = "ssh", runnerAlias = "runner-" + id, adminAlias = "admin-" + id,
                remoteRepository = "C:/repo/bare.git" }), lanes = Array.Empty<string>() }));
        }
        internal ConductorRemoteExecutorProbe Probe(
            Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport) =>
            new(Root, Configuration, Clock, transport, RemoteExecutorOccupancy.DefaultStartTime,
                new ConductEventLogWriter(EventsPath));
        internal IReadOnlyList<RemoteExecutorProbeRow> Rows() => RemoteExecutorReportReader.ReadProbes(Ledger, out _);
        internal IReadOnlyList<string> Events() => File.Exists(EventsPath) ? File.ReadAllLines(EventsPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("remote-executor-health", json.RootElement.GetProperty("eventKind").GetString());
            return json.RootElement.GetProperty("detail").GetString()!;
        }).ToArray() : [];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
