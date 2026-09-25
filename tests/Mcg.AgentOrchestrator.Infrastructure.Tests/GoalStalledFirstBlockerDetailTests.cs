using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalStalledFirstBlockerDetailTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void StalledEventKeepsFirstPreflightReasonWholeAndCountsOmittedBlockers()
    {
        var root = CreateTempDirectory("mcg-stalled-first-blocker");
        try
        {
            var (kernel, goal) = SimpleGoal("show the first actionable blocker");
            var developer = goal.Tasks.Single();
            var preflightReason = $"blocked: Task '{developer.Id.Value}' already has passing verification; "
                + new string('x', 520) + " retry the task before dispatching it again";
            var diagnostics = new[]
            {
                new ReadyBlockedDiagnostic(goal.Id.Value[..8], 1, developer.Id.Value, "codex-cli", "preflight-blocked", [preflightReason]),
                new ReadyBlockedDiagnostic(goal.Id.Value[..8], 2, TaskId.New().Value, "codex-cli", "parallel-serialized", [new string('y', 350)]),
                new ReadyBlockedDiagnostic(goal.Id.Value[..8], 3, TaskId.New().Value, "claude-cli", "parallel-serialized", [new string('z', 350)])
            };
            var emptyReason = ConductorDriver.DescribeEmptyBatch(new ParallelExecutionPlan([], []), diagnostics);
            var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
            var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
            new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(logPath, utcNow: () => now),
                utcNow: () => now).Run(
                    kernel,
                    MakeDriver(
                        getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                        dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch(emptyReason)),
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2,
                    watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ =>
                    {
                        now = now.AddMinutes(11);
                        return false;
                    },
                    goalStallThreshold: TimeSpan.FromMinutes(10));

            var hold = Assert.IsType<GoalHoldState>(kernel.GetGoal(goal.Id).CurrentHold).Blocker;
            Assert.True(hold.Length > 512);
            Assert.True(ConductorDriver.FindFirstBlockerReasonEnd(hold) > 512);
            var stalled = Assert.Single(File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "goal-stalled"));
            Assert.Contains(preflightReason.Replace(' ', '_'), stalled.Detail, StringComparison.Ordinal);
            Assert.Contains("..._(+1_more_blocker)", stalled.Detail, StringComparison.Ordinal);
            Assert.True(stalled.Detail.Length > 512);
            var eventBlocker = stalled.Detail[(stalled.Detail.IndexOf("blocker=", StringComparison.Ordinal) + "blocker=".Length)..];
            Assert.True(eventBlocker.Length - ConductorDriver.FindFirstBlockerReasonEnd(hold) <= 512);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void OtherStalledReasonsKeepTheExistingSanitizer()
    {
        var reason = "other reason " + new string('x', 600);
        Assert.Equal(ConductorBatchLoop.SanitizeReason(reason), ConductorBatchLoop.FormatStalledBlockerDetail(reason));
    }
}
