using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Queues a durable arm flip after an alternating experiment's newest stamped gate.</summary>
internal sealed class ConductorExperimentFlagToggleController(Func<IOperatorIntentStore> intentStore,
    ExperimentFlagStateReader flags, Func<bool> gateInFlight)
{
    internal bool TryToggle(ExperimentRecord record, ExperimentHostLedgers ledgers, DateTimeOffset now,
        ExperimentReadingResult? reading = null)
    {
        if (record.Outcome != ExperimentOutcomeState.Open ||
            record.Spec.Baseline.Kind != ExperimentBaselineKind.AlternatingGates ||
            record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag ||
            record.Spec.Intervention.FlagTarget is not { PriorValue: not null } target) return false;
        try { if (gateInFlight()) return false; }
        catch (Exception) { return false; }

        var gate = ledgers.GateLaneTimes.Where(row => row.ObservedAt > record.CreatedAt &&
                ExperimentAlternatingGateReading.ResolveArm(row, record.Id) is not null)
            .OrderByDescending(row => row.ObservedAt)
            .ThenByDescending(row => row.GateAttemptId, StringComparer.Ordinal).FirstOrDefault();
        if (gate is null || flags.Read(target) is not { } current ||
            ExperimentAlternatingGateReading.ResolveArm(gate, record.Id) != (current == target.ValueToApply ? "on" : "off"))
            return false;

        // At the stop rule, settle on the decided arm instead of alternating.
        if (reading is { StopRuleMet: true } && current ==
            (reading.Verdict == "keep" && !reading.GuardrailBreached ? target.ValueToApply : target.PriorValue.Value))
            return false;

        var id = Guid.NewGuid().ToString("n");
        var intent = intentStore().EnqueueAsync(new OperatorIntentRecord(id,
            $"experiment-toggle-flag:{record.Id}:{gate.GateAttemptId}", OperatorIntentVerbs.ExperimentToggleFlag,
            OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(new ExperimentToggleFlagOperatorIntentPayload(record.Id), OperatorIntentJson.Options),
            [], "conductor", "conductor-experiment-toggle", OperatorIntentAdjudication.StewardAssurance, now,
            ActorKind: OperatorActorKind.Agent)).GetAwaiter().GetResult();
        if (intent.Status == OperatorIntentStatus.Rejected)
            throw new InvalidOperationException($"experiment-toggle-intent-rejected {intent.Id}: {intent.Outcome}");
        return intent.Id == id;
    }
}
