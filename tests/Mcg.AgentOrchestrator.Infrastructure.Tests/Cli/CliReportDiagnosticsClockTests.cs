using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliReportDiagnosticsClockTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public void FailureTriage_FixedClockCrossesRetryAfter_ChangesActiveItem()
    {
        var root = CreateTempDirectory();
        try
        {
            var retryAfter = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
            var clock = new FixedClock(retryAfter.AddHours(-2));
            var kernel = CreateActiveDispatchSeed(root, clock);
            var snapshot = kernel.ExportGoalSnapshot(kernel.Goals.Single().Id);
            snapshot = snapshot with
            {
                Tasks = [snapshot.Tasks.Single() with { SubscriptionRetryAfter = retryAfter }]
            };
            kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []), clock);
            Xunit.Assert.Equal(GoalStatus.Active, kernel.Goals.Single().Status);
            Xunit.Assert.Equal(retryAfter, kernel.Goals.Single().Tasks.Single().SubscriptionRetryAfter);

            var before = WriterOutput(root, kernel, "failure-triage", new FixedClock(retryAfter.AddHours(-1)));
            var after = WriterOutput(root, kernel, "failure-triage", new FixedClock(retryAfter.AddHours(1)));

            Xunit.Assert.Contains("Subscription retry-after is active until", before);
            Xunit.Assert.DoesNotContain("Subscription retry-after is active until", after);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalTiming_FixedClock_OutputIsStableAndChangesWithSampleTime()
    {
        var root = CreateTempDirectory();
        try
        {
            var startedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
            var kernel = CreateActiveDispatchSeed(root, new FixedClock(startedAt));
            var clock = new FixedClock(startedAt.AddHours(1));
            Xunit.Assert.Equal(GoalStatus.Active, kernel.Goals.Single().Status);
            Xunit.Assert.NotNull(kernel.Goals.Single().Tasks.Single().LastDispatch);

            var first = WriterOutput(root, kernel, "goal-timing", clock);
            var repeat = WriterOutput(root, kernel, "goal-timing", clock);
            var later = WriterOutput(root, kernel, "goal-timing", new FixedClock(clock.UtcNow.AddHours(1)));

            Xunit.Assert.Contains("Goal timing abc10000", first);
            Xunit.Assert.Contains("e2eEndedAt=2026-09-24 01:00:00Z e2eEndSource=sample-time", first);
            Xunit.Assert.Contains("e2eEndedAt=2026-09-24 02:00:00Z e2eEndSource=sample-time", later);
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(repeat));
            Xunit.Assert.NotEqual(first, later);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static AgentOrchestratorKernel CreateActiveDispatchSeed(string root, IClock clock)
    {
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Active timing dispatch", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"),
            "Fixed-clock report goal", [task]);
        kernel.ActivateGoal(goal.Id, CliSingleGoalReportReadOnlyRouteTests.ReportAgents());
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "seeded-timing-dispatch", root, clock.UtcNow, ProviderName: "OpenAI"));
        return kernel;
    }

    private static string WriterOutput(string root, AgentOrchestratorKernel kernel, string verb, IClock clock)
    {
        IReadOnlyList<AgentDefinition> agents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliCommandDispatcher.ExecuteCommand(
            [verb, "abc10000"], kernel, OrchestratorWorkspace.ForDirectory(root), ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal, diagnosticsClock: clock)));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
