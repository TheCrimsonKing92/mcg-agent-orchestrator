using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedGateIdentityInspector
{
    internal static (bool Known, string? Difference) InspectTree(ConductorGroupedGateAttempt attempt,
        IReadOnlyList<GateReadyCandidateProjection> projections, string executionDirectory,
        string mainRevision, string integrationBranch, GoalWorktreeCleanupHooks cleanupHooks)
    {
        try
        {
            if (attempt.Kind == "cohort")
            {
                using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                    executionDirectory, mainRevision,
                    new ConductorAcceptanceCohortSelection(projections, []).BindMembers(), integrationBranch,
                    cleanupHooks);
                return (true, integration.TreeRevision == attempt.CombinedTreeRevision ? null : "tree");
            }
            if (attempt.Kind == "train")
            {
                using var integration = GoalWorktrees.CreateMergeTrainWorkspace(
                    executionDirectory, mainRevision,
                    new ConductorMergeTrainSelection(projections).BindMembers(), integrationBranch,
                    cleanupHooks);
                if (!integration.Members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(attempt.Members.Select(member => member.GoalId)))
                    return (true, "members");
                return (true, integration.TreeRevision == attempt.CombinedTreeRevision ? null : "tree");
            }
            throw new InvalidDataException($"Unknown grouped gate kind '{attempt.Kind}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Unavailable materialization is not evidence that identity changed. Keep a provisional
            // hold and retry inspection next tick without admitting an overlapping gate.
            return (false, null);
        }
    }
}
