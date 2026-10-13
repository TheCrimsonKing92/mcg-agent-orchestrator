using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every host, kernel synchronization object and artifact belongs to the case root.
public sealed class DispatchProcessHostIntentAdmissionTests
{
    [Xunit.Fact]
    public async Task ReleasedGate_LateEnqueue_SuppressesActualChildLaunch()
    {
        var root = CreateRoot();
        using var reached = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        Task<int>? host = null;
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), root);
            var reference = store.CaptureAdmission("goal", "task");
            var parameters = Parameters(root, reference);
            File.WriteAllText(parameters.StartGatePath!, "go");
            host = Task.Run(() => DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
                new DispatchProcessHost.HeartbeatTestHooks { BeforeTaskIntentAdmission = start =>
                {
                    ConfigureProbeEnvironment(start);
                    reached.Set();
                    if (!resume.Wait(TimeSpan.FromSeconds(120))) throw new TimeoutException("Intent enqueue was not released at final child admission.");
                }}));
            Xunit.Assert.True(reached.Wait(TimeSpan.FromSeconds(120)), "Host did not reach final child admission after gate release.");
            var intent = await store.EnqueueAsync(Intent());
            resume.Set();
            Xunit.Assert.Equal(TaskIntentAdmission.SuppressedExitCode, await host.WaitAsync(TimeSpan.FromSeconds(120)));
            Xunit.Assert.False(File.Exists(Path.Combine(root, "child-started")));
            var receipt = store.GetSuppression(parameters.StdoutPath);
            Xunit.Assert.NotNull(receipt);
            Xunit.Assert.Equal(intent.Id, receipt.IntentId);
            Xunit.Assert.Equal("goal", receipt.GoalId);
            Xunit.Assert.Equal("task", receipt.TaskId);
            Xunit.Assert.Equal(0, receipt.PreparedVersion);
            Xunit.Assert.Equal(1, receipt.TaskVersion);
        }
        finally
        {
            resume.Set();
            if (host is not null) await host.WaitAsync(TimeSpan.FromSeconds(120));
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("goal", "other-task")]
    [Xunit.InlineData("other-goal", "task")]
    public async Task UnrelatedHold_ActualHost_StartsProbe(string goal, string task)
    {
        var root = CreateRoot();
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), root);
            await store.EnqueueAsync(Intent());
            var parameters = Parameters(root, store.CaptureAdmission(goal, task));
            File.WriteAllText(parameters.StartGatePath!, "go");
            var path = Path.Combine(root, "parameters.json");
            DispatchProcessHost.WriteParameters(path, parameters);
            var read = DispatchProcessHost.ReadParameters(path);
            Xunit.Assert.Equal(parameters.TaskIntentAdmission, read.TaskIntentAdmission);
            Xunit.Assert.Equal(0, DispatchProcessHost.RunWithHeartbeatHooksForTests(read,
                new DispatchProcessHost.HeartbeatTestHooks { BeforeTaskIntentAdmission = ConfigureProbeEnvironment }));
            Xunit.Assert.Equal("started", File.ReadAllText(Path.Combine(root, "child-started")));
            Xunit.Assert.Null(store.GetSuppression(parameters.StdoutPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void LaunchWins_EnqueueAfterAcknowledgement_PreservesActualStart()
    {
        var root = CreateRoot();
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), root);
            var parameters = Parameters(root, store.CaptureAdmission("goal", "task"));
            File.WriteAllText(parameters.StartGatePath!, "go");
            OperatorIntentRecord? persisted = null;
            var exit = DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
                new DispatchProcessHost.HeartbeatTestHooks
                {
                    BeforeTaskIntentAdmission = ConfigureProbeEnvironment,
                    AfterTaskIntentAdmission = worker =>
                    {
                        Xunit.Assert.True(store.HasLaunch(parameters.StdoutPath));
                        Xunit.Assert.False(worker.HasExited); // probe is gated on its still-open stdin
                        persisted = store.EnqueueAsync(Intent()).GetAwaiter().GetResult();
                    }
                });
            Xunit.Assert.Equal(0, exit);
            Xunit.Assert.NotNull(persisted);
            Xunit.Assert.Equal("started", File.ReadAllText(Path.Combine(root, "child-started")));
            Xunit.Assert.Null(store.GetSuppression(parameters.StdoutPath));
            Xunit.Assert.Contains("task", store.ListHeldTaskIds("goal"));
            Xunit.Assert.Equal(1, store.CaptureAdmission("goal", "task").Version);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task RejectedTargetIntent_FreshPreparation_StartsActualProbe()
    {
        var root = CreateRoot();
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), root);
            var intent = await store.EnqueueAsync(Intent());
            await store.ClaimNextAsync("goal", "owner");
            await store.CompleteAsync(intent.Id, "owner", OperatorIntentStatus.Rejected, "stale precondition", DateTimeOffset.UtcNow);
            var at = DateTimeOffset.UtcNow;
            store.BindPreparedAdmission(store.CaptureAdmission("goal", "task"), at);
            var parameters = Parameters(root, store.GetPreparedAdmission("goal", "task", at));
            File.WriteAllText(parameters.StartGatePath!, "go");
            Xunit.Assert.Equal(0, DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
                new DispatchProcessHost.HeartbeatTestHooks { BeforeTaskIntentAdmission = ConfigureProbeEnvironment }));
            Xunit.Assert.Equal("started", File.ReadAllText(Path.Combine(root, "child-started")));
            Xunit.Assert.Empty(store.ListHeldTaskIds("goal"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void MissingAdmissionAuthority_FailsBeforeActualChildLaunch()
    {
        var root = CreateRoot();
        try
        {
            var parameters = Parameters(root, new(Path.Combine(root, "missing.db"), "goal", "task", 0));
            File.WriteAllText(parameters.StartGatePath!, "go");
            var path = Path.Combine(root, "parameters.json");
            DispatchProcessHost.WriteParameters(path, parameters);
            Xunit.Assert.Equal(1, DispatchProcessHost.Run(path));
            Xunit.Assert.False(File.Exists(Path.Combine(root, "child-started")));
            Xunit.Assert.False(File.Exists(parameters.TaskIntentAdmission!.StorePath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-host-intent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static void ConfigureProbeEnvironment(System.Diagnostics.ProcessStartInfo start)
    {
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
    }
    private static OperatorIntentRecord Intent() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        OperatorIntentVerbs.Progress, "goal", "task", JsonSerializer.Serialize(new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "done")),
        [], "operator", "cli", "local-process", DateTimeOffset.UtcNow);
    private static DispatchProcessHost.DispatchRunParameters Parameters(string root, TaskIntentAdmissionReference reference) =>
        new("$null = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText('" + Path.Combine(root, "child-started").Replace("'", "''") + "','started')", root,
            Path.Combine(root, "probe.out.log"), Path.Combine(root, "probe.err.log"), Path.Combine(root, "probe.exit.txt"),
            Path.Combine(root, "probe.heartbeat.json"), false, TaskIntentAdmission: reference, StartGatePath: Path.Combine(root, "start-gate"));
}
