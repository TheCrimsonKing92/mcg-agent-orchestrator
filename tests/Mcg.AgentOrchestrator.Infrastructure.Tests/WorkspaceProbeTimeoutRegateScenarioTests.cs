using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory kernels, injected clocks, and exclusively owned temporary attempt roots.
public sealed class WorkspaceProbeTimeoutRegateScenarioTests : ConductorBatchLoopTests
{
    public WorkspaceProbeTimeoutRegateScenarioTests(ITestOutputHelper output) : base(output) { }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ProbeTimeoutRegatesTwiceAfterBackoffThenEscalates()
    {
        var root = CreateTempDirectory("mcg-probe-timeout-regate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
            var task = goal.Tasks.Single();
            var t0 = new DateTimeOffset(2026, 10, 25, 9, 20, 44, TimeSpan.Zero);
            var now = t0;
            var gateCalls = 0;
            var workerRetries = 0;
            var escalations = new List<string>();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, TrunkBranchName.Default, runInline: true, utcNow: () => now);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    gateCalls++;
                    throw new InvalidOperationException(WorkspaceProbeFaultClassifierTests.F1);
                },
                getLandingFileScopes: _ => ["src/Probe.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator,
                utcNow: () => now,
                retryTask: (goalId, taskId, reason) =>
                {
                    workerRetries++;
                    return kernel.RetryTask(goalId, taskId, reason);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                writeEscalation: (_, _, reason) => escalations.Add(reason));
            var ticks = new List<BatchTickSummary>();
            BatchLoopSummary Tick(int seconds)
            {
                now = t0.AddSeconds(seconds);
                return new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                    NoStopPath(), maxIterations: 1, onTick: tick => ticks.Add(tick));
            }

            var first = Tick(0);
            var attempt = ReadLatestAttempt(root, goal);
            Assert.Equal(1, first.Held);
            Assert.Equal(0, first.Escalated);
            Assert.Equal(1, gateCalls);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, attempt.Outcome);
            Assert.Equal(1, attempt.TransientFailureCount);
            Assert.Equal(t0, attempt.CompletedAt);
            Assert.NotNull(attempt.ReconciledAt);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Contains(goal.Timeline, evt => evt.Message.Contains($"(1/3); not_before={t0.AddSeconds(120):O}", StringComparison.Ordinal) &&
                evt.Message.Contains($"attempt={attempt.AttemptId}", StringComparison.Ordinal));

            Assert.Equal(1, Tick(60).Held);
            Assert.Equal(1, gateCalls);
            Assert.Contains(ticks[^1].ProgressLines!, line => line.StartsWith(
                $"ADMISSION tick=1 result=held reason=workspace-probe-backoff goal={goal.Id.Value[..8]} not_before={t0.AddSeconds(120):O}",
                StringComparison.Ordinal));
            var second = Tick(121);
            Assert.Equal(1, second.Held);
            Assert.Equal(0, second.Escalated);
            Assert.Equal(2, gateCalls);
            Assert.Equal(2, ReadLatestAttempt(root, goal).TransientFailureCount);
            Assert.Equal(1, Tick(181).Held);
            Assert.Equal(2, gateCalls);

            // T0+421 is exactly the second deadline: equality must admit.
            var third = Tick(421);
            Assert.Equal(3, gateCalls);
            Assert.Equal(1, third.Escalated);
            Assert.Equal(3, ReadLatestAttempt(root, goal).TransientFailureCount);
            Assert.Contains(goal.Timeline, evt => evt.TickOutcome?.EscalationKind == nameof(ConductorEscalationKind.BackgroundAcceptanceFailed));
            Assert.Contains("timed out after 60000ms", Assert.Single(escalations), StringComparison.Ordinal);
            Assert.DoesNotContain(escalations, reason => reason.Contains("Unhandled lifecycle state", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(0, workerRetries);
        }
        finally { TryDeleteDirectory(root); }
    }

    public static TheoryData<string> DefinitiveDiagnostics => new()
    {
        WorkspaceProbeFaultClassifierTests.F3,
        WorkspaceProbeFaultClassifierTests.F4,
        WorkspaceProbeFaultClassifierTests.F5
    };

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [MemberData(nameof(DefinitiveDiagnostics))]
    public void DefinitiveFaultMatchesUnclassifiedBaseline(string detail)
    {
        var baseline = ObserveUnclassifiedPath("acceptance attempt wrapper");
        var actual = ObserveUnclassifiedPath(detail);
        Assert.Equal(baseline.FirstHeld, actual.FirstHeld);
        Assert.Equal(baseline.FirstEscalated, actual.FirstEscalated);
        Assert.Equal(baseline.SecondHeld, actual.SecondHeld);
        Assert.Equal(baseline.SecondEscalated, actual.SecondEscalated);
        Assert.Equal(baseline.Status, actual.Status);
        Assert.Equal(baseline.TransientFailureCount, actual.TransientFailureCount);
        Assert.Equal(baseline.GateCalls, actual.GateCalls);
    }

    private static FaultObservation ObserveUnclassifiedPath(string detail)
    {
        var root = CreateTempDirectory("mcg-probe-negative-control");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
            var now = new DateTimeOffset(2026, 10, 25, 9, 20, 44, TimeSpan.Zero);
            var calls = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => { calls++; throw new InvalidOperationException(detail); },
                getLandingFileScopes: _ => ["src/Probe.cs"], utcNow: () => now,
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, TrunkBranchName.Default, runInline: true, utcNow: () => now));
            BatchLoopSummary Tick() => new ConductorBatchLoop().Run(kernel, driver,
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            var first = Tick();
            Assert.Equal(1, calls);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            now = now.AddSeconds(421);
            var second = Tick();
            return new(first.Held, first.Escalated, second.Held, second.Escalated,
                goal.Status, ReadLatestAttempt(root, goal).TransientFailureCount, calls);
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed record FaultObservation(int FirstHeld, int FirstEscalated, int SecondHeld,
        int SecondEscalated, GoalStatus Status, int TransientFailureCount, int GateCalls);

    private static ConductorParallelAcceptanceAttempt ReadLatestAttempt(string root, Goal goal) =>
        Directory.EnumerateFiles(Path.Combine(root, goal.Id.Value), "*.attempt.json")
            .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .OrderByDescending(attempt => attempt.StartedAt).First();
}
