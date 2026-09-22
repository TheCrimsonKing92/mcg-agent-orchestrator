using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Runtime.CompilerServices;
using Xunit;

[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class RuntimeMaintenanceOwnershipTests
{
    [Fact]
    public void RuntimeConductorJanitorialPhaseInvokesSchedulerWithoutDashboardHost()
    {
        var root = CreateRepositoryRoot();
        try
        {
            var schedulerInvocations = 0;
            var scheduler = new GoalWorktreeOrphanSweepScheduler(new GoalWorktreeCleanupHooks
            {
                CleanupUtcNow = () =>
                {
                    schedulerInvocations++;
                    return DateTimeOffset.Parse("2026-09-19T00:00:00Z");
                }
            });

            new ConductorBatchLoop(sweep: kernel => scheduler.SweepIfDue(root, kernel)).Run(
                new AgentOrchestratorKernel(),
                CreateDriver(),
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, "stop"),
                maxIterations: 1);

            Assert.Equal(1, schedulerInvocations);
            Assert.DoesNotContain(
                AppDomain.CurrentDomain.GetAssemblies(),
                assembly => assembly.GetName().Name == "Mcg.AgentOrchestrator.Dashboard");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DashboardSourceHasNoMaintenanceSweepOwner()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dashboardRoot = Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Dashboard");
        var dashboardSource = Directory.EnumerateFiles(dashboardRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        var runtimeSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Cli",
            "CliCommandHandlers.Goals.cs"));

        Assert.DoesNotContain(dashboardSource, source =>
            source.Contains("SweepNow(", StringComparison.Ordinal) ||
            source.Contains("SweepIfDue(", StringComparison.Ordinal) ||
            source.Contains("AddHostedService", StringComparison.Ordinal) ||
            source.Contains("BackgroundService", StringComparison.Ordinal) ||
            source.Contains("PeriodicTimer", StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(runtimeSource, "context.CleanupContext.Scheduler.SweepIfDue"));
        Assert.Contains("measuredSweepWithCheckpointHolds: reconcileSweep", runtimeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledDashboardLifetimeDoesNotCancelRuntimeConductorJanitorialPhase()
    {
        using var dashboardLifetime = new CancellationTokenSource();
        using var dashboardStarted = new ManualResetEventSlim();
        using var dashboardStopped = new ManualResetEventSlim();
        using var sweepEntered = new ManualResetEventSlim();
        using var sweepCompleted = new ManualResetEventSlim();
        var root = CreateRepositoryRoot();
        try
        {
            var schedulerInvocations = 0;
            var scheduler = new GoalWorktreeOrphanSweepScheduler(new GoalWorktreeCleanupHooks
            {
                CleanupUtcNow = () =>
                {
                    schedulerInvocations++;
                    return DateTimeOffset.Parse("2026-09-19T00:00:00Z");
                }
            });
            var dashboard = Task.Run(() =>
            {
                dashboardStarted.Set();
                dashboardLifetime.Token.WaitHandle.WaitOne();
                dashboardStopped.Set();
            });
            Assert.True(dashboardStarted.Wait(TimeSpan.FromSeconds(5)), "Dashboard lifetime did not start.");

            var runtime = Task.Run(() =>
                new ConductorBatchLoop(sweep: kernel =>
                {
                    sweepEntered.Set();
                    Assert.True(
                        dashboardStopped.Wait(TimeSpan.FromSeconds(5)),
                        "Runtime sweep did not observe dashboard shutdown.");
                    scheduler.SweepIfDue(root, kernel);
                    sweepCompleted.Set();
                }).Run(
                    new AgentOrchestratorKernel(),
                    CreateDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    Path.Combine(root, "stop"),
                    maxIterations: 1));

            Assert.True(sweepEntered.Wait(TimeSpan.FromSeconds(5)), "Runtime janitorial phase did not start.");
            dashboardLifetime.Cancel();
            Assert.True(dashboardStopped.Wait(TimeSpan.FromSeconds(5)), "Dashboard lifetime did not stop.");
            Assert.True(sweepCompleted.Wait(TimeSpan.FromSeconds(5)), "Runtime janitorial phase was cancelled with the dashboard.");
            Assert.True(runtime.Wait(TimeSpan.FromSeconds(5)), "Runtime conductor did not finish its bounded tick.");
            Assert.True(dashboard.Wait(TimeSpan.FromSeconds(5)), "Dashboard lifetime task did not finish.");
            Assert.Equal(1, schedulerInvocations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRepositoryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "runtime-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static ConductorDriver CreateDriver() =>
        new(
            _ => GoalLifecycleFacts.None,
            () => 0,
            _ => "/tmp/workspace",
            _ => DispatchStartOutcome.Started(),
            null,
            null,
            _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            null,
            null,
            null,
            null,
            null,
            _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "OK",
                [],
                null),
            (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"),
            null,
            _ => { },
            _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            (_, _, _) => { },
            _ => null);
}
