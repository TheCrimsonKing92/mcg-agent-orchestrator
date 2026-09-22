using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.Data.Sqlite;

public sealed class TerminalGoalSweepOwnedRootTests
{
    [Xunit.Theory]
    [Xunit.InlineData(5)]
    [Xunit.InlineData(6)]
    public void Sqlite_deferral_is_observable(int sqliteErrorCode)
    {
        var result = TerminalGoalSweep.ExecuteOwnedBuildRootReap(
            () => throw new SqliteException("injected owned-root registry hold", sqliteErrorCode));

        Assert.NotNull(result.DeferredReason);
        Assert.Contains($"sqlite code {sqliteErrorCode}", result.DeferredReason, StringComparison.Ordinal);
        Assert.Contains("injected owned-root registry hold", result.DeferredReason, StringComparison.Ordinal);
        Assert.Equal(0, result.ProcessedCount);
    }

    [Xunit.Fact]
    public void Console_view_reports_owned_root_deferral_and_observe_only_roots()
    {
        var result = new TerminalGoalSweepResult([])
        {
            OwnedRoots = new TerminalGoalSweepOwnedRootResult(
                ProcessedCount: 0,
                RemovedCount: 0,
                RetainedCount: 0,
                FailedCount: 0,
                ObserveOnlyReports: [@"C:\build-runs\legacy-root"],
                DeferredReason: "owned-root reap deferred: synthetic registry hold")
        };

        var output = AsyncLocalConsoleRouter.Capture(() => ConsoleViews.PrintTerminalGoalSweep(result));

        Assert.Contains("SWEEP_OWNED_ROOT_DEFERRED", output, StringComparison.Ordinal);
        Assert.Contains("synthetic registry hold", output, StringComparison.Ordinal);
        Assert.Contains("SWEEP_OWNED_ROOT_OBSERVED", output, StringComparison.Ordinal);
        Assert.Contains(@"C:\build-runs\legacy-root", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Console_view_omits_empty_owned_root_output()
    {
        var result = new TerminalGoalSweepResult([])
        {
            OwnedRoots = new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, [])
        };

        var output = AsyncLocalConsoleRouter.Capture(() => ConsoleViews.PrintTerminalGoalSweep(result));

        Assert.Empty(output);
    }
}
