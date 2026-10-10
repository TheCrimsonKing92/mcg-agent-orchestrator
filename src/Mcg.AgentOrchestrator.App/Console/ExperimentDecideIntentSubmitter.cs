using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class ExperimentDecideIntentSubmitter(OrchestratorWorkspace workspace)
{
    internal OwnerAnswerSubmission Submit(string experimentReference, OwnerExperimentDecisionForm form)
    {
        if (string.IsNullOrWhiteSpace(experimentReference)) throw new ArgumentException("experiment reference is required.");
        if (form.Refusal() is { } refusal) throw new ArgumentException(refusal);
        var id = Guid.NewGuid().ToString("n");
        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(new OperatorIntentRecord(id, Guid.NewGuid().ToString("n"),
                OperatorIntentVerbs.ExperimentDecide, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(new ExperimentDecideOperatorIntentPayload(experimentReference,
                    form.Outcome, form.Evidence, form.Action), OperatorIntentJson.Options),
                [], "operator", "owner-console", "local-process", DateTimeOffset.UtcNow,
                ActorKind: OperatorActorKind.Human)).GetAwaiter().GetResult();
        return new(persisted.Id);
    }
}
