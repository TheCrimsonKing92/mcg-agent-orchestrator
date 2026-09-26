using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalSweepObserveOnceTests
{
    [Fact]
    public void Unchanged_unregistered_roots_are_reported_once_per_sweep_state()
    {
        var temp = Directory.CreateTempSubdirectory("goal-observe-").FullName;
        try
        {
            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            var db = Path.Combine(temp, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var runRoot = Directory.CreateDirectory(Path.Combine(storage.RootPath, "runs", "unregistered")).FullName;
            var goalRoot = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(GoalId.New(), storage).RootPath;
            var state = new TerminalGoalSweep.OwnedRootSweepState();

            var first = TerminalGoalSweep.ReapOwnedBuildRootsCore(db, storage, state);
            var second = TerminalGoalSweep.ReapOwnedBuildRootsCore(db, storage, state);
            var fresh = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                db, storage, new TerminalGoalSweep.OwnedRootSweepState());

            foreach (var root in new[] { runRoot, goalRoot })
            {
                Assert.Single(first.ObserveOnlyReports.Where(report => report.Contains(root, StringComparison.Ordinal)));
                Assert.DoesNotContain(second.ObserveOnlyReports, report => report.Contains(root, StringComparison.Ordinal));
                Assert.Single(fresh.ObserveOnlyReports.Where(report => report.Contains(root, StringComparison.Ordinal)));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
