using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PendingStoreSetupGate
{
    internal static string Describe(IReadOnlyList<StoreSetupPlanEntry> plan, int? ownerPid)
    {
        var owner = ownerPid is { } pid ? $" (pid {pid})" : string.Empty;
        var stores = plan.Where(entry => entry.RequiresMigration).Select(entry =>
            $"{entry.StoreName} ({entry.DatabasePath}): recorded {entry.RecordedVersion?.ToString() ?? "missing"}, required {entry.RequiredVersion}");
        return $"Store schema upgrade pending while the conductor{owner} holds conduct-loop.lock; this command was not run." +
            Environment.NewLine + string.Join(Environment.NewLine, stores) + Environment.NewLine +
            "The conductor's self-relaunch applies a landed forward upgrade; otherwise inspect with `conductor setup --plan`, " +
            "stop the conductor with `conductor stop`, run `conductor setup`, then `conductor start --clear-stop`.";
    }

    internal static bool AllowsWhilePending(IReadOnlyList<string> args) =>
        StoreSetupCommandKinds.IsInboxBackedTaskMutation(args) ||
        StoreSetupCommandKinds.IsCriterionEvidenceMutation(args) ||
        StoreSetupCommandKinds.RequiresBacklogState(args);
}
