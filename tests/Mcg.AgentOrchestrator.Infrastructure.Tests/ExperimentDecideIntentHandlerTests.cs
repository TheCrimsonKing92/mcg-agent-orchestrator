using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns a unique directory and real SQLite stores.
public sealed class ExperimentDecideIntentHandlerTests
{
    [Theory]
    [InlineData("confirmed", ExperimentOutcomeState.Confirmed)]
    [InlineData("refuted", ExperimentOutcomeState.Refuted)]
    [InlineData("inconclusive", ExperimentOutcomeState.Inconclusive)]
    public void Apply_OutcomeWords_StoreMatchingDecisionVerbatim(string word, ExperimentOutcomeState expected)
    {
        using var scene = new Scene();
        var experiment = scene.Add();
        var intent = scene.Submit(new(experiment.Id[..8], word, " receipt:x \n", " Keep this action \n"));

        Assert.False(new ExperimentDecideIntentHandler(scene.Workspace.ExperimentStorePath).Apply(intent));

        var stored = scene.Read(experiment.Id);
        Assert.Equal(expected, stored.Outcome);
        Assert.NotNull(stored.Decision);
        Assert.Equal(expected, stored.Decision.Outcome);
        Assert.Equal(" receipt:x \n", stored.Decision.Evidence);
        Assert.Equal(" Keep this action \n", stored.Decision.Action);
    }

    [Fact]
    public void ExecuteWorkspacePending_DuplicateAndBadTargets_RecordExactRejections()
    {
        using var scene = new Scene();
        var experiment = scene.Add();
        var first = scene.Submit(new ExperimentDecideOperatorIntentPayload(experiment.Id, "confirmed", "first evidence", "first action"));
        scene.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, scene.Result(first).Status);
        Assert.Equal("Applied experiment-decide.", scene.Result(first).Outcome);

        var second = scene.Submit(new(experiment.Id, "refuted", "second evidence", "second action"));
        scene.Tick();
        scene.AssertRejected(second, "experiment-decided");
        Assert.Equal("first evidence", scene.Read(experiment.Id).Decision!.Evidence);
        Assert.Equal("first action", scene.Read(experiment.Id).Decision!.Action);
        Assert.Equal(ExperimentOutcomeState.Confirmed, scene.Read(experiment.Id).Outcome);

        var unknown = scene.Submit(new(new string('f', 32), "confirmed", "receipt", "Keep"));
        scene.Tick();
        scene.AssertRejected(unknown, "experiment-not-found");

        var open = scene.Add();
        var blank = scene.Submit(new(open.Id, "confirmed", "receipt", "  "));
        scene.Tick();
        scene.AssertRejected(blank, "action-required");
        Assert.Equal(ExperimentOutcomeState.Open, scene.Read(open.Id).Outcome);
        Assert.Null(scene.Read(open.Id).Decision);
    }

    [Theory]
    [InlineData("steward", "steward-capability-boundary")]
    [InlineData("unverified", "tier-below-mutate")]
    public void ExecuteWorkspacePending_UnauthorizedDecision_LeavesExperimentOpen(string assurance, string reason)
    {
        using var scene = new Scene();
        var experiment = scene.Add();
        var intent = scene.Submit(new ExperimentDecideOperatorIntentPayload(experiment.Id, "confirmed", "receipt", "Keep"), assurance);
        scene.Tick();
        scene.AssertRejected(intent, reason);
        Assert.Equal(ExperimentOutcomeState.Open, scene.Read(experiment.Id).Outcome);
        Assert.Null(scene.Read(experiment.Id).Decision);
    }

    [Theory]
    [InlineData("evidence", "evidence-required")]
    [InlineData("outcome", "invalid-outcome")]
    [InlineData("payload", "invalid-payload")]
    [InlineData("null", "invalid-payload")]
    [InlineData("reference", "invalid-payload")]
    public void ExecuteWorkspacePending_InvalidFields_RejectBeforeWriting(string field, string reason)
    {
        using var scene = new Scene();
        var experiment = scene.Add();
        var payload = new ExperimentDecideOperatorIntentPayload(experiment.Id, "confirmed", "receipt", "Keep");
        if (field == "evidence") payload = payload with { Evidence = " \n " };
        if (field == "outcome") payload = payload with { Outcome = "keep" };
        if (field == "reference") payload = payload with { ExperimentId = " " };
        var intent = scene.Submit(payload, json: field == "payload" ? "{" : field == "null" ? "null" : null);
        scene.Tick();
        scene.AssertRejected(intent, reason);
        Assert.Equal(ExperimentOutcomeState.Open, scene.Read(experiment.Id).Outcome);
        Assert.Null(scene.Read(experiment.Id).Decision);
    }

    [Fact]
    public void CreateDefault_ExperimentDecideIntent_WiresDecisionHandler()
    {
        using var scene = new Scene();
        var experiment = scene.Add();
        var intent = scene.Submit(new ExperimentDecideOperatorIntentPayload(experiment.Id[..8], "inconclusive", "receipt", "Revisit"));
        OperatorIntentCoordinator.CreateDefault(scene.Workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
        Assert.Equal(OperatorIntentStatus.Applied, scene.Result(intent).Status);
        Assert.Equal(ExperimentOutcomeState.Inconclusive, scene.Read(experiment.Id).Outcome);
    }

    [Fact]
    public void Submit_ValidForm_EnqueuesOneAttributedWorkspaceIntent()
    {
        using var scene = new Scene();
        var form = new OwnerExperimentDecisionForm("confirmed", " evidence ", " action ");
        var submitted = new ExperimentDecideIntentSubmitter(scene.Workspace).Submit("1a2b3c4d", form);
        var intent = Assert.Single(scene.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult());
        Assert.Equal(submitted.IntentId, intent.Id);
        Assert.Equal(OperatorIntentVerbs.ExperimentDecide, intent.Verb);
        Assert.Equal(OperatorIntentScopes.Workspace, intent.GoalId);
        Assert.Null(intent.TaskId);
        Assert.Equal(new ExperimentDecideOperatorIntentPayload("1a2b3c4d", form.Outcome, form.Evidence, form.Action),
            JsonSerializer.Deserialize<ExperimentDecideOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options));
        Assert.Equal("operator", intent.Actor);
        Assert.Equal("owner-console", intent.Channel);
        Assert.Equal("local-process", intent.AuthenticationAssurance);
        Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Assert.True(Guid.TryParseExact(intent.IdempotencyKey, "n", out _));
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
    }

    [Fact]
    public void Submit_BlankAction_ThrowsWithoutEnqueueing()
    {
        using var scene = new Scene();
        var error = Assert.Throws<ArgumentException>(() => new ExperimentDecideIntentSubmitter(scene.Workspace)
            .Submit("1a2b3c4d", new("confirmed", "receipt", " \n ")));
        Assert.Equal("action is required.", error.Message);
        Assert.Empty(scene.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult());
    }

    [Fact]
    public void ParseOutcome_InvalidWord_PreservesCliError()
    {
        Assert.Equal("outcome: expected confirmed, refuted or inconclusive.",
            Assert.Throws<ArgumentException>(() => ExperimentDecisionApplier.ParseOutcome("open")).Message);
    }

    private sealed class Scene : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "experiment-decide-" + Guid.NewGuid().ToString("n"));
        internal OrchestratorWorkspace Workspace { get; }
        internal ExperimentStore Experiments { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        private readonly OperatorIntentCoordinator _coordinator;
        internal Scene()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Experiments = new(Workspace.ExperimentStorePath);
            Intents = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            _coordinator = new(Intents)
            { ExperimentDecisions = new ExperimentDecideIntentHandler(Workspace.ExperimentStorePath) };
        }
        internal ExperimentRecord Add() => Experiments.AddAsync(ExperimentFlagTestFixture.Spec()).GetAwaiter().GetResult();
        internal ExperimentRecord Read(string id) => Experiments.ResolveAsync(id).GetAwaiter().GetResult()!;
        internal OperatorIntentRecord Submit(ExperimentDecideOperatorIntentPayload payload,
            string assurance = "local-process", string? json = null)
        {
            var id = Guid.NewGuid().ToString("n");
            return Intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.ExperimentDecide,
                OperatorIntentScopes.Workspace, null, json ?? JsonSerializer.Serialize(payload, OperatorIntentJson.Options),
                [], "operator", "owner-console", assurance, DateTimeOffset.UtcNow,
                ActorKind: OperatorActorKind.Human)).GetAwaiter().GetResult();
        }
        internal void Tick() => _coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());
        internal OperatorIntentRecord Result(OperatorIntentRecord intent) => Intents.GetAsync(intent.Id).GetAwaiter().GetResult()!;
        internal void AssertRejected(OperatorIntentRecord intent, string reason)
        {
            var result = Result(intent);
            Assert.Equal(OperatorIntentStatus.Rejected, result.Status);
            Assert.Equal($"Rejected experiment-decide: {reason}", result.Outcome);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
