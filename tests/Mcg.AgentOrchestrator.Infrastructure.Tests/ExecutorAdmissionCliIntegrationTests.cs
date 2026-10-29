using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AdmissionScenario = RemoteExecutorAdmissionTests.Scenario;

// Parallel-safe contract integration: real app entry and inbox, fixture-owned roots and
// allow-listed child environment. Placement uses existing injected transport, never SSH.
public sealed class ExecutorAdmissionCliIntegrationTests
{
    [Fact]
    public async Task RealEntry_DisableApplyResume_PreservesOwnedSettlementAndFutureOffers()
    {
        using var fixture = new Fixture();
        using var placement = new AdmissionScenario();
        var captured = fixture.Load();
        using var owned = placement.Coordinator(captured, root: fixture.Root);
        var entry = Assert.IsType<RemoteLaneExecutorEntry>(owned.TryClaimIdleExecutor());
        var release = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        placement.Executor.Submit = (_, _) => release.Task;
        var work = owned.RunRemoteAsync(AdmissionScenario.Lane, entry, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), default);
        try
        {
            var request = await RemoteExecutorAdmissionTests.Event(placement.Executor.Submitted.Task, "owned integration offer"); // Hang guard: owned offer.
            var before = File.ReadAllBytes(fixture.Path);
            var pending = await fixture.Submit(false, "disable");
            Assert.Equal(OperatorIntentStatus.Pending, pending.Status);
            Assert.Equal(before, File.ReadAllBytes(fixture.Path));
            Assert.Single(fixture.Tick());
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store().GetAsync(pending.Id))!.Status);
            Assert.False(Assert.Single(fixture.Load().Executors).AdmissionEnabled);
            using var disabled = placement.Coordinator(fixture.Load(), root: fixture.Root);
            Assert.Null(disabled.TryClaimIdleExecutor(out var reason));
            Assert.Equal("no-enabled-executor", reason);
            var handle = placement.CompletedHandle(request);
            release.SetResult(new(handle));
            var settled = await RemoteExecutorAdmissionTests.Event(work, "owned job settlement"); // Hang guard: existing receipt settled.
            Assert.NotNull(settled.Accepted);
            Assert.All(settled.Accepted!.TestResultPaths!, path => Assert.True(File.Exists(path)));
            Assert.Equal(1, handle.Abandons);
            Assert.Equal(0, handle.Cancellations);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(fixture.Root, "one"));
            var resume = await fixture.Submit(true, "resume");
            Assert.False(fixture.Load().Executors[0].AdmissionEnabled);
            Assert.Single(fixture.Tick());
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store().GetAsync(resume.Id))!.Status);
            Assert.Null(disabled.TryClaimIdleExecutor());
            using var future = placement.Coordinator(fixture.Load(), root: fixture.Root);
            var futureEntry = Assert.IsType<RemoteLaneExecutorEntry>(future.TryClaimIdleExecutor());
            placement.Executor.Submit = (offered, _) => Task.FromResult(new RemoteLaneSubmission(placement.CompletedHandle(offered)));
            Assert.NotNull((await future.RunRemoteAsync(AdmissionScenario.Lane, futureEntry, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), default)).Accepted);
            Assert.Equal(2, placement.Executor.Requests.Count);
            var rows = RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(fixture.Root));
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome));
            Assert.Equal(2, File.ReadAllLines(fixture.Workspace.ConductEventsLogPath).Length);
            var replay = await fixture.Submit(true, "resume", OperatorIntentStatus.Applied);
            Assert.Equal(resume.Id, replay.Id);
            Assert.Empty(fixture.Tick());
        }
        finally
        {
            release.TrySetResult(new(null, "fixture-cleanup"));
            await RemoteExecutorAdmissionTests.Event(work, "owned integration job drained");
        }
    }

    [Fact]
    public async Task RealEntry_InvalidInputConflictAndAuthority_DoNotMutate()
    {
        using var fixture = new Fixture();
        var bytes = File.ReadAllBytes(fixture.Path);
        foreach (var args in new[]
        {
            new[] { "executor-set-enabled", "one", "yes" },
            new[] { "executor-set-enabled", "one", " false " },
            new[] { "executor-set-enabled", "missing", "false" },
            new[] { "executor-set-enabled", "one", "false", "extra" }
        })
        {
            var result = await fixture.Run(args);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Error:", result.StandardError);
            Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        }
        Assert.Empty(await fixture.Store().ListForGoalAsync(OperatorIntentScopes.Workspace));
        var queued = await fixture.Submit(false, "conflict");
        var conflict = await fixture.Run("executor-set-enabled", "one", "true", "--idempotency-key", "conflict", "--operator-actor", "owner", "--actor-kind", "agent", "--json");
        Assert.NotEqual(0, conflict.ExitCode);
        Assert.Contains("idempotency", conflict.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        // Application-time existence is checked against current configuration, after real CLI enqueue.
        File.WriteAllText(fixture.Path, File.ReadAllText(fixture.Path).Replace("\"one\"", "\"replacement\""));
        var current = File.ReadAllBytes(fixture.Path);
        fixture.Tick();
        Assert.Equal("Rejected executor-set-enabled: executor-not-found", (await fixture.Store().GetAsync(queued.Id))!.Outcome);
        var rejected = await fixture.Store().EnqueueAsync(queued with
        {
            Id = Guid.NewGuid().ToString("N"), IdempotencyKey = "untrusted", AuthenticationAssurance = "untrusted",
            PayloadJson = "{\"executorId\":\"replacement\",\"enabled\":false}"
        });
        fixture.Tick();
        Assert.Equal("Rejected executor-set-enabled: tier-below-mutate", (await fixture.Store().GetAsync(rejected.Id))!.Outcome);
        Assert.Equal(current, File.ReadAllBytes(fixture.Path));
        Assert.False(File.Exists(fixture.Workspace.ConductEventsLogPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealInbox_RestartAtClaimOrAfterWrite_RecoversWithoutLostSetting(bool afterWrite)
    {
        using var fixture = new Fixture();
        var queued = await fixture.Submit(false, "restart");
        var claimed = (await fixture.Store().ClaimNextAsync(OperatorIntentScopes.Workspace, OperatorIntentCoordinator.ClaimOwner))!;
        Assert.Equal(queued.Id, claimed.Id);
        if (afterWrite) fixture.Handler().Apply(claimed, Fixture.At);
        Assert.Single(fixture.Tick()); // Fresh store, coordinator and handler.
        var applied = (await fixture.Store().GetAsync(queued.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, applied.Status);
        Assert.False(fixture.Load().Executors[0].AdmissionEnabled);
        var bytes = File.ReadAllBytes(fixture.Path);
        var events = File.ReadAllLines(fixture.Workspace.ConductEventsLogPath);
        Assert.Empty(fixture.Tick());
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        Assert.Equal(events, File.ReadAllLines(fixture.Workspace.ConductEventsLogPath));
    }

    [Fact]
    public async Task RealInbox_WriteFailure_RejectsAndRetainsOriginalBytes()
    {
        using var fixture = new Fixture();
        var pending = await fixture.Submit(false, "failure");
        var backup = fixture.Path + ".20261018T101112Z." + pending.Id[..8] + ".bak";
        File.WriteAllText(backup, "preexisting");
        var original = File.ReadAllBytes(fixture.Path);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store().GetAsync(pending.Id))!.Status);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Path)!, "*.tmp"));
        Assert.False(File.Exists(fixture.Workspace.ConductEventsLogPath));
    }

    private sealed class Fixture : IDisposable
    {
        internal static DateTimeOffset At => ExecutorSettingIntentScenarioTests.Harness.At;
        internal string Root { get; } = Directory.CreateTempSubdirectory("admission-cli-").FullName;
        internal OrchestratorWorkspace Workspace { get; }
        internal string Path => RemoteLaneExecutorConfiguration.ResolveStorePath(Root);
        internal Fixture()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Root, ".git"));
            Workspace = OrchestratorWorkspace.ForDirectory(Root, null, null,
                new OrchestratorProjectRegistry(System.IO.Path.Combine(Root, "registry"), System.IO.Path.Combine(Root, "data")));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, "{\"executors\":[{\"id\":\"one\",\"slots\":1}],\"lanes\":[\"infrastructure tests: A\"]}");
        }
        internal RemoteLaneExecutorConfiguration Load() => RemoteLaneExecutorConfiguration.Load(Path);
        internal SqliteOperatorIntentStore Store() => SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        internal ExecutorSettingIntentHandler Handler() => new(Path, Workspace.ConductEventsLogPath);
        internal IReadOnlyList<string> Tick() => new OperatorIntentCoordinator(Store(), utcNow: () => At)
        { ExecutorSettings = Handler() }.ExecuteWorkspacePending(new AgentOrchestratorKernel());
        internal async Task<OperatorIntentRecord> Submit(bool enabled, string key, OperatorIntentStatus expected = OperatorIntentStatus.Pending)
        {
            var result = await Run("executor-set-enabled", "one", enabled ? "true" : "false", "--idempotency-key", key,
                "--operator-actor", "owner", "--actor-kind", "agent", "--json");
            Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(expected.ToString(), payload.GetProperty("status").GetString());
            return (await Store().GetAsync(payload.GetProperty("id").GetString()!))!;
        }
        internal Task<CliChildProcessResult> Run(params string[] command)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "ProgramFiles", "USERPROFILE", "LOCALAPPDATA" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = System.IO.Path.Combine(Root, "registry");
            start.Environment["MCG_ORCHESTRATOR_DATA"] = System.IO.Path.Combine(Root, "data");
            start.Environment["MCG_ORCHESTRATOR_HOME"] = System.IO.Path.Combine(Root, "home");
            start.Environment["MCG_DOTNET_ISOLATED_ROOT"] = System.IO.Path.Combine(Root, "dotnet");
            start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
            start.ArgumentList.Add(typeof(OrchestratorWorkspace).Assembly.Location);
            foreach (var arg in command.Concat(["--repository-root", Root, "--expected-state-directory", Workspace.OrchestratorDirectory, "--disposable-fixture"]))
                start.ArgumentList.Add(arg);
            return CliChildProcessRunner.RunAsync(start);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
