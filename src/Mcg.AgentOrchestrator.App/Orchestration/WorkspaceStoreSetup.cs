using Mcg.AgentOrchestrator.App.Cli;
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
        RefusePendingSetupWithLiveOwner(workspace, plan);
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
        RefusePendingSetupWithLiveOwner(workspace, plan);
        using var lease = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        // A later incompatible store must prevent earlier state migrations too.
        StoreSetupRunner.RequireCompatible(StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
    }

    internal static bool DefersStateUpgradeToLiveOwner(OrchestratorWorkspace workspace, IReadOnlyList<string> args)
    {
        if (!PendingStoreSetupGate.AllowsWhilePending(args) || StateDbMigrations.IsUpToDate(workspace.SqliteStatePath))
            return false;

        var plan = StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true);
        StoreSetupRunner.RequireCompatible(plan);
        var state = plan.Single(entry => entry.StoreName == "state-migrations");
        if (state.RecordedVersion is null || !state.RequiresMigration) return false;
        using (var snapshot = new StoreInspectionSnapshot(workspace.SqliteStatePath))
            if (!snapshot.ReadJournalMode().Equals("wal", StringComparison.OrdinalIgnoreCase)) return false;

        // Backlog creation can also assign an epic in portfolio; intent verbs only append to the inbox.
        var usesBacklog = CliPersistentStateRunner.RequiresKernelBacklogState(args);
        if (plan.Any(entry => entry.RequiresMigration && (usesBacklog
                ? entry.StoreName == StoreSchemaRegistry.Backlog.StoreName || entry.StoreName == StoreSchemaRegistry.Portfolio.StoreName
                : entry.StoreName == StoreSchemaRegistry.OperatorIntents.StoreName)))
            return false;

        return ConductorLoopLease.IsActive(workspace.OrchestratorDirectory);
    }

    private static void RefusePendingSetupWithLiveOwner(OrchestratorWorkspace workspace, IReadOnlyList<StoreSetupPlanEntry> plan)
    {
        if (ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
            throw new InvalidOperationException(PendingStoreSetupGate.Describe(plan,
                new SystemConductorLockProbe().ActiveOwnerPid(workspace)));
    }
}
