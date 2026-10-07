using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static string FormatGoalProgressLine(string label, ConductorAdvanceOutcome outcome, int? slotIndex = null)
    {
        var slot = slotIndex.HasValue ? $" slot=slot-{slotIndex.Value}" : string.Empty;
        var line = outcome switch
        {
            ConductorAdvanceOutcome.Executed e  => $"GOAL goal={label} result=executed state={e.FromState}{slot}",
            ConductorAdvanceOutcome.Held h      => $"GOAL goal={label} result=held state={h.State}{slot} reason={SanitizeReason(h.Reason)}",
            ConductorAdvanceOutcome.Escalated e => $"GOAL goal={label} result=escalated state={e.State}{slot} reason={SanitizeReason(e.Reason)}",
            ConductorAdvanceOutcome.Done d      => $"GOAL goal={label} result=done state={d.State}{slot}",
            _                                   => $"GOAL goal={label} result=unknown{slot}"
        };
        var decision = outcome switch
        {
            ConductorAdvanceOutcome.Held h => h.Decision,
            ConductorAdvanceOutcome.Escalated e => e.Decision,
            ConductorAdvanceOutcome.Done d => d.Decision,
            _ => null
        };
        return decision is null
            ? line
            : $"{line} decision={decision.Stage}/{decision.Rung.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{decision.DiscriminatingEvidence}";
    }
}
