using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalBriefRevisionReceipt
{
    internal static void WriteSummary(GoalBriefRevisionResult result, bool resetRefinement)
    {
        var refinedCriteriaReceipt = resetRefinement ? "reset" : result.RefinedCriteriaReDerived
            ? result.RefinedCriteriaCount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unchanged";
        Console.WriteLine(
            $"Goal brief revised: goal={result.GoalId.Value} authoritative=v{result.AuthoritativeVersion.Version} " +
            $"not-yet-started={result.NotYetStartedTaskIds.Count} in-flight={result.InFlightTaskIds.Count} " +
            $"completed-unchanged={result.CompletedTaskIds.Count} refined-criteria={refinedCriteriaReceipt}");
    }
}
