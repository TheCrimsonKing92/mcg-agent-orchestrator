using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Harness = ExecutorSettingIntentScenarioTests.Harness;

// Parallel-safe: each test owns its workspace and immediate fake transport.
public sealed class ExecutorSettingRemoteSlotsSyncScenarioTests
{
    [Theory]
    [InlineData(5, 3)]
    [InlineData(2, 2)]
    public async Task RaiseSyncsBeforeHostAndLowerSyncsAfterHost(int requested, int hostAtCall)
    {
        using var fixture = new Harness();
        var original = fixture.FileBytes();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", requested.ToString());
        var remoteBackup = RemoteBackup(intent);
        var calls = 0;
        var sync = new RemoteExecutorSlotsFileSync((args, directory, bound, token) =>
        {
            calls++;
            Assert.Equal(hostAtCall, fixture.Load().Executors[0].Slots);
            Assert.Equal(fixture.Workspace.ExecutionDirectory, directory);
            Assert.Equal(TimeSpan.FromSeconds(30), bound);
            Assert.Equal(CancellationToken.None, token);
            Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                "admin-1", "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand" }, args[..^1]);
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            Assert.Contains("C:/mcg-executor", script);
            Assert.Contains("'slots.txt'", script);
            Assert.Contains("-Value '" + requested + "'", script);
            Assert.Contains(remoteBackup, script);
            return Task.FromResult(Success(requested, remoteBackup));
        }, fixture.Workspace.ExecutionDirectory, TimeSpan.FromSeconds(30));
        Tick(fixture, sync);
        Assert.Equal(1, calls);
        Assert.Equal(requested, fixture.Load().Executors[0].Slots);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Folder, fixture.BackupName(intent))));
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.EndsWith("; remote_slots_file=synced.", (await fixture.Intents.GetAsync(intent.Id))!.Outcome);
        Assert.EndsWith($" remote_slots_file=synced remote_backup={remoteBackup} remote_previous=3", Detail(fixture));
        var row = Assert.Single(Rows(fixture)).Value;
        Assert.Equal(new RemoteExecutorSlotsSyncRow(Harness.At, "mcg-exec-1", intent.Id, requested, "synced", null, remoteBackup, null), row);
        Assert.Single(File.ReadAllLines(Ledger(fixture)));
    }

    [Fact]
    public async Task FailedLowerKeepsLoweredHostAndAppliedIntent()
    {
        using var fixture = new Harness();
        var original = fixture.FileBytes();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2");
        var calls = 0;
        Tick(fixture, Sync(() =>
        {
            calls++;
            Assert.Equal(2, fixture.Load().Executors[0].Slots);
            return new(255, "ssh failed") { Stderr = "fixture stderr" };
        }));
        Assert.Equal(1, calls);
        Assert.Equal(2, fixture.Load().Executors[0].Slots);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Folder, fixture.BackupName(intent))));
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.EndsWith("; remote_slots_file=failed.", completed.Outcome);
        Assert.EndsWith(" remote_slots_file=failed remote_reason=unreachable", Detail(fixture));
        var row = Assert.Single(Rows(fixture)).Value;
        Assert.Equal("failed", row.Outcome);
        Assert.Equal("unreachable", row.Reason);
        Assert.Equal("fixture stderr", row.ErrorTail);
    }

    [Fact]
    public async Task FailedRaisePreservesHostAndFolderExceptLedgerAndAuditsRejection()
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "5");
        var original = fixture.FileBytes();
        var listing = fixture.Listing();
        var calls = 0;
        Tick(fixture, Sync(() => { calls++; return Success(3, RemoteBackup(intent)); }));
        Assert.Equal(1, calls);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(listing.Append(RemoteExecutorSlotsSyncLedger.FileName).Order(StringComparer.Ordinal), fixture.Listing());
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, completed.Status);
        Assert.Equal("Rejected executor-set-slots: remote-slots-sync-failed", completed.Outcome);
        Assert.Equal($"EXECUTOR_SETTING intent={intent.Id} verb=executor-set-slots field=executors[mcg-exec-1].slots old=3 new=5" +
            " changed=false backup=none actor=owner actor_kind=Agent remote_slots_file=failed remote_reason=readback-mismatch", Detail(fixture));
        Assert.Equal("readback-mismatch", Assert.Single(Rows(fixture)).Value.Reason);
    }

    [Fact]
    public async Task HostBackupCollisionRejectsBeforeAnyRemoteCall()
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "5");
        File.WriteAllText(Path.Combine(fixture.Folder, fixture.BackupName(intent)), "existing");
        var original = fixture.FileBytes();
        var listing = fixture.Listing();
        var calls = 0;
        Tick(fixture, Sync(() => { calls++; return Success(5, RemoteBackup(intent)); }));
        Assert.Equal(0, calls);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(listing, fixture.Listing());
        Assert.Empty(fixture.EventLines());
        Assert.Equal("Rejected executor-set-slots: executors-file-invalid", (await fixture.Intents.GetAsync(intent.Id))!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualRepairsRemoteWithoutHostWriteEvenWhenRepairFails(bool failed)
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "3");
        var original = fixture.FileBytes();
        var backups = Directory.GetFiles(fixture.Folder, "*.bak");
        var calls = 0;
        Tick(fixture, Sync(() =>
        {
            calls++;
            return failed ? new(255, "") : Success(3, RemoteBackup(intent), "drifted");
        }));
        Assert.Equal(1, calls);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(backups, Directory.GetFiles(fixture.Folder, "*.bak"));
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.Contains("replayed=True", completed.Outcome);
        Assert.EndsWith(failed ? "; remote_slots_file=failed." : "; remote_slots_file=synced.", completed.Outcome);
        Assert.EndsWith(failed ? " remote_slots_file=failed remote_reason=unreachable" :
            $" remote_slots_file=synced remote_backup={RemoteBackup(intent)} remote_previous=drifted", Detail(fixture));
    }

    [Fact]
    public async Task NonSshEntryDoesNotCallTransportOrWriteLedger()
    {
        using var fixture = new Harness();
        fixture.Write(Harness.Json.Replace("\"transport\": \"ssh\", ", "", StringComparison.Ordinal));
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "5");
        var calls = 0;
        Tick(fixture, Sync(() => { calls++; return Success(5, null); }));
        Assert.Equal(0, calls);
        Assert.False(File.Exists(Ledger(fixture)));
        Assert.Equal(5, fixture.Load().Executors[0].Slots);
        Assert.EndsWith(" remote_slots_file=not-applicable", Detail(fixture));
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.EndsWith("; remote_slots_file=not-applicable.", completed.Outcome);
    }

    [Fact]
    public async Task FocusedEvidenceMatchesTwoArgumentHandlerByteForByteAfterIdentityNormalization()
    {
        using var configured = new Harness();
        using var baseline = new Harness();
        var left = await configured.Queue("focused-evidence-set", "--mode", "off", "--sample-every", "4");
        var right = await baseline.Queue("focused-evidence-set", "--mode", "off", "--sample-every", "4");
        var calls = 0;
        Tick(configured, Sync(() => { calls++; return Success(5, null); }));
        baseline.Tick();
        Assert.Equal(0, calls);
        Assert.Equal(baseline.FileBytes(), configured.FileBytes());
        string Normalize(string value, OperatorIntentRecord intent) => value
            .Replace(intent.Id, "intent", StringComparison.Ordinal).Replace(intent.Id[..8], "prefix", StringComparison.Ordinal);
        Assert.Equal(baseline.Listing().Select(value => Normalize(value, right)), configured.Listing().Select(value => Normalize(value, left)));
        Assert.Equal(baseline.EventLines().Select(value => Normalize(EventDetail(value), right)),
            configured.EventLines().Select(value => Normalize(EventDetail(value), left)));
        Assert.Equal(Normalize((await baseline.Intents.GetAsync(right.Id))!.Outcome!, right),
            Normalize((await configured.Intents.GetAsync(left.Id))!.Outcome!, left));
        Assert.False(File.Exists(Ledger(configured)));
    }

    [Fact]
    public void ShippedWiringCarriesThirtySecondSync()
    {
        using var fixture = new Harness();
        var sync = OperatorIntentCoordinator.CreateDefault(fixture.Workspace).ExecutorSettings!.RemoteSync;
        Assert.NotNull(sync);
        Assert.Equal(TimeSpan.FromSeconds(30), sync.Bound);
    }

    private static string RemoteBackup(OperatorIntentRecord intent) => $"slots.txt.20261018T101112Z.{intent.Id[..8]}.bak";
    private static string Ledger(Harness fixture) => Path.Combine(fixture.Folder, RemoteExecutorSlotsSyncLedger.FileName);
    private static IReadOnlyDictionary<string, RemoteExecutorSlotsSyncRow> Rows(Harness fixture) =>
        RemoteExecutorSlotsSyncLedger.ReadLatestByExecutor(Ledger(fixture));
    private static string Detail(Harness fixture) => EventDetail(Assert.Single(fixture.EventLines()));
    private static string EventDetail(string line)
    {
        using var json = JsonDocument.Parse(line);
        Assert.Equal("executor-setting", json.RootElement.GetProperty("eventKind").GetString());
        return json.RootElement.GetProperty("detail").GetString()!;
    }
    private static GoalAcceptanceVerifier.CommandResult Success(int slots, string? backup, string previous = "3") =>
        new(0, JsonSerializer.Serialize(new { previous, backup, readback = slots.ToString() }));
    private static RemoteExecutorSlotsFileSync Sync(Func<GoalAcceptanceVerifier.CommandResult> result) =>
        new((_, _, _, _) => Task.FromResult(result()), "fixture", TimeSpan.FromSeconds(30));
    private static void Tick(Harness fixture, RemoteExecutorSlotsFileSync sync) =>
        Assert.Single(new OperatorIntentCoordinator(fixture.Intents, utcNow: () => Harness.At)
        {
            ExecutorSettings = new ExecutorSettingIntentHandler(fixture.Path, fixture.Workspace.ConductEventsLogPath, sync)
        }.ExecuteWorkspacePending(new AgentOrchestratorKernel()));
}
