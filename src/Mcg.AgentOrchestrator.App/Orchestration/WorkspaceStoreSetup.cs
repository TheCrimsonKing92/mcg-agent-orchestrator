using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkspaceStoreSetup
{
    internal static IReadOnlyList<StoreSetupResult> Run(OrchestratorWorkspace workspace, Action? verifyBinding = null)
    {
        verifyBinding?.Invoke();
        var plan = StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true);
        StoreSetupRunner.RequireCompatible(plan);
        // Current-schema setup needs no owner probe and invokes no migration actions.
        if (!plan.Any(entry => entry.RequiresMigration)) return StoreSetupRunner.Run(workspace.OrchestratorDirectory);
        using var lease = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        verifyBinding?.Invoke();
        return RunWithOwnership(workspace);
    }

    internal static IReadOnlyList<StoreSetupResult> RunWithOwnership(OrchestratorWorkspace workspace, bool requireCurrent = false)
    {
        var plan = StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true);
        StoreSetupRunner.RequireCompatible(plan, requireCurrent);
        var results = StoreSetupRunner.Run(workspace.OrchestratorDirectory);
        if (plan.Single(entry => entry.StoreName == "state-migrations").RequiresMigration)
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        return results;
    }

    internal static void EnsureState(OrchestratorWorkspace workspace)
    {
        // Preserve the cheap existing probe for ordinary current-schema commands.
        // Only a requested migration needs the complete noncreating setup inventory.
        if (StateDbMigrations.IsUpToDate(workspace.SqliteStatePath)) return;
        var plan = StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true);
        StoreSetupRunner.RequireCompatible(plan);
        if (!plan.Single(entry => entry.StoreName == "state-migrations").RequiresMigration) return;
        using var lease = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        // A later incompatible store must prevent earlier state migrations too.
        StoreSetupRunner.RequireCompatible(StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
    }
}
