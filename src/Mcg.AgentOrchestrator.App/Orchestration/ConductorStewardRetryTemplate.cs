using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStewardRetryTemplate
{
    internal static string? Compose(ConductorStewardTrigger trigger, ConductorStewardAdjudication adjudication)
    {
        if (trigger.AcceptanceCriteria.Count == 0 || string.IsNullOrWhiteSpace(trigger.Evidence) ||
            string.IsNullOrWhiteSpace(adjudication.Text) || string.IsNullOrWhiteSpace(adjudication.Instruction))
            return null;
        var frame = new StringBuilder();
        frame.AppendLine("Current refined acceptance criteria:");
        for (var index = 0; index < trigger.AcceptanceCriteria.Count; index++)
            frame.AppendLine($"{index + 1}. {trigger.AcceptanceCriteria[index]}");
        frame.AppendLine().AppendLine($"Case {trigger.CaseLetter} evidence:").AppendLine(trigger.Evidence);
        frame.AppendLine().AppendLine("Decision procedure:");
        frame.AppendLine(trigger.Kind switch
        {
            ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed =>
                "Run the named failing test against the same candidate and input; quote its assertion, then fix that failure and recheck it.",
            ConductorStewardTriggerKind.PlannerOutputContractRejected =>
                "Check each plan citation against an exact tracked path at HEAD and each required mapping field against the output contract.",
            _ => "Find the named class and collection; rename the class so its acceptance lane substring matches the collection, then check the collection guard."
        });
        frame.AppendLine().AppendLine("Steward diagnosis:").AppendLine(adjudication.Text);
        frame.AppendLine().AppendLine("Steward instruction:").AppendLine(adjudication.Instruction);
        return frame.ToString();
    }
}
