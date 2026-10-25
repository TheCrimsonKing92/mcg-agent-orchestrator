using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal static class GoalLifecycleFactsReader
{
    public static GoalLifecycleFacts Read(
        OrchestratorWorkspace workspace,
        Goal goal,
        bool? hasOpenClarification = null)
    {
        var executionDirectory = workspace.ExecutionDirectory;
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var workspaceExists = worktree is not null;
        var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
        var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
        var isRecorded = GoalOperationJournal.HasCompletedRecordEvidence(journal);
        var isCleanedUp = GoalOperationJournal.HasCompletedCleanupEvidence(journal);
        var resolvedHasOpenClarification = hasOpenClarification ??
            GoalRefinementGate.HasOpenClarification(workspace, goal);
        var isBlocked = GoalAcceptanceStatusProjector.HasCurrentBlockingAcceptanceState(goal, executionDirectory, journal) ||
            journal.LatestByOperation.Any(entry =>
                entry.Status == GoalOperationStatus.Failed &&
                (entry.Operation.Contains("land", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Contains("cleanup", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Contains("workspace:remove", StringComparison.OrdinalIgnoreCase)));
        return new GoalLifecycleFacts(workspaceExists, isBlocked, isMerged, isRecorded, isCleanedUp, resolvedHasOpenClarification);
    }
}
