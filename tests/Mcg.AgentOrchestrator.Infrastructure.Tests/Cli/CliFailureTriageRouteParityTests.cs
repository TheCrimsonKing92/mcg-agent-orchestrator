using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its workspace and seed; collection placement mirrors the timing parity tests.
[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliFailureTriageRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(-1, true)]
    [Xunit.InlineData(1, false)]
    public void FixedClock_RetryAfterBoundary_OutputMatchesWriterBytes(int hoursFromRetry, bool retryActive)
    {
        var root = CreateTempDirectory();
        try
        {
            var retryAfter = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
            var seedClock = new FixedClock(retryAfter.AddHours(-2));
            var kernel = CliReportDiagnosticsClockTests.CreateActiveDispatchSeed(root, seedClock);
            var snapshot = kernel.ExportGoalSnapshot(kernel.Goals.Single().Id);
            snapshot = snapshot with
            {
                Tasks = [snapshot.Tasks.Single() with { SubscriptionRetryAfter = retryAfter }]
            };
            kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []), seedClock);
            var goal = kernel.Goals.Single();
            Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
            Xunit.Assert.NotNull(goal.Tasks.Single().LastDispatch);
            Xunit.Assert.Equal(retryAfter, goal.Tasks.Single().SubscriptionRetryAfter);

            var clock = new FixedClock(retryAfter.AddHours(hoursFromRetry));
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            string[] args = ["failure-triage", "abc10000"];
            var providers = new InMemoryModelProviderRegistry([]);
            IReadOnlyList<AgentDefinition> writerAgents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
            var writerProfiles = WorkerProfileCatalog.Default();
            Goal? writerGoal = goal;
            var writer = CaptureConsole(() => Xunit.Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref writerAgents, providers, ref writerProfiles, ref writerGoal,
                diagnosticsClock: clock)));

            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> readAgents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
            var readProfiles = WorkerProfileCatalog.Default();
            Goal? readGoal = null;
            var read = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref readAgents, ref readProfiles, ref readGoal, out var changed, clock));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains("Failure triage goal: abc10000", writer.Split(Environment.NewLine));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(writer), Encoding.UTF8.GetBytes(read));
            if (retryActive)
            {
                Xunit.Assert.Contains("Subscription retry-after is active until", writer);
                Xunit.Assert.Contains("Subscription retry-after is active until", read);
            }
            else
            {
                Xunit.Assert.DoesNotContain("Subscription retry-after is active until", writer);
                Xunit.Assert.DoesNotContain("Subscription retry-after is active until", read);
            }
            Xunit.Assert.Equal(goal.Id, readGoal!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
