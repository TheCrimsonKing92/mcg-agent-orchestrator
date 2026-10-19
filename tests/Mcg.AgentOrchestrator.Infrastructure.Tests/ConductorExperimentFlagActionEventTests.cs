using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns its workspace and AsyncLocal live-occupancy override.
public sealed class ConductorExperimentFlagActionEventTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
    private const string FixedId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("revert")]
    [InlineData("keep")]
    [InlineData("toggle")]
    public void ObserveTick_NewAction_WritesExactlyOnceAcrossRestarts(string action)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Prepare(fixture, action);
        var asOf = record.CreatedAt.AddMinutes(7);
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath, utcNow: () => asOf);
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var watch = new ConductorExperimentWatch(fixture.Workspace, writer);

        watch.ObserveTick(asOf);

        var entry = Assert.Single(ActionEvents(fixture));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("goalId").ValueKind);
        Assert.Equal(asOf, entry.GetProperty("timestamp").GetDateTimeOffset());
        Assert.False(entry.TryGetProperty("operator", out _));
        var expected = $"EXPERIMENT_FLAG_ACTION experiment={record.Id} action={action} " +
            "file=conductor-policy property=followerGatesEnabled from=true to=" +
            (action == "keep" ? $"true backlog=experiment-flag-keep-{record.Id}" : "false");
        Assert.Equal(expected, entry.GetProperty("detail").GetString());
        AssertActionState(fixture, record, action);

        watch.ObserveTick(asOf);
        new ConductorExperimentWatch(fixture.Workspace, writer).ObserveTick(asOf);

        Assert.Equal(entry.GetRawText(), Assert.Single(ActionEvents(fixture)).GetRawText());
        AssertActionState(fixture, record, action);
    }

    [Theory]
    [InlineData("revert")]
    [InlineData("keep")]
    [InlineData("toggle")]
    public void ObserveTick_NullWriter_PreservesIntentsOutcomeAndBacklog(string action)
    {
        using var logged = new ExperimentFlagTestFixture();
        using var silent = new ExperimentFlagTestFixture();
        var loggedRecord = Prepare(logged, action);
        var silentRecord = Prepare(silent, action);
        var asOf = loggedRecord.CreatedAt.AddMinutes(7);
        var silentAsOf = silentRecord.CreatedAt.AddMinutes(7);
        var writer = new ConductEventLogWriter(logged.Workspace.ConductEventsLogPath, utcNow: () => asOf);
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);

        new ConductorExperimentWatch(logged.Workspace, writer).ObserveTick(asOf);
        new ConductorExperimentWatch(silent.Workspace, null).ObserveTick(silentAsOf);

        Assert.Single(ActionEvents(logged));
        Assert.Empty(ActionEvents(silent));
        AssertActionState(logged, loggedRecord, action);
        AssertActionState(silent, silentRecord, action);
        Assert.Equal(IntentKeys(logged, loggedRecord.Id), IntentKeys(silent, silentRecord.Id));
        Assert.Equal(Outcome(logged, loggedRecord), Outcome(silent, silentRecord));
        Assert.Equal(Backlog(logged, loggedRecord.Id), Backlog(silent, silentRecord.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserveTick_OccupantOrProbeError_WritesNoActionOrIntent(bool throws)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Prepare(fixture, "toggle");
        var asOf = record.CreatedAt.AddMinutes(7);
        var before = Intents(fixture).Count;
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath, utcNow: () => asOf);
        var probes = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
        {
            probes++;
            if (throws) throw new GateLoadContextProbe.LoadProbeUnavailableException("test probe failure");
            return [new(null, "test-gate", 0, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.Acceptance)];
        });

        new ConductorExperimentWatch(fixture.Workspace, writer).ObserveTick(asOf);

        Assert.Equal(1, probes);
        Assert.Empty(ActionEvents(fixture));
        Assert.Equal(before, Intents(fixture).Count);
        Assert.Equal(ExperimentOutcomeState.Open, Outcome(fixture, record));
    }

    [Fact]
    public void ObserveTick_RevertAppendFails_PreservesIntentAndDecision()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Prepare(fixture, "revert");
        var asOf = record.CreatedAt.AddMinutes(7);
        var appends = 0;
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath, utcNow: () => asOf,
            beforeAppendCommit: () => { appends++; throw new IOException("test append failure"); });
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);

        var error = Record.Exception(() => new ConductorExperimentWatch(fixture.Workspace, writer).ObserveTick(asOf));

        Assert.Null(error);
        Assert.Equal(1, appends);
        AssertActionState(fixture, record, "revert");
        Assert.Empty(ActionEvents(fixture));
    }

    [Fact]
    public void ObserveTick_ToggleAppendFails_StillRaisesReadingDue()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Prepare(fixture, "toggle", stopCount: 1);
        var asOf = record.CreatedAt.AddMinutes(7);
        var appends = 0;
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath, utcNow: () => asOf,
            beforeAppendCommit: () => { appends++; throw new IOException("test append failure"); });
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);

        var error = Record.Exception(() => new ConductorExperimentWatch(fixture.Workspace, writer).ObserveTick(asOf));

        Assert.Null(error);
        Assert.Equal(2, appends); // The action append and the subsequent reading-due append both ran.
        AssertActionState(fixture, record, "toggle");
        Assert.Empty(ActionEvents(fixture));
        var item = Assert.Single(CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory)
            .ListAsync().GetAwaiter().GetResult());
        Assert.Equal($"experiment-reading-due:{record.Id}:stop-rule", item.CorrelationKey);
    }

    [Fact]
    public void ObserveTick_FlagReadFails_StillRaisesReadingDue()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Prepare(fixture, "toggle", stopCount: 1);
        var asOf = record.CreatedAt.AddMinutes(7);
        File.WriteAllText(fixture.PolicyPath, "{malformed");
        var before = Intents(fixture).Count;
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath, utcNow: () => asOf);
        var probes = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => { probes++; return []; });

        var error = Record.Exception(() => new ConductorExperimentWatch(fixture.Workspace, writer).ObserveTick(asOf));

        Assert.Null(error);
        Assert.Equal(1, probes); // The controller still ran after the observation read failed.
        Assert.Equal(before, Intents(fixture).Count);
        Assert.Empty(ActionEvents(fixture));
        var entry = Assert.Single(Events(fixture, "experiment-reading-due"));
        Assert.Contains($"experiment={record.Id} trigger=stop-rule", entry.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("revert", true, false, true, "true", "false")]
    [InlineData("revert", false, false, true, "false", "false")]
    [InlineData("revert", null, false, true, "unknown", "false")]
    [InlineData("revert", true, null, true, "true", "unknown")]
    [InlineData("keep", null, false, true, "unknown", "true")]
    [InlineData("keep", true, true, false, "true", "false")]
    [InlineData("toggle", null, false, true, "unknown", "unknown")]
    [InlineData("toggle", true, false, true, "true", "false")]
    [InlineData("toggle", false, false, true, "false", "true")]
    [InlineData("toggle", false, true, false, "false", "true")]
    [InlineData("toggle", true, true, false, "true", "false")]
    [InlineData("toggle", true, null, true, "true", "unknown")]
    public void Detail_ValidTarget_FormatsExactValues(string action, bool? before,
        bool? prior, bool applied, string from, string to)
    {
        var record = HandBuilt(new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", applied, prior));

        var detail = ExperimentFlagActionEvent.Detail(record, action, before);

        Assert.Equal("experiment-flag-action", ExperimentFlagActionEvent.Kind);
        Assert.Equal($"EXPERIMENT_FLAG_ACTION experiment={FixedId} action={action} " +
            $"file=conductor-policy property=followerGatesEnabled from={from} to={to}" +
            (action == "keep" ? $" backlog=experiment-flag-keep-{FixedId}" : ""), detail);
    }

    [Fact]
    public void Detail_RemoteTarget_PreservesFileAndPropertyName()
    {
        var record = HandBuilt(new(ExperimentFlagFileKind.RemoteLaneExecutors, "focusedEvidenceShadow", true, false));

        Assert.Equal($"EXPERIMENT_FLAG_ACTION experiment={FixedId} action=toggle " +
            "file=remote-lane-executors property=focusedEvidenceShadow from=false to=true",
            ExperimentFlagActionEvent.Detail(record, "toggle", false));
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("")]
    [InlineData("Keep")]
    public void Detail_UnsupportedAction_Throws(string action) =>
        Assert.Throws<ArgumentException>(() => ExperimentFlagActionEvent.Detail(
            HandBuilt(new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true)), action, true));

    [Theory]
    [InlineData(ExperimentInterventionKind.Policy, true)]
    [InlineData(ExperimentInterventionKind.ConfigFlag, false)]
    public void Detail_InvalidIntervention_Throws(ExperimentInterventionKind kind, bool hasTarget)
    {
        var record = HandBuilt(new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true));
        record = record with { Spec = record.Spec with { Intervention = new(kind, "test",
            hasTarget ? record.Spec.Intervention.FlagTarget : null) } };

        Assert.Throws<ArgumentException>(() => ExperimentFlagActionEvent.Detail(record, "revert", true));
    }

    private static ExperimentRecord HandBuilt(ExperimentFlagTarget target) =>
        new(FixedId, ExperimentFlagTestFixture.Spec(target), FixedTime, ExperimentOutcomeState.Open, null);

    private static ExperimentRecord Prepare(ExperimentFlagTestFixture fixture, string action, int? stopCount = null)
    {
        var spec = ExperimentFlagTestFixture.Spec() with
        {
            Baseline = new(ExperimentBaselineKind.AlternatingGates), Metrics = ["gate-seconds"],
            Guardrail = new("gate-seconds", new("gate-seconds", ">", 100)),
            StopRule = new(stopCount ?? (action == "toggle" ? 99 : 6), ExperimentStopUnit.Gates),
            DecisionRule = new([new("gate-seconds", "<", 0)], [new("gate-seconds", ">", 0)])
        };
        var record = fixture.Add(spec);
        var apply = fixture.Submit(record.Id, idempotencyKey: "apply-under-test");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.False(record.Spec.Intervention.FlagTarget!.PriorValue);
        Assert.True(fixture.Flags.Read(record.Spec.Intervention.FlagTarget));
        var count = action == "toggle" ? 1 : 6;
        for (var index = 0; index < count; index++)
        {
            var totalMs = index < 3 ? 1000 : action == "keep" ? 500 : 1500;
            GateLaneTimeLedger.Append(GateLaneTimeLedger.ResolveStorePath(fixture.Workspace.ExecutionDirectory),
                new(record.CreatedAt.AddMinutes(index + 1), $"gate-{index}", null, "completed",
                    totalMs, totalMs, 0, 0,
                    ExperimentArms: new Dictionary<string, string> { [record.Id] = count == 1 || index >= 3 ? "on" : "off" }));
        }
        return record;
    }

    private static void AssertActionState(ExperimentFlagTestFixture fixture, ExperimentRecord record, string action)
    {
        var intents = Intents(fixture);
        var queued = intents.Where(intent => intent.Verb != OperatorIntentVerbs.ExperimentApplyFlag).ToArray();
        Assert.Equal(action switch { "revert" => ExperimentOutcomeState.Refuted,
            "keep" => ExperimentOutcomeState.Confirmed, _ => ExperimentOutcomeState.Open }, Outcome(fixture, record));
        var backlog = new BacklogStore(fixture.Workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult();
        if (action == "keep")
        {
            Assert.Empty(queued);
            Assert.Equal($"experiment-flag-keep-{record.Id}", Assert.Single(backlog).Id);
        }
        else
        {
            Assert.Empty(backlog);
            var intent = Assert.Single(queued);
            Assert.Equal(action == "revert" ? OperatorIntentVerbs.ExperimentRevertFlag : OperatorIntentVerbs.ExperimentToggleFlag,
                intent.Verb);
            Assert.Equal($"experiment-{action}-flag:{record.Id}" + (action == "toggle" ? ":gate-0" : ""), intent.IdempotencyKey);
        }
    }

    private static ExperimentOutcomeState Outcome(ExperimentFlagTestFixture fixture, ExperimentRecord record) =>
        fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome;

    private static IReadOnlyList<OperatorIntentRecord> Intents(ExperimentFlagTestFixture fixture) =>
        fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult();

    private static string[] IntentKeys(ExperimentFlagTestFixture fixture, string id) => Intents(fixture)
        .Select(intent => $"{intent.Verb}|{intent.IdempotencyKey.Replace(id, FixedId, StringComparison.Ordinal)}")
        .Order(StringComparer.Ordinal).ToArray();

    private static string[] Backlog(ExperimentFlagTestFixture fixture, string id) =>
        new BacklogStore(fixture.Workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult()
            .Select(item => JsonSerializer.Serialize(item with { CreatedAt = FixedTime, UpdatedAt = FixedTime })
                .Replace(id, FixedId, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    private static JsonElement[] ActionEvents(ExperimentFlagTestFixture fixture) => Events(fixture, "experiment-flag-action");

    private static JsonElement[] Events(ExperimentFlagTestFixture fixture, string kind) =>
        !File.Exists(fixture.Workspace.ConductEventsLogPath) ? [] : File.ReadAllLines(fixture.Workspace.ConductEventsLogPath)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .Where(entry => entry.GetProperty("eventKind").GetString() == kind).ToArray();
}
