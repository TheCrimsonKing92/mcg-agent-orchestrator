using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static bool HasPendingDeferredNoChangeEvidence(Goal goal)
    {
        var developer = goal.Tasks.LastOrDefault(task =>
            task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed &&
            string.Equals(task.LastVerification?.CompletionVerdictRule,
                "deferred-no-change-round", StringComparison.Ordinal));
        if (developer?.LastVerification is null) return false;
        if (!DeferredNoChangeOutcome.TryParse(developer.LastVerification.StandardError, out var outcome))
            return true;

        var prior = DeferredNoChangeEvidenceIndexLines.Latest(goal, developer.Id, outcome.CandidateSha);
        return prior is not { Outcome: "green", NotRun.Count: 0 } ||
            prior.Selections.Count != outcome.TestClasses.Count ||
            outcome.TestClasses.Any(name => !prior.Selections.Any(selection =>
                selection.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase)));
    }
}
