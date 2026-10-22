using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedGateIdentityInspector
{
    internal static bool HasCurrentPassedCohortIdentity(
        ConductorAcceptanceCohortSelection selection, AcceptanceCohortReceipt receipt,
        AgentOrchestratorKernel? kernel, OrchestratorWorkspace? workspace, IGoalAcceptanceVerifier? verifier,
        string integrationBranch, GoalWorktreeCleanupHooks cleanupHooks)
    {
        if (kernel is null || workspace is null || verifier is null)
        {
            return false;
        }

        var goalsById = kernel.Goals.ToDictionary(goal => goal.Id);
        if (selection.Members.Any(member => !goalsById.ContainsKey(member.GoalId))) return false;
        var bindings = selection.BindMembers(id => goalsById[id].AuthoritativeBrief.Version);
        try
        {
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                workspace.ExecutionDirectory,
                selection.Members[0].MainRevision,
                bindings, integrationBranch,
                cleanupHooks);
            var manifest = verifier.ComputeEffectivePlanIdentity(
                integration.Path,
                bindings.SelectMany(member => member.LandingPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            return string.Equals(
                AcceptanceCohortIdentity.Create(bindings, selection.Members[0].MainRevision,
                    integration.TreeRevision, manifest).Value,
                receipt.Identity.Value,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is AcceptanceCohortMaterializationException or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool HasCurrentPassedMergeTrainIdentity(
        ConductorMergeTrainSelection selection, MergeTrainReceipt receipt,
        AgentOrchestratorKernel? kernel, OrchestratorWorkspace? workspace, IGoalAcceptanceVerifier? verifier,
        string integrationBranch, GoalWorktreeCleanupHooks cleanupHooks)
    {
        if (kernel is null || workspace is null || verifier is null)
        {
            return false;
        }
        var goalsById = kernel.Goals.ToDictionary(goal => goal.Id);
        if (selection.Members.Any(member => !goalsById.ContainsKey(member.GoalId))) return false;
        try
        {
            using var integration = GoalWorktrees.CreateMergeTrainWorkspace(
                workspace.ExecutionDirectory,
                selection.Members[0].MainRevision,
                selection.BindMembers(id => goalsById[id].AuthoritativeBrief.Version), integrationBranch,
                cleanupHooks);
            if (integration.Members.Count != receipt.Identity.Members.Count)
            {
                return false;
            }
            var changedFiles = integration.Members.SelectMany(member => member.LandingPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var manifest = verifier.ComputeEffectivePlanIdentity(integration.Path, changedFiles);
            return string.Equals(MergeTrainIdentity.Create(integration.Members,
                    selection.Members[0].MainRevision, integration.TreeRevision, manifest).Value,
                receipt.Identity.Value, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

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
