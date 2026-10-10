using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Submits a durable, once-keyed revert; keep remains an owner decision.</summary>
internal sealed class ConductorExperimentFlagRevertController(ExperimentStore experiments,
    Func<IOperatorIntentStore> intentStore, ExperimentFlagStateReader flags)
{
    internal bool TryRevert(ExperimentRecord record, ExperimentReadingResult reading, DateTimeOffset now)
    {
        if (record.Outcome != ExperimentOutcomeState.Open ||
            record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag ||
            record.Spec.Intervention.FlagTarget is not { PriorValue: not null } target ||
            !(reading.Verdict == "revert" || reading.GuardrailBreached)) return false;
        // Refresh persisted eligibility when a stale reading is delivered after a previous tick.
        if (experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()?.Outcome != ExperimentOutcomeState.Open) return false;
        // Prior capture precedes the write and cannot prove that the intervention took effect.
        if (flags.Read(target) != target.ValueToApply)
            return false;
        var intents = intentStore();
        var id = Guid.NewGuid().ToString("n");
        var intent = intents.EnqueueAsync(new OperatorIntentRecord(id, $"experiment-revert-flag:{record.Id}",
            OperatorIntentVerbs.ExperimentRevertFlag, OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(new ExperimentRevertFlagOperatorIntentPayload(record.Id), OperatorIntentJson.Options),
            [], "conductor", "conductor-experiment-revert", OperatorIntentAdjudication.StewardAssurance, now,
            ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
        if (intent.Status == OperatorIntentStatus.Rejected)
            throw new InvalidOperationException($"experiment-revert-intent-rejected {intent.Id}: {intent.Outcome}");
        experiments.DecideAsync(record.Id, ExperimentOutcomeState.Refuted, $"operator-intent:{intent.Id}",
            $"Revert {target.PropertyName} to {target.PriorValue} via experiment-revert-flag.").GetAwaiter().GetResult();
        return true;
    }
}
