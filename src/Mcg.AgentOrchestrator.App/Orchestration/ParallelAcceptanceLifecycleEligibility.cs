using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ParallelAcceptanceLifecycleEligibility
{
    internal static bool IsParallelAcceptanceLifecycleEligible(
        Goal goal,
        ConductorDriver driver)
    {
        if (!driver.ParallelAcceptanceEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(driver.ExecutionDirectory))
        {
            return true;
        }

        try
        {
            var journal = GoalOperationJournal.Read(driver.ExecutionDirectory, goal.Id);
            return !GoalOperationJournal.HasRetiredTerminalDisposition(journal) &&
                !GoalOperationJournal.HasMergeEvidenceTerminalDisposition(journal);
        }
        catch
        {
            return true;
        }
    }

}
