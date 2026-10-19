using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class ConductorStartupLockScenarioTests
{
    [Theory]
    [InlineData("store-plan")]
    [InlineData("store-setup")]
    [InlineData("state-upgrade")]
    public void StartupSurvivesTwoLocksAndReportsReadinessWithRealLeaseAndState(string operation)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        using var lease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);
        var elapsed = TimeSpan.Zero;
        var calls = 0;
        var sleeps = new List<TimeSpan>();
        var options = new StartupSqliteRetryOptions(delay => { sleeps.Add(delay); elapsed += delay; }, () => elapsed,
            (_, step) => { if (step == operation && ++calls <= 2) throw new SqliteException("database is locked", 5); });
        WorkspaceStoreSetup.RunWithOwnership(workspace, retry: options);
        var lines = new List<string>();
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        CliPersistentStateRunner.RunConductPreLoopStartup(
            () => ConductorLoopLease.IsActive(workspace.OrchestratorDirectory),
            () => repository.LoadAsync().GetAwaiter().GetResult(),
            _ => "LOOP_READY lock=acquired state=loaded", lines.Add, state => state);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)], sleeps);
        Assert.True(elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal("LOOP_READY lock=acquired state=loaded", Assert.Single(lines));
    }

    [Fact]
    public void PersistentStartupLockStopsAtBudgetWithDiagnosticContext()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        using var lease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);
        var elapsed = TimeSpan.Zero;
        var calls = 0;
        var options = new StartupSqliteRetryOptions(delay => elapsed += delay, () => elapsed, (_, step) =>
        {
            Assert.Equal("store-plan", step);
            calls++;
            throw new SqliteException("database is locked", 5);
        });
        var error = Assert.Throws<SqliteException>(() => WorkspaceStoreSetup.RunWithOwnership(workspace, retry: options));
        Assert.Equal(TimeSpan.FromSeconds(30), elapsed);
        Assert.Equal(calls, error.Data["Mcg.AttemptCount"]);
        var report = ProgramStartupErrorFormatter.Format(error);
        Assert.Contains($"database={workspace.OrchestratorDirectory}", report);
        Assert.Contains("operation=store-plan", report);
        Assert.Contains($"attempts={calls}", report);
        Assert.Contains("stack:" + Environment.NewLine, report);
        Assert.Contains(nameof(PersistentStartupLockStopsAtBudgetWithDiagnosticContext), report);
    }

    [Fact]
    public void InjectedPolicySetupDelegateIsStillCalledExactlyOnce()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var error = new StringWriter();
        var calls = 0;
        var resolution = CliConductLoopStartup.PrepareConductLoopPolicy(
            fixture.Workspace.OrchestratorDirectory, "Manual", error, _ =>
            {
                calls++;
                throw new SqliteException("locked", 5);
            });
        Assert.NotNull(resolution);
        Assert.Equal(1, calls);
        Assert.Equal($"Warning: store setup deferred: locked{Environment.NewLine}", error.ToString());
    }
}
