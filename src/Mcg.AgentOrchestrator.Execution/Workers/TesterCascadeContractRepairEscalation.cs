using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TesterCascadeContractRepairEscalation
{
    internal const string RuleId = "tester-terminal-contract-malformed";
    internal const string RepairEventPrefix = "Tester terminal contract repair ";
    internal const string MalformedFragment = "; same-role retry: terminal_contract=Malformed;";

    internal static bool ShouldEscalate(int newerDispatches, IEnumerable<ProgressEvent> laterEvents) =>
        newerDispatches == 0 && laterEvents.Any(e =>
            e.Kind == ProgressKind.TaskRetried &&
            e.Message.StartsWith(RepairEventPrefix, StringComparison.Ordinal) &&
            e.Message.Contains(MalformedFragment, StringComparison.Ordinal));
}
