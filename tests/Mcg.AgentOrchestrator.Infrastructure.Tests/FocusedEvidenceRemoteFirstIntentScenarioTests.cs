using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ExecutorSettingIntentScenarioTests;

// Parallel-safe: per-test workspace, SQLite and logs; injected clock and AsyncLocal stderr.
public sealed class FocusedEvidenceRemoteFirstIntentScenarioTests
{
    [Theory]
    [InlineData("shadow", "remote-first")]
    [InlineData("off", "remote-first")]
    [InlineData("remote-first", "shadow")]
    [InlineData("remote-first", "off")]
    public async Task Mode_Transition_ChangesOnlyTokenAndAuditsBackup(string current, string desired)
    {
        using var fixture = new Harness();
        var text = Harness.Json.Replace("\"shadow\"", $"\"{current}\"", StringComparison.Ordinal);
        fixture.Write(text);
        var original = fixture.FileBytes();
        var before = fixture.Load();
        var oldEvents = fixture.EventLines();

        var intent = await fixture.Queue("focused-evidence-set", "--mode", desired);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Single(fixture.Tick());

        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        var backup = fixture.BackupName(intent);
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.Equal($"Applied focused-evidence-set; tier=Mutate; replayed=False; changes=focusedEvidence.mode:{current}->{desired}; backup={backup}.", completed.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes(text.Replace($"\"{current}\"", $"\"{desired}\"", StringComparison.Ordinal)), fixture.FileBytes());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Folder, backup)));
        Assert.Single(Directory.GetFiles(fixture.Folder, "*.bak"));
        AssertEquivalent(before, fixture.Load(), mode: desired);
        AssertEvent(Assert.Single(fixture.EventLines().Skip(oldEvents.Length)), intent,
            "focusedEvidence.mode", current, desired, true, backup);
    }

    [Fact]
    public async Task Mode_RemoteFirstNoop_PreservesFileAndFolderAndAuditsNoChange()
    {
        using var fixture = new Harness();
        fixture.Write(Harness.Json.Replace("\"shadow\"", "\"remote-first\"", StringComparison.Ordinal));
        var original = fixture.FileBytes();
        var before = fixture.Load();
        var intent = await fixture.Queue("focused-evidence-set", "--mode", "remote-first");
        var listing = fixture.Listing();

        Assert.Single(fixture.Tick());

        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.Equal("Applied focused-evidence-set; tier=Mutate; replayed=True; changes=focusedEvidence.mode:remote-first->remote-first; backup=none.", completed.Outcome);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(listing, fixture.Listing());
        AssertEquivalent(before, fixture.Load());
        AssertEvent(Assert.Single(fixture.EventLines()), intent,
            "focusedEvidence.mode", "remote-first", "remote-first", false, "none");
    }

    [Fact]
    public async Task ModeAndSample_RemoteFirst_UsesOneBackupAndOrderedEvents()
    {
        using var fixture = new Harness();
        var original = fixture.FileBytes();
        var before = fixture.Load();
        var intent = await fixture.Queue("focused-evidence-set", "--mode", "remote-first", "--sample-every", "9");
        Assert.Equal(original, fixture.FileBytes());

        Assert.Single(fixture.Tick());

        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        var backup = fixture.BackupName(intent);
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.Equal($"Applied focused-evidence-set; tier=Mutate; replayed=False; changes=focusedEvidence.mode:shadow->remote-first,focusedEvidence.sampleEvery:1->9; backup={backup}.", completed.Outcome);
        Assert.Single(Directory.GetFiles(fixture.Folder, "*.bak"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Folder, backup)));
        Assert.Equal(Encoding.UTF8.GetBytes(Harness.Json.Replace("\"shadow\"", "\"remote-first\"", StringComparison.Ordinal)
            .Replace("\"sampleEvery\": 1", "\"sampleEvery\": 9", StringComparison.Ordinal)), fixture.FileBytes());
        AssertEquivalent(before, fixture.Load(), mode: "remote-first", sample: 9);
        Assert.Equal(before.FocusedEvidence.GraceSeconds, fixture.Load().FocusedEvidence.GraceSeconds);
        var events = fixture.EventLines();
        Assert.Equal(2, events.Length);
        AssertEvent(events[0], intent, "focusedEvidence.mode", "shadow", "remote-first", true, backup);
        AssertEvent(events[1], intent, "focusedEvidence.sampleEvery", "1", "9", true, backup);
    }

    [Fact]
    public async Task SampleOnly_RemoteFirst_PreservesModeAndAuditsOneField()
    {
        using var fixture = new Harness();
        var text = Harness.Json.Replace("\"shadow\"", "\"remote-first\"", StringComparison.Ordinal);
        fixture.Write(text);
        var before = fixture.Load();
        var intent = await fixture.Queue("focused-evidence-set", "--sample-every", "4");

        Assert.Single(fixture.Tick());

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal("remote-first", fixture.Load().FocusedEvidence.Mode);
        AssertEquivalent(before, fixture.Load(), sample: 4);
        Assert.Equal(Encoding.UTF8.GetBytes(text.Replace("\"sampleEvery\": 1", "\"sampleEvery\": 4", StringComparison.Ordinal)), fixture.FileBytes());
        AssertEvent(Assert.Single(fixture.EventLines()), intent,
            "focusedEvidence.sampleEvery", "1", "4", true, fixture.BackupName(intent));
    }

    [Fact]
    public async Task Submit_RemoteFirst_QueuesExactPayloadWithoutChangingFile()
    {
        using var fixture = new Harness();
        var original = fixture.FileBytes();

        var intent = await fixture.Queue("focused-evidence-set", "--mode", "remote-first");

        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(intent.Id, Assert.Single(await fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace)).Id);
        var payload = JsonSerializer.Deserialize<FocusedEvidenceSetOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options);
        Assert.NotNull(payload);
        Assert.Equal("remote-first", payload.Mode);
        Assert.Null(payload.SampleEvery);
        Assert.Equal(Harness.At, intent.CreatedAt);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Empty(fixture.EventLines());
    }

    [Theory]
    [InlineData("on")]
    [InlineData("Shadow")]
    [InlineData("Remote-First")]
    [InlineData("remote_first")]
    [InlineData("")]
    public async Task Submit_UnsupportedMode_RejectsWithoutIntentOrFileChange(string mode)
    {
        using var fixture = new Harness();
        var original = fixture.FileBytes();
        using var output = new StringWriter();
        var exit = -1;

        var error = AsyncLocalConsoleRouter.CaptureError(() => exit = CliExecutorSettingCommands.Run(
            ["focused-evidence-set", "--mode", mode], fixture.Workspace, output, new FixedTime()));

        Assert.Equal(1, exit);
        Assert.Equal("", output.ToString());
        Assert.StartsWith("Error: ", error);
        Assert.Empty(await fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(original, fixture.FileBytes());
    }

    [Theory]
    [InlineData("absent", "focused-evidence-mode-absent")]
    [InlineData("bogus", "executors-file-invalid")]
    public async Task Apply_InvalidFile_RejectsWithoutFileFolderOrLogChange(string state, string reason)
    {
        using var fixture = new Harness();
        fixture.Write(state == "absent"
            ? Harness.Json.Replace("\"mode\": \"shadow\", ", "", StringComparison.Ordinal)
            : Harness.Json.Replace("\"shadow\"", "\"bogus\"", StringComparison.Ordinal));
        var intent = await fixture.Queue("focused-evidence-set", "--mode", "remote-first");
        await AssertRejectedWithoutWrites(fixture, intent, reason);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("Shadow")]
    [InlineData("Remote-First")]
    [InlineData("remote_first")]
    [InlineData("")]
    public async Task Apply_UnsupportedPayload_RejectsWithoutFileFolderOrLogChange(string mode)
    {
        using var fixture = new Harness();
        var intent = await fixture.Enqueue(OperatorIntentVerbs.FocusedEvidenceSet,
            JsonSerializer.Serialize(new FocusedEvidenceSetOperatorIntentPayload(mode, null), OperatorIntentJson.Options));
        await AssertRejectedWithoutWrites(fixture, intent, "mode-unsupported");
    }

    private static async Task AssertRejectedWithoutWrites(Harness fixture, OperatorIntentRecord intent, string reason)
    {
        var original = fixture.FileBytes();
        var listing = fixture.Listing();
        var log = fixture.LogBytes();
        Assert.Single(fixture.Tick());
        var completed = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, completed.Status);
        Assert.Equal($"Rejected focused-evidence-set: {reason}", completed.Outcome);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(listing, fixture.Listing());
        Assert.Equal(log, fixture.LogBytes());
    }

    private static void AssertEvent(string line, OperatorIntentRecord intent, string field,
        string oldValue, string newValue, bool changed, string backup)
    {
        using var document = JsonDocument.Parse(line);
        Assert.Equal("executor-setting", document.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal($"EXECUTOR_SETTING intent={intent.Id} verb=focused-evidence-set field={field} old={oldValue} new={newValue}" +
            $" changed={changed.ToString().ToLowerInvariant()} backup={backup} actor=owner actor_kind=Agent",
            document.RootElement.GetProperty("detail").GetString());
    }
}
