using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns all stores and targeted host files.
public sealed class ExperimentFlagToggleIntentTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ClaimedToggle_AfterWriteAndFailedCompletion_ReplaysSameDestination(bool remote, bool startsOn)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture, remote);
        var target = record.Spec.Intervention.FlagTarget!;
        if (!startsOn)
        {
            var first = Submit(fixture, record.Id);
            fixture.Tick();
            Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(first).Status);
        }
        Assert.Equal(startsOn, fixture.Flags.Read(target));
        var intent = Submit(fixture, record.Id);
        var failingStore = new FailingToggleCompletionStore(fixture.Intents, intent.Id);
        var interrupted = Coordinator(fixture, failingStore);

        var failure = Assert.Throws<IOException>(() =>
            interrupted.ExecuteWorkspacePending(new AgentOrchestratorKernel()));
        Assert.Equal("toggle completion interrupted after host write", failure.Message);
        Assert.Equal(1, failingStore.CompletionAttempts);
        Assert.Equal(!startsOn, fixture.Flags.Read(target));
        var claimed = fixture.Result(intent);
        Assert.Equal(OperatorIntentStatus.Claimed, claimed.Status);
        Assert.Equal(OperatorIntentCoordinator.ClaimOwner, claimed.ClaimOwner);
        Assert.Null(claimed.CompletedAt);
        var flagPath = remote ? fixture.ExecutorsPath : fixture.PolicyPath;
        var afterWrite = File.ReadAllBytes(flagPath);

        // Reopen durable state and rebuild every service, as a restarted conductor would.
        var recoveredStore = SqliteOperatorIntentStore.ForDirectories(
            fixture.Workspace.OrchestratorDirectory, fixture.Workspace.LogDirectory);
        Assert.Equal(intent.Id, recoveredStore.ClaimNextAsync(OperatorIntentScopes.Workspace,
            OperatorIntentCoordinator.ClaimOwner).GetAwaiter().GetResult()!.Id);
        var recovered = Coordinator(fixture, recoveredStore);
        var lines = recovered.ExecuteWorkspacePending(new AgentOrchestratorKernel());
        Assert.Contains($"id={intent.Id}", Assert.Single(lines));
        var completed = recoveredStore.GetAsync(intent.Id).GetAwaiter().GetResult()!;
        Assert.Equal(OperatorIntentStatus.Applied, completed.Status);
        Assert.Equal("Applied experiment-toggle-flag; tier=Mutate; replayed=True.", completed.Outcome);
        Assert.Equal(!startsOn, fixture.Flags.Read(target));
        Assert.Equal(afterWrite, File.ReadAllBytes(flagPath));
        var experiment = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, experiment.Outcome);
        Assert.Null(experiment.Decision);
        Assert.Equal(target, experiment.Spec.Intervention.FlagTarget);
    }

    [Theory]
    [InlineData(false, "local-process")]
    [InlineData(true, "local-process")]
    [InlineData(false, "steward")]
    [InlineData(true, "steward")]
    public void AppliedFlag_TogglesBothDirectionsWithoutChangingExperiment(bool remote, string assurance)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture, remote);
        var target = record.Spec.Intervention.FlagTarget!;
        Assert.False(target.PriorValue);
        Assert.True(fixture.Flags.Read(target));

        foreach (var expected in new[] { false, true })
        {
            var intent = Submit(fixture, record.Id, assurance);
            fixture.Tick();
            var result = fixture.Result(intent);
            Assert.Equal(OperatorIntentStatus.Applied, result.Status);
            Assert.Equal("Applied experiment-toggle-flag; tier=Mutate; replayed=False.", result.Outcome);
            Assert.Equal(expected, fixture.Flags.Read(target));
            var persisted = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            Assert.Equal(ExperimentOutcomeState.Open, persisted.Outcome);
            Assert.Null(persisted.Decision);
            Assert.Equal(target, persisted.Spec.Intervention.FlagTarget);
        }
    }

    [Theory]
    [InlineData("property", "property-not-allowlisted maxConcurrentPaidWorkers")]
    [InlineData("file-kind", "file-kind-unsupported")]
    [InlineData("baseline", "not-alternating-gates")]
    [InlineData("decided", "experiment-decided")]
    [InlineData("not-config", "not-config-flag")]
    [InlineData("unapplied", "flag-not-applied")]
    public void InvalidTargetOrState_RejectsWithoutWriting(string failure, string reason)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var spec = AlternatingSpec() with
        {
            Intervention = new(ExperimentInterventionKind.ConfigFlag, "toggle",
                new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true, false))
        };
        spec = failure switch
        {
            "property" => spec with { Intervention = spec.Intervention with
                { FlagTarget = spec.Intervention.FlagTarget! with { PropertyName = "maxConcurrentPaidWorkers" } } },
            "baseline" => spec with { Baseline = new(ExperimentBaselineKind.BeforeAfterWindow) },
            "not-config" => spec with { Intervention = new(ExperimentInterventionKind.Policy, "policy") },
            "unapplied" => spec with { Intervention = spec.Intervention with
                { FlagTarget = spec.Intervention.FlagTarget! with { PriorValue = null } } },
            _ => spec
        };
        var record = fixture.Add(spec);
        if (failure == "file-kind")
        {
            // Missing fileKind deserializes as the unsupported zero value; undefined enums cannot be serialized.
            var intervention = JsonSerializer.SerializeToNode(spec.Intervention, ExperimentStore.JsonOptions)!;
            intervention["flagTarget"]!.AsObject().Remove("fileKind");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Workspace.ExperimentStorePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE experiments SET intervention_json=$json WHERE id=$id";
            command.Parameters.AddWithValue("$json", intervention.ToJsonString());
            command.Parameters.AddWithValue("$id", record.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
            Assert.Equal((ExperimentFlagFileKind)0,
                fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Spec.Intervention.FlagTarget!.FileKind);
        }
        if (failure == "decided") fixture.Experiments.DecideAsync(record.Id, ExperimentOutcomeState.Confirmed,
            "test", "keep").GetAwaiter().GetResult();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = Submit(fixture, record.Id);
        fixture.Tick();
        AssertRejected(fixture, intent, reason, before);
    }

    [Theory]
    [InlineData("other", "conductor-experiment-toggle", OperatorActorKind.Agent)]
    [InlineData("conductor", "cli", OperatorActorKind.Agent)]
    [InlineData("conductor", "conductor-experiment-toggle", OperatorActorKind.Human)]
    [InlineData("conductor", "conductor-experiment-revert", OperatorActorKind.Agent)]
    public void StewardToggle_RejectsEveryUnboundCaller(string actor, string channel, OperatorActorKind kind)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = Submit(fixture, record.Id, OperatorIntentAdjudication.StewardAssurance, actor, channel, kind);
        fixture.Tick();
        AssertRejected(fixture, intent, "steward-capability-boundary", before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StewardApplyAndRevert_RejectToggleChannel(bool revert)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = fixture.Submit(record.Id, revert, OperatorIntentAdjudication.StewardAssurance,
            actor: "conductor", channel: "conductor-experiment-toggle", actorKind: OperatorActorKind.Agent);
        fixture.Tick();
        AssertRejected(fixture, intent, "steward-capability-boundary", before);
    }

    [Theory]
    [InlineData("steward", "steward-capability-boundary")]
    [InlineData("unverified", "tier-below-mutate")]
    public void CapabilityChecks_PrecedeExperimentState(string assurance, string reason)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        fixture.Experiments.DecideAsync(record.Id, ExperimentOutcomeState.Confirmed, "test", "keep").GetAwaiter().GetResult();
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = Submit(fixture, record.Id, assurance, actor: "other");
        fixture.Tick();
        AssertRejected(fixture, intent, reason, before);
    }

    private static ExperimentSpec AlternatingSpec(ExperimentFlagTarget? target = null) => ExperimentFlagTestFixture.Spec(target) with
    {
        Baseline = new(ExperimentBaselineKind.AlternatingGates),
        Metrics = ["gate-seconds"],
        Guardrail = new("tick-cpu-ms", new("tick-cpu-ms", ">", 100)),
        StopRule = new(99, ExperimentStopUnit.Gates),
        DecisionRule = new([new("gate-seconds", "<", 0)], [new("gate-seconds", ">", 0)])
    };

    private static ExperimentRecord Apply(ExperimentFlagTestFixture fixture, bool remote = false)
    {
        if (remote) File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson());
        var record = fixture.Add(AlternatingSpec(remote ? new(ExperimentFlagFileKind.RemoteLaneExecutors,
            RemoteLaneExecutorFlags.FocusedEvidenceShadow, true) : null));
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        return fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
    }

    private static OperatorIntentRecord Submit(ExperimentFlagTestFixture fixture, string experimentId,
        string assurance = "local-process", string? actor = null, string? channel = null, OperatorActorKind? kind = null)
    {
        var id = Guid.NewGuid().ToString("n");
        var steward = assurance == OperatorIntentAdjudication.StewardAssurance;
        return fixture.Intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.ExperimentToggleFlag,
            OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(new ExperimentToggleFlagOperatorIntentPayload(experimentId), OperatorIntentJson.Options),
            [], actor ?? (steward ? "conductor" : "owner"), channel ?? (steward ? "conductor-experiment-toggle" : "cli"),
            assurance, ExperimentFlagTestFixture.Now, ActorKind: kind ?? (steward ? OperatorActorKind.Agent : OperatorActorKind.Human)))
            .GetAwaiter().GetResult();
    }

    private static void AssertRejected(ExperimentFlagTestFixture fixture, OperatorIntentRecord intent, string reason, byte[] before)
    {
        var result = fixture.Result(intent);
        Assert.Equal(OperatorIntentStatus.Rejected, result.Status);
        Assert.Equal($"Rejected {intent.Verb}: {reason}", result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    private static OperatorIntentCoordinator Coordinator(ExperimentFlagTestFixture fixture, IOperatorIntentStore store) =>
        new(store, utcNow: () => ExperimentFlagTestFixture.Now)
        {
            ExperimentFlags = new ExperimentFlagIntentHandler(fixture.Workspace.ExperimentStorePath,
                fixture.PolicyPath, fixture.ExecutorsPath)
        };

    private sealed class FailingToggleCompletionStore(IOperatorIntentStore inner, string interruptedId) : IOperatorIntentStore
    {
        internal int CompletionAttempts { get; private set; }

        public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default) =>
            inner.EnqueueAsync(intent, cancellationToken);
        public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) =>
            inner.ClaimNextAsync(goalId, claimOwner, cancellationToken);
        public Task<OperatorIntentRecord?> ClaimNextPendingAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) =>
            inner.ClaimNextPendingAsync(goalId, claimOwner, cancellationToken);
        public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner,
            CancellationToken cancellationToken = default) => inner.ClaimNextByVerbAsync(goalId, verb, claimOwner, cancellationToken);
        public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome,
            DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        {
            if (intentId == interruptedId && status == OperatorIntentStatus.Applied)
            {
                CompletionAttempts++;
                return Task.FromException(new IOException("toggle completion interrupted after host write"));
            }
            return inner.CompleteAsync(intentId, claimOwner, status, outcome, completedAt, cancellationToken);
        }
        public Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(intentId, cancellationToken);
        public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20,
            CancellationToken cancellationToken = default) => inner.ListForGoalAsync(goalId, limit, cancellationToken);
        public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) =>
            inner.ListActionableGoalIdsAsync(cancellationToken);
        public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(
            IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) =>
            inner.ListActionableSummariesAsync(goalIds, cancellationToken);
        public void AcknowledgeWake(string intentId) => inner.AcknowledgeWake(intentId);
    }
}
