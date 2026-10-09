using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ExperimentFlagTestFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "experiment-flag-" + Guid.NewGuid().ToString("n"));
    internal OrchestratorWorkspace Workspace { get; }
    internal string PolicyPath { get; }
    internal ExperimentStore Experiments { get; }
    internal SqliteOperatorIntentStore Intents { get; }
    internal OperatorIntentCoordinator Coordinator { get; }
    internal static DateTimeOffset Now { get; } = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    internal ExperimentFlagTestFixture()
    {
        Directory.CreateDirectory(Root);
        Workspace = OrchestratorWorkspace.ForDirectory(Root);
        Experiments = new ExperimentStore(Workspace.ExperimentStorePath);
        Intents = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        PolicyPath = Path.Combine(Workspace.OrchestratorDirectory, "conductor-policy.json");
        File.WriteAllText(PolicyPath, PolicyJson());
        Coordinator = new OperatorIntentCoordinator(Intents, utcNow: () => Now)
        {
            ExperimentFlags = new ExperimentFlagIntentHandler(Workspace.ExperimentStorePath, PolicyPath)
        };
    }

    internal static ExperimentSpec Spec(ExperimentFlagTarget? target = null) => new("Trial a conductor boolean",
        new(ExperimentInterventionKind.ConfigFlag, "Trial a conductor boolean",
            target ?? new(ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true)),
        new(ExperimentBaselineKind.BeforeAfterWindow, Now.AddDays(-2), Now.AddDays(-1)),
        ["rounds-per-landing", "landings-per-hour"],
        new("productive-rounds", new("productive-rounds", "<", -10)), new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    internal static string PolicyJson(bool enabled = false)
    {
        var document = JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!;
        document["followerGatesEnabled"] = enabled;
        document["zzOperatorNote"] = JsonNode.Parse("{\"nested\":[1,2,\"keep me\"]}");
        return document.ToJsonString();
    }

    internal ExperimentRecord Add(ExperimentSpec? spec = null) => Experiments.AddAsync(spec ?? Spec()).GetAwaiter().GetResult();

    internal OperatorIntentRecord Submit(string experimentId, bool revert = false, string assurance = "local-process",
        string? idempotencyKey = null, string actor = "owner", string channel = "cli",
        OperatorActorKind actorKind = OperatorActorKind.Human)
    {
        var id = Guid.NewGuid().ToString("n");
        var payload = revert
            ? JsonSerializer.Serialize(new ExperimentRevertFlagOperatorIntentPayload(experimentId), OperatorIntentJson.Options)
            : JsonSerializer.Serialize(new ExperimentApplyFlagOperatorIntentPayload(experimentId), OperatorIntentJson.Options);
        return Intents.EnqueueAsync(new OperatorIntentRecord(id, idempotencyKey ?? id,
            revert ? OperatorIntentVerbs.ExperimentRevertFlag : OperatorIntentVerbs.ExperimentApplyFlag,
            OperatorIntentScopes.Workspace, null, payload, [], actor, channel, assurance, Now,
            ActorKind: actorKind)).GetAwaiter().GetResult();
    }

    internal void Tick() => Coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());
    internal OperatorIntentRecord Result(OperatorIntentRecord intent) => Intents.GetAsync(intent.Id).GetAwaiter().GetResult()!;
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
