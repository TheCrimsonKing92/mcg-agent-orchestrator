using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Harness = ExecutorSettingIntentScenarioTests.Harness;

// Parallel-safe: isolated workspace, inbox and filesystem for every case.
public sealed class ExecutorAdmissionIntentTests
{
    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    public async Task Submit_ValidBoolean_AppliesOnlyAdmissionAndAudits(string token, bool enabled)
    {
        using var fixture = new Harness();
        fixture.Write(Harness.Json.Replace("\"lanes\":", "\"unknown\": {\"retain\":42}, \"workerBuildCheck\": {\"mode\":\"remote-first\"}, \"lanes\":"));
        var original = fixture.FileBytes();
        var before = fixture.Load();
        var intent = await fixture.Queue("executor-set-enabled", "mcg-exec-1", token);
        Assert.Equal(original, fixture.FileBytes());
        Assert.Equal(new ExecutorSetEnabledOperatorIntentPayload("mcg-exec-1", enabled),
            JsonSerializer.Deserialize<ExecutorSetEnabledOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options));
        Assert.Single(fixture.Tick());
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        var after = fixture.Load();
        Assert.Equal(before.Executors.Select(e => e.Id == "mcg-exec-1" ? e with { AdmissionEnabled = enabled } : e), after.Executors);
        Assert.Equal(before.WorkerBuildCheck, after.WorkerBuildCheck);
        ExecutorSettingIntentScenarioTests.AssertEquivalent(before with { Executors = after.Executors }, after);
        var expected = enabled ? original! : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original!)
            .Replace("{ \"id\": \"mcg-exec-1\"", "{ \"enabled\": false, \"id\": \"mcg-exec-1\""));
        Assert.Equal(expected, fixture.FileBytes());
        using var audit = JsonDocument.Parse(Assert.Single(fixture.EventLines()));
        var detail = audit.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("field=executors[mcg-exec-1].enabled old=true new=" + token.ToLowerInvariant(), detail);
        Assert.DoesNotContain("remote_slots_file", detail);
        if (!enabled) Assert.Equal(original, File.ReadAllBytes(Path.Combine(fixture.Folder, fixture.BackupName(intent))));
        else Assert.Empty(Directory.GetFiles(fixture.Folder, "*.bak"));
    }

    [Theory]
    [InlineData("{}", "local-process", "invalid-payload")]
    [InlineData("{\"executorId\":\"mcg-exec-1\"}", "local-process", "invalid-payload")]
    [InlineData("{\"executorId\":\"mcg-exec-1\",\"enabled\":null}", "local-process", "invalid-payload")]
    [InlineData("{\"executorId\":\"mcg-exec-1\",\"enabled\":\"false\"}", "local-process", "invalid-payload")]
    [InlineData("{\"executorId\":\"mcg-exec-1\",\"enabled\":0}", "local-process", "invalid-payload")]
    [InlineData("{\"executorId\":\"missing\",\"enabled\":false}", "local-process", "executor-not-found")]
    [InlineData("{\"executorId\":\"mcg-exec-1\",\"enabled\":false}", "untrusted", "tier-below-mutate")]
    [InlineData("{\"executorId\":\"mcg-exec-1\",\"enabled\":false}", OperatorIntentAdjudication.StewardAssurance, "steward-capability-boundary")]
    public async Task Apply_InvalidOrUnauthorized_PreservesBytes(string payload, string assurance, string reason)
    {
        using var fixture = new Harness();
        var intent = await fixture.Enqueue(OperatorIntentVerbs.ExecutorSetEnabled, payload, assurance);
        var bytes = fixture.FileBytes();
        var listing = fixture.Listing();
        Assert.Single(fixture.Tick());
        var rejected = (await fixture.Intents.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Equal("Rejected executor-set-enabled: " + reason, rejected.Outcome);
        Assert.Equal(bytes, fixture.FileBytes());
        Assert.Equal(listing, fixture.Listing());
        Assert.Empty(fixture.EventLines());
    }

    [Fact]
    public async Task Apply_RemovedAfterSubmission_RejectsAtApplication()
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-enabled", "mcg-exec-1", "false");
        fixture.Write(Harness.Json.Replace("mcg-exec-1", "replacement"));
        var bytes = fixture.FileBytes();
        fixture.Tick();
        Assert.Equal("Rejected executor-set-enabled: executor-not-found", (await fixture.Intents.GetAsync(intent.Id))!.Outcome);
        Assert.Equal(bytes, fixture.FileBytes());
    }

    [Fact]
    public async Task Apply_BackupCollision_RejectsWithoutReplacementOrTempFile()
    {
        using var fixture = new Harness();
        var intent = await fixture.Queue("executor-set-enabled", "mcg-exec-1", "false");
        File.WriteAllText(Path.Combine(fixture.Folder, fixture.BackupName(intent)), "existing");
        var listing = fixture.Listing();
        var bytes = fixture.FileBytes();
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(bytes, fixture.FileBytes());
        Assert.Equal(listing, fixture.Listing());
        Assert.Empty(fixture.EventLines());
    }

    [Fact]
    public async Task Replay_AppliedOrConflicting_ReturnsIdentityOrRejectsWithoutMutation()
    {
        using var fixture = new Harness();
        var first = await fixture.Queue("executor-set-enabled", "mcg-exec-1", "false", "--idempotency-key", "same");
        fixture.Tick();
        var applied = (await fixture.Intents.GetAsync(first.Id))!;
        var bytes = fixture.FileBytes();
        var events = fixture.EventLines();
        using var output = new StringWriter();
        Assert.Equal(0, CliExecutorSettingCommands.Run(["executor-set-enabled", "mcg-exec-1", "FALSE", "--idempotency-key", "same",
            "--operator-actor", "owner", "--actor-kind", "agent", "--json"], fixture.Workspace, output));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(first.Id, json.RootElement.GetProperty("payload").GetProperty("id").GetString());
        Assert.Equal("Applied", json.RootElement.GetProperty("payload").GetProperty("status").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Intents.EnqueueAsync(first with
        { Id = Guid.NewGuid().ToString("N"), PayloadJson = "{\"executorId\":\"mcg-exec-1\",\"enabled\":true}" }));
        Assert.Empty(fixture.Tick());
        var replayed = (await fixture.Intents.GetAsync(first.Id))!;
        Assert.Equal(applied.Status, replayed.Status);
        Assert.Equal(applied.Outcome, replayed.Outcome);
        Assert.Equal(bytes, fixture.FileBytes());
        Assert.Equal(events, fixture.EventLines());
    }

    [Fact]
    public async Task Restart_ReconstructedStoreAndHandler_AppliesPendingThenReenables()
    {
        using var fixture = new Harness();
        var pending = await fixture.Queue("executor-set-enabled", "mcg-exec-1", "false");
        Assert.NotNull(await fixture.Intents.ClaimNextAsync(OperatorIntentScopes.Workspace, OperatorIntentCoordinator.ClaimOwner));
        var store = SqliteOperatorIntentStore.ForDirectories(fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        var coordinator = new OperatorIntentCoordinator(store, utcNow: () => Harness.At)
        { ExecutorSettings = new ExecutorSettingIntentHandler(fixture.Path, fixture.Workspace.ConductEventsLogPath) };
        Assert.Single(coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel()));
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(pending.Id))!.Status);
        Assert.False(fixture.Load().Executors[0].AdmissionEnabled);
        var resume = await fixture.Queue("executor-set-enabled", "mcg-exec-1", "true");
        Assert.Single(fixture.Tick());
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(resume.Id))!.Status);
        Assert.True(fixture.Load().Executors[0].AdmissionEnabled);
        var slots = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(slots.Id))!.Status);
        Assert.True(fixture.Load().Executors[0].AdmissionEnabled);
        Assert.Equal(2, fixture.Load().Executors[0].Slots);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("false, \"enabled\":true")]
    public void Load_NonBooleanOrDuplicate_DisablesInvalidConfiguration(string token)
    {
        using var fixture = new Harness();
        fixture.Write(Harness.Json.Replace("\"slots\": 3", "\"enabled\": " + token + ", \"slots\": 3"));
        Assert.False(fixture.Load().Enabled);
        Assert.Equal("invalid", fixture.Load().DisabledReason);
    }

    [Fact]
    public async Task Settings_DisabledTarget_RemainsAddressableWithoutReenabling()
    {
        using var fixture = new Harness();
        await fixture.Queue("executor-set-enabled", "mcg-exec-1", "false");
        fixture.Tick();
        var slots = await fixture.Queue("executor-set-slots", "mcg-exec-1", "2");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(slots.Id))!.Status);
        Assert.False(fixture.Load().Executors[0].AdmissionEnabled);
        Assert.Equal(2, fixture.Load().Executors[0].Slots);
        var focused = await fixture.Queue("focused-evidence-set", "--mode", "remote-first", "--sample-every", "4");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(focused.Id))!.Status);
        Assert.False(fixture.Load().Executors[0].AdmissionEnabled);
        Assert.Equal(2, fixture.Load().Executors[0].Slots);
        Assert.Equal("remote-first", fixture.Load().FocusedEvidence.Mode);
        Assert.Equal(4, fixture.Load().FocusedEvidence.SampleEvery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_ApplyWithSlotSyncConfigured_NeverTouchesRemoteSlots(bool enabled)
    {
        using var fixture = new Harness();
        var pending = await fixture.Queue("executor-set-enabled", "mcg-exec-1", enabled ? "true" : "false");
        var calls = 0;
        var sync = new RemoteExecutorSlotsFileSync((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "forbidden remote slots sync"));
        }, fixture.Workspace.RootDirectory, TimeSpan.FromSeconds(30));
        var coordinator = new OperatorIntentCoordinator(fixture.Intents, utcNow: () => Harness.At)
        { ExecutorSettings = new ExecutorSettingIntentHandler(fixture.Path, fixture.Workspace.ConductEventsLogPath, sync) };
        Assert.Single(coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel()));
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(pending.Id))!.Status);
        Assert.Equal(enabled, fixture.Load().Executors[0].AdmissionEnabled);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rewrite_LegacyOrExisting_PreservesBomWhitespaceAndUnknowns(bool existing)
    {
        var json = "{\r\n  \"executors\":[{\r\n    \"id\":\"one\",\"unknown\":42" + (existing ? ",\"enabled\":true" : "") + "\r\n  }],\"lanes\":[\"*\"]\r\n}";
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var expected = existing ? json.Replace("\"enabled\":true", "\"enabled\":false")
            : json.Replace("\"id\"", "\"enabled\": false,\r\n    \"id\"");
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(expected)),
            RemoteLaneExecutorSettingRewrite.RewriteEnabled(bytes, "one", false));
    }
}
