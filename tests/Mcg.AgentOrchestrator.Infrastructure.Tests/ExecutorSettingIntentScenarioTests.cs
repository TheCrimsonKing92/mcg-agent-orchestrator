using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test workspace, SQLite files and logs; clocks are injected.
public sealed class ExecutorSettingIntentScenarioTests
{
    [Fact]
    public async Task Slots_SubmitThenTick_ChangesOnlyTargetDigitAndAuditsBackup()
    {
        using var fixture = new Harness();
        var original = File.ReadAllBytes(fixture.Path);
        var before = fixture.Load();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2");
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Equal(Harness.At, intent.CreatedAt);
        Assert.Equal("owner", intent.Actor);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        var oldEvents = fixture.EventLines();
        Assert.Single(fixture.Tick());
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        var backup = fixture.BackupName(intent);
        Assert.Equal($"Applied executor-set-slots; tier=Mutate; replayed=False; changes=executors[mcg-exec-1].slots:3->2; backup={backup}.", completed.Outcome);
        var expected = Encoding.UTF8.GetBytes(Harness.Json.Replace("\"slots\": 3", "\"slots\": 2", StringComparison.Ordinal));
        var actual = File.ReadAllBytes(fixture.Path);
        Assert.Equal(expected, actual);
        Assert.Single(original.Zip(actual).Where(pair => pair.First != pair.Second));
        Assert.Equal(original, File.ReadAllBytes(System.IO.Path.Combine(fixture.Folder, backup)));
        Assert.Equal(new[] { 2, 2 }, fixture.Load().Executors.Select(e => e.Slots));
        AssertEquivalent(before, fixture.Load(), "mcg-exec-1", 2);
        var line = Assert.Single(fixture.EventLines().Skip(oldEvents.Length));
        AssertEvent(line, intent, "executors[mcg-exec-1].slots", "3", "2", true, backup, slots: true);
    }

    [Fact]
    public async Task FocusedSettings_TwoFields_UsesOneBackupAndOrderedEvents()
    {
        using var fixture = new Harness();
        var before = fixture.Load();
        var original = File.ReadAllBytes(fixture.Path);
        var intent = await fixture.Queue("focused-evidence-set", "--mode", "off", "--sample-every", "4");
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        fixture.Tick();
        var backup = fixture.BackupName(intent);
        Assert.Equal($"Applied focused-evidence-set; tier=Mutate; replayed=False; changes=focusedEvidence.mode:shadow->off,focusedEvidence.sampleEvery:1->4; backup={backup}.",
            (await fixture.Intents.GetAsync(intent.Id))!.Outcome);
        Assert.Single(Directory.GetFiles(fixture.Folder, "*.bak"));
        Assert.Equal(original, File.ReadAllBytes(System.IO.Path.Combine(fixture.Folder, backup)));
        Assert.Equal(Encoding.UTF8.GetBytes(Harness.Json.Replace("\"mode\": \"shadow\"", "\"mode\": \"off\"", StringComparison.Ordinal)
            .Replace("\"sampleEvery\": 1", "\"sampleEvery\": 4", StringComparison.Ordinal)), File.ReadAllBytes(fixture.Path));
        AssertEquivalent(before, fixture.Load(), mode: "off", sample: 4);
        Assert.Equal(300, fixture.Load().FocusedEvidence.GraceSeconds);
        var events = fixture.EventLines();
        Assert.Equal(2, events.Length);
        AssertEvent(events[0], intent, "focusedEvidence.mode", "shadow", "off", true, backup);
        AssertEvent(events[1], intent, "focusedEvidence.sampleEvery", "1", "4", true, backup);
    }

    [Fact]
    public async Task Slots_CurrentValue_LeavesFileAndFolderUntouchedButAuditsNoop()
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "3");
        var original = File.ReadAllBytes(fixture.Path);
        var listing = fixture.Listing();
        fixture.Tick();
        Assert.Equal("Applied executor-set-slots; tier=Mutate; replayed=True; changes=executors[mcg-exec-1].slots:3->3; backup=none.",
            (await fixture.Intents.GetAsync(intent.Id))!.Outcome);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Equal(listing, fixture.Listing());
        AssertEvent(Assert.Single(fixture.EventLines()), intent, "executors[mcg-exec-1].slots", "3", "3", false, "none", slots: true);
    }

    [Theory]
    [InlineData("slots-zero", "slots-out-of-range")]
    [InlineData("slots-high", "slots-out-of-range")]
    [InlineData("unknown", "executor-not-found")]
    [InlineData("slots-absent", "slots-absent")]
    [InlineData("sample-absent", "sample-every-absent")]
    [InlineData("mode-absent", "focused-evidence-mode-absent")]
    [InlineData("remote-first", "focused-evidence-mode-remote-first")]
    [InlineData("malformed-file", "executors-file-invalid")]
    [InlineData("missing-file", "executors-file-missing")]
    [InlineData("steward", "steward-capability-boundary")]
    [InlineData("unavailable", "Executor setting intent services are unavailable.")]
    [InlineData("tier", "tier-below-mutate")]
    [InlineData("payload", "invalid-payload")]
    [InlineData("sample-zero", "sample-every-out-of-range")]
    [InlineData("mode-unsupported", "mode-unsupported")]
    [InlineData("no-setting", "no-setting")]
    public async Task Apply_Refusal_PreservesFileFolderAndConductLog(string scenario, string reason)
    {
        using var fixture = new Harness();
        object payload = new ExecutorSetSlotsOperatorIntentPayload("mcg-exec-1", 2);
        var verb = OperatorIntentVerbs.ExecutorSetSlots;
        var assurance = "local-process";
        switch (scenario)
        {
            case "slots-zero": payload = new ExecutorSetSlotsOperatorIntentPayload("mcg-exec-1", 0); break;
            case "slots-high": payload = new ExecutorSetSlotsOperatorIntentPayload("mcg-exec-1", 17); break;
            case "unknown": payload = new ExecutorSetSlotsOperatorIntentPayload("no-such", 2); break;
            case "slots-absent": fixture.Write(Harness.Json.Replace("\"slots\": 3, ", "", StringComparison.Ordinal)); break;
            case "sample-absent": fixture.Write(Harness.Json.Replace("\"sampleEvery\": 1, ", "", StringComparison.Ordinal)); goto case "sample";
            case "sample-zero": payload = new FocusedEvidenceSetOperatorIntentPayload(null, 0); verb = OperatorIntentVerbs.FocusedEvidenceSet; break;
            case "sample": payload = new FocusedEvidenceSetOperatorIntentPayload(null, 4); verb = OperatorIntentVerbs.FocusedEvidenceSet; break;
            case "mode-absent": fixture.Write(Harness.Json.Replace("\"mode\": \"shadow\", ", "", StringComparison.Ordinal)); goto case "mode";
            case "remote-first": fixture.Write(Harness.Json.Replace("\"shadow\"", "\"remote-first\"", StringComparison.Ordinal)); goto case "mode";
            case "mode": payload = new FocusedEvidenceSetOperatorIntentPayload("off", null); verb = OperatorIntentVerbs.FocusedEvidenceSet; break;
            case "mode-unsupported": payload = new FocusedEvidenceSetOperatorIntentPayload("remote-first", null); verb = OperatorIntentVerbs.FocusedEvidenceSet; break;
            case "no-setting": payload = new FocusedEvidenceSetOperatorIntentPayload(null, null); verb = OperatorIntentVerbs.FocusedEvidenceSet; break;
            case "malformed-file": fixture.Write("{"); break;
            case "missing-file": File.Delete(fixture.Path); break;
            case "steward": assurance = OperatorIntentAdjudication.StewardAssurance; break;
            case "tier": assurance = "untrusted"; break;
        }
        var intent = await fixture.Enqueue(verb, scenario == "payload" ? "{" : JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options), assurance);
        var bytes = fixture.FileBytes();
        var listing = fixture.Listing();
        var log = fixture.LogBytes();
        fixture.Tick(scenario != "unavailable");
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, completed.Status);
        Assert.Equal($"Rejected {verb}: {reason}", completed.Outcome);
        Assert.Equal(bytes, fixture.FileBytes());
        Assert.Equal(listing, fixture.Listing());
        Assert.Equal(log, fixture.LogBytes());
    }

    [Theory]
    [InlineData("remote-first", null, 4)]
    [InlineData("shadow", "shadow", 4)]
    [InlineData("shadow", "off", null)]
    public async Task FocusedSettings_PartialChange_PreservesUnrequestedValues(
        string currentMode, string? mode, int? sample)
    {
        using var fixture = new Harness();
        fixture.Write(Harness.Json.Replace("\"shadow\"", $"\"{currentMode}\"", StringComparison.Ordinal));
        var before = fixture.Load();
        var intent = await fixture.Enqueue(OperatorIntentVerbs.FocusedEvidenceSet,
            JsonSerializer.Serialize(new FocusedEvidenceSetOperatorIntentPayload(mode, sample), OperatorIntentJson.Options));
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        AssertEquivalent(before, fixture.Load(), mode: mode, sample: sample);
        Assert.Equal((mode is null ? 0 : 1) + (sample is null ? 0 : 1), fixture.EventLines().Length);
        if (mode == currentMode)
            AssertEvent(fixture.EventLines()[0], intent, "focusedEvidence.mode", currentMode, mode, false, fixture.BackupName(intent));
    }

    [Fact]
    public async Task BackupCollision_RejectsAndRemovesTemporaryFile()
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2");
        var backup = System.IO.Path.Combine(fixture.Folder, fixture.BackupName(intent));
        File.WriteAllText(backup, "existing backup");
        var listing = fixture.Listing();
        var original = fixture.FileBytes();
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(listing, fixture.Listing());
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal("existing backup", File.ReadAllText(backup));
        Assert.Empty(fixture.EventLines());
    }

    internal static void AssertEquivalent(RemoteLaneExecutorConfiguration before, RemoteLaneExecutorConfiguration after,
        string? executorId = null, int? slots = null, string? mode = null, int? sample = null)
    {
        Assert.True(before.Enabled);
        Assert.True(after.Enabled);
        Assert.Equal(before.Executors.Select(e => e.Id == executorId ? e with { Slots = slots!.Value } : e).ToArray(), after.Executors.ToArray());
        Assert.Equal(before.Lanes.ToArray(), after.Lanes.ToArray());
        Assert.Equal(before.MachineLocalResourceKeys.ToArray(), after.MachineLocalResourceKeys.ToArray());
        Assert.Equal(before.AllInfrastructureLanes, after.AllInfrastructureLanes);
        Assert.Equal(before.FocusedEvidence with { Mode = mode ?? before.FocusedEvidence.Mode, SampleEvery = sample ?? before.FocusedEvidence.SampleEvery }, after.FocusedEvidence);
        Assert.Equal(before.Screening.Mode, after.Screening.Mode);
        Assert.Equal(before.Screening.FaultReason, after.Screening.FaultReason);
        Assert.Equal(before.Screening.KnownDivergentTests.ToArray(), after.Screening.KnownDivergentTests.ToArray());
    }

    private static void AssertEvent(string line, OperatorIntentRecord intent, string field, string oldValue,
        string newValue, bool changed, string backup, bool slots = false)
    {
        using var document = JsonDocument.Parse(line);
        Assert.Equal("executor-setting", document.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal($"EXECUTOR_SETTING intent={intent.Id} verb={intent.Verb} field={field} old={oldValue} new={newValue} changed={changed.ToString().ToLowerInvariant()} backup={backup} actor=owner actor_kind=Agent" +
            (slots ? " remote_slots_file=unsynced" : ""), document.RootElement.GetProperty("detail").GetString());
    }

    internal sealed class Harness : IDisposable
    {
        internal static readonly DateTimeOffset At = new(2026, 10, 18, 10, 11, 12, TimeSpan.Zero);
        private readonly ConductorVerbStartTests.Fixture fixture = new();
        internal OrchestratorWorkspace Workspace => fixture.Workspace;
        internal SqliteOperatorIntentStore Intents { get; }
        internal string Path => RemoteLaneExecutorConfiguration.ResolveStorePath(Workspace.ExecutionDirectory);
        internal string Folder => System.IO.Path.GetDirectoryName(Path)!;
        internal static string Json => """
            {
              "executors": [
                { "id": "mcg-exec-1", "slots": 3, "leaseSeconds": 60, "transport": "ssh", "runnerAlias": "runner-1", "adminAlias": "admin-1", "remoteRepository": "C:/repo", "pollSeconds": 10, "occupancyProbe": true },
                { "id": "mcg-exec-2", "slots": 2, "leaseSeconds": 90, "transport": "ssh", "runnerAlias": "runner-2", "adminAlias": "admin-2", "remoteRepository": "C:/repo", "pollSeconds": 12, "occupancyProbe": false }
              ],
              "focusedEvidence": { "mode": "shadow", "sampleEvery": 1, "graceSeconds": 300 },
              "lanes": ["*"],
              "machineLocalResourceKeys": ["key1", "key2", "key3", "key4"]
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

        internal Harness()
        {
            Intents = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Directory.CreateDirectory(Folder);
            Write(Json);
        }

        internal void Write(string text) => File.WriteAllBytes(Path, Encoding.UTF8.GetBytes(text));
        internal RemoteLaneExecutorConfiguration Load() => RemoteLaneExecutorConfiguration.Load(Path);
        internal byte[]? FileBytes() => File.Exists(Path) ? File.ReadAllBytes(Path) : null;
        internal byte[]? LogBytes() => File.Exists(Workspace.ConductEventsLogPath) ? File.ReadAllBytes(Workspace.ConductEventsLogPath) : null;
        internal string[] Listing() => Directory.GetFileSystemEntries(Folder).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        internal string[] EventLines() => File.Exists(Workspace.ConductEventsLogPath) ? File.ReadAllLines(Workspace.ConductEventsLogPath) : [];
        internal string BackupName(OperatorIntentRecord intent) => $"remote-lane-executors.json.20261018T101112Z.{intent.Id[..8]}.bak";

        internal async Task<OperatorIntentRecord> Queue(params string[] args)
        {
            using var output = new StringWriter();
            Assert.Equal(0, CliExecutorSettingCommands.Run(args.Concat(["--operator-actor", "owner", "--actor-kind", "agent", "--json"]).ToArray(),
                Workspace, output, new FixedTime()));
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("mcg.operator-intent", document.RootElement.GetProperty("schema").GetString());
            var payload = document.RootElement.GetProperty("payload");
            Assert.Equal(args[0], payload.GetProperty("verb").GetString());
            Assert.Equal("Pending", payload.GetProperty("status").GetString());
            return (await Intents.GetAsync(payload.GetProperty("id").GetString()!))!;
        }

        internal Task<OperatorIntentRecord> Enqueue(string verb, string json, string assurance = "local-process")
        {
            var id = Guid.NewGuid().ToString("N");
            return Intents.EnqueueAsync(new(id, id, verb, OperatorIntentScopes.Workspace, null, json,
                [], "owner", "cli", assurance, At, ActorKind: OperatorActorKind.Agent));
        }

        internal IReadOnlyList<string> Tick(bool services = true) => new OperatorIntentCoordinator(Intents, utcNow: () => At)
        {
            ExecutorSettings = services ? new ExecutorSettingIntentHandler(Path, Workspace.ConductEventsLogPath) : null
        }.ExecuteWorkspacePending(new AgentOrchestratorKernel());

        public void Dispose() => fixture.Dispose();
    }

    internal sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Harness.At;
    }
}
