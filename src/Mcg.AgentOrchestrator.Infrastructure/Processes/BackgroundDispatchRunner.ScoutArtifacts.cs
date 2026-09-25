using Mcg.AgentOrchestrator.Core;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static bool RequiresDurableResearchArtifact(Goal goal, TaskSpec researcher)
    {
        var researcherIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == researcher.Id);
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Planner);
        return researcherIndex >= 0 && plannerIndex > researcherIndex;
    }

    private static bool RequiresDurablePlanArtifact(Goal goal, TaskSpec planner)
    {
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == planner.Id);
        return ScoutRoundPolicy.IsScoutPlanner(goal, planner) ||
            (plannerIndex > 0 && goal.Tasks.Take(plannerIndex).Any(candidate => candidate.RequiredRole == AgentRole.Researcher));
    }

    private static bool TryPersistScoutResearch(string selectedSourcePath, string standardOutputPath, out string diagnostic)
    {
        var captured = ResearcherOutputContract.ReadCapturedOutputTail(selectedSourcePath);
        var planStart = Regex.Match(captured, @"(?im)^\s*#{1,6}\s+Premise\s+Validity\b");
        var researchOutput = planStart.Success ? captured[..planStart.Index] : captured;
        var contract = ResearcherOutputContract.Resolve(researchOutput);
        if (!contract.Succeeded || contract.Research is null)
        {
            diagnostic = AppendDiagnostic(
                contract.Diagnostic.Replace("Retry Researcher", "Retry Planner", StringComparison.Ordinal),
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected));
            return false;
        }

        if (!ResearcherOutputContract.TryPersistDurableReceipt(
                standardOutputPath,
                contract.Research,
                out var persistenceDiagnostic))
        {
            diagnostic = AppendDiagnostic(
                $"Scout research output contract could not persist the accepted artifact: {persistenceDiagnostic}. Retry Planner for contract repair.",
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed));
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }
}
