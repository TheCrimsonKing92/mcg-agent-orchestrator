using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every host, kernel synchronization object and artifact belongs to the case root.
public sealed class DispatchProcessHostIntentAdmissionTests(Xunit.ITestOutputHelper output)
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
            Xunit.Assert.Equal(0, RunProbe(read, store));
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
            RecordProbeOutcome(parameters, store, exit);
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
            var path = Path.Combine(root, "parameters.json");
            DispatchProcessHost.WriteParameters(path, parameters);
            var read = DispatchProcessHost.ReadParameters(path);
            Xunit.Assert.Equal(parameters.TaskIntentAdmission, read.TaskIntentAdmission);
            File.WriteAllText(parameters.StartGatePath!, "go");
            Xunit.Assert.Equal(0, RunProbe(read, store));
            Xunit.Assert.Equal("started", File.ReadAllText(Path.Combine(root, "child-started")));
            Xunit.Assert.True(store.HasLaunch(parameters.StdoutPath));
            Xunit.Assert.Null(store.GetSuppression(parameters.StdoutPath));
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

    [Xunit.Fact]
    public void SuspendedChild_ModuleIdentityUnavailable_RecordsActualKernelIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateRoot();
        OwnedProcessGroup.SuspendedProcessStart? child = null;
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), root);
            var reference = store.CaptureAdmission("goal", "task");
            var executablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var process = TaskIntentAdmission.Launch(reference, "suspended-child", () =>
            {
                child = OwnedProcessGroup.StartSuspended(new System.Diagnostics.ProcessStartInfo(executablePath)
                    { UseShellExecute = false, CreateNoWindow = true });
                // The initial thread cannot initialize the loader until Resume. This forces
                // the missing-module condition without depending on scheduling or machine load.
                string? modulePath = null;
                var moduleFailure = Xunit.Record.Exception(() => modulePath = child.Process.MainModule?.FileName);
                Xunit.Assert.True(moduleFailure is null or System.ComponentModel.Win32Exception,
                    "Module enumeration failed outside the controlled loader boundary: " + moduleFailure);
                Xunit.Assert.Null(modulePath);
                // The native launch returns a GetProcessById wrapper with no managed start info.
                // Identity recording must succeed without either loader metadata or launch hints.
                Xunit.Assert.Throws<InvalidOperationException>(() => child.Process.StartInfo);
                return child.Process;
            });
            var identity = child!.ReadLifecycleIdentity(process.Id);
            Xunit.Assert.Equal(ProcessInspectionStatus.Available, identity.Status);
            Xunit.Assert.False(process.HasExited);
            Xunit.Assert.True(store.HasLaunch("suspended-child"));
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = reference.StorePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT process_id, process_started_at, process_path FROM task_intent_launches WHERE decision_key = 'suspended-child'";
            using var row = command.ExecuteReader();
            Xunit.Assert.True(row.Read());
            Xunit.Assert.Equal(process.Id, row.GetInt32(0));
            Xunit.Assert.Equal(identity.StartedAt!.Value.ToString("O"), row.GetString(1));
            Xunit.Assert.Equal(identity.ExecutablePath, row.GetString(2));
            Xunit.Assert.Equal(executablePath, row.GetString(2), ignoreCase: true);
            Xunit.Assert.Null(store.GetSuppression("suspended-child"));
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    // Close the kill-on-close job owned by StartSuspended, retaining the native
                    // process handle until its exit is observed. No PID is reopened for cleanup.
                    child.Group.Dispose();
                    Xunit.Assert.True(child.HasOwnedExitObservation);
                    Xunit.Assert.True(child.WaitForOwnedExit(TimeSpan.FromSeconds(120)), "Suspended child did not exit during cleanup.");
                }
                finally { child.Dispose(); }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("unavailable")]
    [Xunit.InlineData("reader-throws")]
    [Xunit.InlineData("wrong-pid")]
    [Xunit.InlineData("missing-image")]
    [Xunit.InlineData("missing-start")]
    public void RequiredIdentityFailure_ReapsBeforeTeardown_PublishesFailedExit(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateRoot();
        System.Diagnostics.Process? observedChild = null;
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), root);
            var parameters = Parameters(root, store.CaptureAdmission("goal", "task"));
            File.WriteAllText(parameters.StartGatePath!, "go");
            var reads = 0;
            ProcessInspectionStatus? nativeStatus = null;
            var exitedBeforeTeardown = false;
            var exit = DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
                new DispatchProcessHost.HeartbeatTestHooks
                {
                    BeforeTaskIntentAdmission = ConfigureProbeEnvironment,
                    ReadTaskIntentLaunchIdentity = process =>
                    {
                        reads++;
                        observedChild = System.Diagnostics.Process.GetProcessById(process.Id);
                        // Retain the exact process object after the host disposes its own handle.
                        var native = WindowsNativeProcessInspection.ReadLifecycleIdentity(process.Id, observedChild.SafeHandle);
                        nativeStatus = native.Status;
                        if (failure == "reader-throws") throw new InvalidOperationException("forced-identity-reader-failure");
                        var identity = failure == "unavailable" ? null : new SpawnProcessIdentity(
                            failure == "wrong-pid" ? -1 : process.Id,
                            failure == "missing-start" ? default : native.StartedAt!.Value,
                            failure == "missing-image" ? "" : native.ExecutablePath!);
                        return new(identity, 1, "forced-identity-" + failure);
                    },
                    TeardownPhase = phase =>
                    {
                        if (phase == "quiescing") exitedBeforeTeardown = observedChild?.HasExited == true;
                    }
                });
            RecordProbeOutcome(parameters, store, exit);
            Xunit.Assert.Equal(ProcessInspectionStatus.Available, nativeStatus);
            Xunit.Assert.Equal(1, reads);
            Xunit.Assert.Equal(1, exit);
            Xunit.Assert.True(exitedBeforeTeardown, "Unacknowledged child was still alive when host teardown began.");
            Xunit.Assert.True(DispatchExitArtifacts.TryRead(parameters.ExitCodePath, out var artifact));
            Xunit.Assert.Equal(1, artifact.ExitCode);
            Xunit.Assert.False(store.HasLaunch(parameters.StdoutPath));
            Xunit.Assert.Null(store.GetSuppression(parameters.StdoutPath));
            Xunit.Assert.False(File.Exists(Path.Combine(root, "child-started")));
            Xunit.Assert.Contains("forced-identity-", File.ReadAllText(parameters.StderrPath));
        }
        finally
        {
            observedChild?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private int RunProbe(DispatchProcessHost.DispatchRunParameters parameters, SqliteOperatorIntentStore store)
    {
        var exit = DispatchProcessHost.RunWithHeartbeatHooksForTests(parameters,
            new DispatchProcessHost.HeartbeatTestHooks { BeforeTaskIntentAdmission = ConfigureProbeEnvironment });
        RecordProbeOutcome(parameters, store, exit);
        return exit;
    }

    private void RecordProbeOutcome(DispatchProcessHost.DispatchRunParameters parameters, SqliteOperatorIntentStore store, int exit)
    {
        // Emit before teardown: failed assertions retain this output in the managed TRX receipt.
        output.WriteLine($"host-exit={exit}; launch={store.HasLaunch(parameters.StdoutPath)}; suppression={JsonSerializer.Serialize(store.GetSuppression(parameters.StdoutPath))}");
        foreach (var path in new[] { parameters.ExitCodePath, parameters.StdoutPath, parameters.StderrPath })
            output.WriteLine($"{Path.GetFileName(path)}: {(File.Exists(path) ? File.ReadAllText(path) : "<missing>")}");
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
