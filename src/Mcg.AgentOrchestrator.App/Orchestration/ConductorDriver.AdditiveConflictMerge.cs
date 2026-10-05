using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static AdditiveConflictMergeOptions CreateAdditiveConflictMergeOptions(
        AgentOrchestratorKernel kernel, Goal goal, string conductEventsLogPath)
    {
        var frozenPaths = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && request.IsCompleted &&
                !request.WasDismissed && request.SupersededByRequestId is null &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                HumanWaitPolicyDefaults.IsSpecClarificationClass(request.Kind))
            .Select(request => FrozenFactRuling.TryParse(request.AuthoritativeAnswer?.Text))
            .OfType<FrozenFactRuling>()
            .SelectMany(ruling => ruling.AmendedFacts.Select(fact => fact.File))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return new(frozenPaths, line =>
            new ConductEventLogWriter(conductEventsLogPath).Append("rebase-automerge", goal.Id.Value[..8], line));
    }
}
