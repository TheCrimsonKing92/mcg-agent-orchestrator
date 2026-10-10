using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Files a once-keyed make-permanent follow-up before confirming an applied flag.</summary>
internal sealed class ConductorExperimentFlagKeepController(ExperimentStore experiments,
    Func<BacklogStore> backlogStore, string policyPath)
{
    internal static string BacklogId(string experimentId) => $"experiment-flag-keep-{experimentId}";

    internal bool TryKeep(ExperimentRecord record, ExperimentReadingResult reading, DateTimeOffset now)
    {
        if (record.Outcome != ExperimentOutcomeState.Open ||
            record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag ||
            record.Spec.Intervention.FlagTarget is not { PriorValue: not null } target ||
            reading.Verdict != "keep" || !reading.StopRuleMet || reading.GuardrailBreached) return false;
        // Refresh persisted eligibility when a stale reading is delivered after a previous tick.
        if (experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()?.Outcome != ExperimentOutcomeState.Open) return false;
        // Prior capture precedes the write and cannot prove that the intervention took effect.
        if (!File.Exists(policyPath) || ConductorPolicyBooleanFlags.Read(
                ConductorAutonomyPolicy.ParseJson(File.ReadAllText(policyPath)), target.PropertyName) != target.ValueToApply)
            return false;

        var id = BacklogId(record.Id);
        var action = $"Make {target.PropertyName}={target.ValueToApply} the default and remove the flag";
        // An existing item is success too: replay after a failed decision must preserve it.
        backlogStore().UpsertAsync(new BacklogItem(id,
            $"Make {target.PropertyName}={target.ValueToApply} permanent and remove the flag",
            $"Experiment {record.Id} has a keep reading at its stop rule with a clear guardrail. " +
            $"{action}. Hypothesis: {record.Spec.Hypothesis}",
            BacklogItemStatus.Open, now, now, null)).GetAwaiter().GetResult();
        experiments.DecideAsync(record.Id, ExperimentOutcomeState.Confirmed, $"backlog:{id}",
            $"{action} (backlog {id}).").GetAwaiter().GetResult();
        return true;
    }
}
