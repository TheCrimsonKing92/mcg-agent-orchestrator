using Mcg.AgentOrchestrator.Core;

public sealed class GoalTimingReportTests
{
    private const string WorkDir = "C:\\repo";
    private const string Command = "codex exec prompt.md";

    [Xunit.Fact(DisplayName = "GoalTimingReport_partitions_two_round_goal_with_receipt_sourced_prep")]
    public void GoalTimingReportPartitionsTwoRoundGoalWithReceiptSourcedPrep()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement the report", AgentRole.Developer);
        var goal = kernel.CreateGoal("Timing report", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(30));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, Process(clock.UtcNow, completedAt: null, "round1"));
        clock.Advance(TimeSpan.FromMinutes(60));
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            Process(clock.UtcNow.AddMinutes(-60), clock.UtcNow, "round1"),
            Verification(1, clock.UtcNow, SandboxPrep(clock.UtcNow.AddMinutes(-90), TimeSpan.FromMinutes(30)), "round1"));

        clock.Advance(TimeSpan.FromMinutes(20));
        kernel.RetryTask(goal.Id, task.Id, "retry");
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(40));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, Process(clock.UtcNow, completedAt: null, "round2"));
        clock.Advance(TimeSpan.FromMinutes(60));
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            Process(clock.UtcNow.AddMinutes(-60), clock.UtcNow, "round2"),
            Verification(0, clock.UtcNow, SandboxPrep(clock.UtcNow.AddMinutes(-100), TimeSpan.FromMinutes(40)), "round2"));

        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("acceptance", WorkDir, 0, "PHASE_TIMING command=acceptance phase=verify elapsedMs=300000", string.Empty, clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(35));
        kernel.CompleteGoal(goal.Id, "landed");

        var report = GoalTimingReport.Build(goal);
        var rounds = report.Tasks.Single().Rounds;

        Xunit.Assert.Collection(
            rounds,
            round =>
            {
                Xunit.Assert.Equal(TimeSpan.FromMinutes(30), round.SandboxPrepDuration);
                Xunit.Assert.Equal(TimeSpan.FromMinutes(60), round.WorkerRunDuration);
                Xunit.Assert.Equal(TimeSpan.FromMinutes(20), round.HandoffWait);
            },
            round =>
            {
                Xunit.Assert.Equal(TimeSpan.FromMinutes(40), round.SandboxPrepDuration);
                Xunit.Assert.Equal(TimeSpan.FromMinutes(60), round.WorkerRunDuration);
                Xunit.Assert.Equal(TimeSpan.Zero, round.HandoffWait);
            });
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), report.IntakeToFirstDispatchWait);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(5), report.GateDuration);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(195), report.WorkDuration);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(65), report.WaitDuration);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(260), report.TotalDuration);
        Xunit.Assert.Equal(report.TotalDuration, report.WorkDuration + report.WaitDuration);
    }

    private static TaskDispatchRecord Dispatch(DateTimeOffset at) =>
        new("codex-cli", Command, WorkDir, at, SandboxLowIntegrity: true);

    private static TaskProcessRecord Process(DateTimeOffset startedAt, DateTimeOffset? completedAt, string artifactBase) =>
        new(1234, Command, WorkDir, $"{artifactBase}.out.log", $"{artifactBase}.err.log", $"{artifactBase}.exit.txt", startedAt, completedAt, completedAt is null ? null : 0);

    private static TaskVerificationRecord Verification(int exitCode, DateTimeOffset at, string stderr, string artifactBase) =>
        new(Command, WorkDir, exitCode, exitCode == 0 ? "ok" : "failed", stderr, at, StandardOutputPath: $"{artifactBase}.out.log", StandardErrorPath: $"{artifactBase}.err.log");

    private static string SandboxPrep(DateTimeOffset startedAt, TimeSpan elapsed) =>
        $$"""{"event":"sandbox-prep","phase":"complete","timestamp":"{{startedAt.Add(elapsed):O}}","startedAt":"{{startedAt:O}}","workingDirectory":"C:\\repo","elapsedMs":{{(long)elapsed.TotalMilliseconds}}}""";
}
