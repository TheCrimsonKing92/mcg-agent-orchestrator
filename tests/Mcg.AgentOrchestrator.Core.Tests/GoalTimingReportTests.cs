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
            null);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            Verification(
                0,
                clock.UtcNow,
                SandboxPrep(clock.UtcNow.AddMinutes(-100), TimeSpan.FromMinutes(40)) +
                    Environment.NewLine +
                    "PHASE_TIMING command=acceptance phase=verify elapsedMs=300000",
                "round2"));

        clock.Advance(TimeSpan.FromMinutes(40));
        kernel.ParkGoal(goal.Id, "landed timing marker");

        var report = GoalTimingReport.Build(goal);
        var rounds = report.Tasks.Single().Rounds;

        Xunit.Assert.Collection(
            rounds,
            round =>
            {
                Xunit.Assert.Equal(TimeSpan.FromMinutes(30), round.SandboxPrepDuration);
                Xunit.Assert.Equal(TimeSpan.FromMinutes(60), round.WorkerRunDuration);
                Xunit.Assert.Equal(TimeSpan.FromMinutes(20), round.HandoffWait);
                Xunit.Assert.Equal(DispatchRoundValueClass.Corrective, round.ValueClass);
                Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, round.OutcomeVerdict);
            },
            round =>
            {
                Xunit.Assert.Equal(TimeSpan.FromMinutes(40), round.SandboxPrepDuration);
                Xunit.Assert.Equal(TimeSpan.FromMinutes(60), round.WorkerRunDuration);
                Xunit.Assert.Equal(TimeSpan.Zero, round.HandoffWait);
                Xunit.Assert.Equal(DispatchRoundValueClass.Productive, round.ValueClass);
                Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, round.OutcomeVerdict);
            });
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), report.IntakeToFirstDispatchWait);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(5), report.GateDuration);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(195), report.WorkDuration);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(65), report.WaitDuration);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(260), report.TotalDuration);
        Xunit.Assert.Equal(report.TotalDuration, report.WorkDuration + report.WaitDuration);
    }

    [Xunit.Fact(DisplayName = "GoalTimingReport_hand_landed_goal_reports_verified_to_integration_commit_landing_wait")]
    public void GoalTimingReportHandLandedGoalReportsVerifiedToIntegrationCommitLandingWait()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement hand landed report", AgentRole.Developer);
        var goal = kernel.CreateGoal("Hand landed timing", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromMinutes(5));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, Process(clock.UtcNow, completedAt: null, "hand-landed"));
        clock.Advance(TimeSpan.FromMinutes(10));
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            Process(clock.UtcNow.AddMinutes(-10), clock.UtcNow, "hand-landed"),
            Success(clock.UtcNow) with
            {
                Command = Command,
                WorkingDirectory = WorkDir,
                StandardError = SandboxPrep(clock.UtcNow.AddMinutes(-15), TimeSpan.FromMinutes(5)),
                StandardOutputPath = "hand-landed.out.log",
                StandardErrorPath = "hand-landed.err.log"
            });

        var verifiedAt = clock.UtcNow;
        var landedAt = verifiedAt.AddMinutes(30);
        var report = GoalTimingReport.Build(
            goal,
            new GoalTimingReportContext(LandedAt: landedAt, LandingSource: "git-integration-commit"));

        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
        Xunit.Assert.Equal(landedAt, report.LandedAt);
        Xunit.Assert.Equal("git-integration-commit", report.LandingSource);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(30), report.LandingWait);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(50), report.TotalDuration);
    }

    [Xunit.Fact(DisplayName = "GoalTimingReport_gate_duration_uses_acceptance_journal_span")]
    public void GoalTimingReportGateDurationUsesAcceptanceJournalSpan()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement gated report", AgentRole.Developer);
        var goal = kernel.CreateGoal("Gated timing", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        RecordRound(kernel, clock, goal.Id, task.Id, Success(clock.UtcNow));

        var gateStart = clock.UtcNow.AddMinutes(3);
        var gateEnd = gateStart.AddMinutes(7);
        var report = GoalTimingReport.Build(
            goal,
            new GoalTimingReportContext(
                LandedAt: gateEnd,
                LandingSource: "acceptance-journal",
                GateSpans: [new GoalTimingGateSpan(gateStart, gateEnd, "passed")]));

        Xunit.Assert.Equal(TimeSpan.FromMinutes(7), report.GateDuration);
    }

    [Xunit.Fact(DisplayName = "GoalTimingReport_rollup_reconciles_per_goal_totals_and_dispatch_value_waste")]
    public void GoalTimingReportRollupReconcilesPerGoalTotalsAndDispatchValueWaste()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var firstTask = new TaskSpec(TaskId.New(), "Implement first report", AgentRole.Developer);
        var first = kernel.CreateGoal("First landed timing", [firstTask]);
        kernel.ActivateGoal(first.Id, DefaultAgents());
        RecordRound(kernel, clock, first.Id, firstTask.Id, Success(clock.UtcNow));

        var secondTask = new TaskSpec(TaskId.New(), "Implement second report", AgentRole.Developer);
        var second = kernel.CreateGoal("Second landed timing", [secondTask]);
        kernel.ActivateGoal(second.Id, DefaultAgents());
        RecordRound(kernel, clock, second.Id, secondTask.Id, Verification(1, clock.UtcNow, string.Empty, "ERROR: Unable to connect to API", "env-waste"));
        kernel.RetryTask(second.Id, secondTask.Id, "retry environmental failure");
        RecordRound(kernel, clock, second.Id, secondTask.Id, Success(clock.UtcNow));

        var contexts = new Dictionary<GoalId, GoalTimingReportContext>
        {
            [first.Id] = new(
                BacklogIntentAt: first.Timeline.Min(evt => evt.OccurredAt).AddMinutes(-4),
                LandedAt: first.Timeline.Max(evt => evt.OccurredAt).AddMinutes(6),
                LandingSource: "fixture"),
            [second.Id] = new(
                LandedAt: second.Timeline.Max(evt => evt.OccurredAt).AddMinutes(9),
                LandingSource: "fixture",
                GateSpans: [new GoalTimingGateSpan(second.Timeline.Max(evt => evt.OccurredAt), second.Timeline.Max(evt => evt.OccurredAt).AddMinutes(2), "passed")])
        };

        var firstReport = GoalTimingReport.Build(first, contexts[first.Id]);
        var secondReport = GoalTimingReport.Build(second, contexts[second.Id]);
        var rollup = GoalTimingReport.BuildRollup([first, second], contexts);

        Xunit.Assert.Equal(2, rollup.GoalCount);
        Xunit.Assert.Equal(firstReport.TotalDuration + secondReport.TotalDuration, rollup.TotalDuration);
        Xunit.Assert.Equal(firstReport.WorkDuration + secondReport.WorkDuration, rollup.WorkDuration);
        Xunit.Assert.Equal(1, rollup.WastedEnvironmentalCount);
        Xunit.Assert.Contains(rollup.Phases, phase => phase.Phase == "BacklogIntentWait" && phase.Total == TimeSpan.FromMinutes(4));
        Xunit.Assert.Single(rollup.TopWasteSources, source => source.Source == DispatchOutcomeKind.ProviderConnectivity.ToString() && source.Count == 1);
    }

    [Xunit.Fact(DisplayName = "DispatchValueReport_classifies_known_round_types_and_rolls_up_per_goal")]
    public void DispatchValueReportClassifiesKnownRoundTypesAndRollsUpPerGoal()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var landedTask = new TaskSpec(TaskId.New(), "Implement value ledger", AgentRole.Developer);
        var landed = kernel.CreateGoal("Landed value report", [landedTask]);
        kernel.ActivateGoal(landed.Id, DefaultAgents());

        RecordRound(kernel, clock, landed.Id, landedTask.Id, Success(clock.UtcNow));
        kernel.RetryTask(landed.Id, landedTask.Id, "review requested rework");
        RecordRound(kernel, clock, landed.Id, landedTask.Id, Verification(1, clock.UtcNow, "tests failed", string.Empty, "corrective"));
        kernel.RetryTask(landed.Id, landedTask.Id, "fix review finding");
        RecordRound(kernel, clock, landed.Id, landedTask.Id, Verification(1, clock.UtcNow, string.Empty, "ERROR: Unable to connect to API", "connectivity"));
        kernel.RetryTask(landed.Id, landedTask.Id, "retry after connectivity failure");
        RecordRound(kernel, clock, landed.Id, landedTask.Id, Verification(1, clock.UtcNow, string.Empty, "Developer/Tester dispatch exited 0 but did not produce required relevant file-change evidence", "false-fail"));
        kernel.RetryTask(landed.Id, landedTask.Id, "bridge false fail");
        RecordRound(kernel, clock, landed.Id, landedTask.Id, Success(clock.UtcNow));
        kernel.RecordTaskVerification(
            landed.Id,
            landedTask.Id,
            new TaskVerificationRecord("manual", WorkDir, 0, "passed", string.Empty, clock.UtcNow));

        var abandonedTask = new TaskSpec(TaskId.New(), "Discarded pivot", AgentRole.Developer);
        var abandoned = kernel.CreateGoal("Abandoned value report", [abandonedTask]);
        kernel.ActivateGoal(abandoned.Id, DefaultAgents());
        RecordRound(kernel, clock, abandoned.Id, abandonedTask.Id, Verification(1, clock.UtcNow, "obsolete", string.Empty, "abandoned"));
        kernel.SupersedeGoal(abandoned.Id, "pivoted elsewhere");

        var report = kernel.BuildDispatchValueReport();
        var landedReport = report.Goals.Single(goal => goal.GoalId == landed.Id);
        var abandonedReport = report.Goals.Single(goal => goal.GoalId == abandoned.Id);

        Xunit.Assert.Equal(GoalTerminalOutcome.Landed, landedReport.TerminalOutcome);
        Xunit.Assert.Equal(5, landedReport.DispatchRoundCount);
        Xunit.Assert.Equal(2, landedReport.ProductiveCount);
        var landedClasses = string.Join(", ", landedReport.Rounds.Select(round => $"{round.RoundNumber}:{round.ValueClass}:{round.OutcomeVerdict?.ToString() ?? "none"}:{round.WasteSource}"));
        Xunit.Assert.True(landedReport.CorrectiveCount == 1, landedClasses);
        Xunit.Assert.Equal(1, landedReport.WastedEnvironmentalCount);
        Xunit.Assert.Equal(1, landedReport.WastedFalseFailCount);
        Xunit.Assert.Contains(landedReport.Rounds, round =>
            round.ValueClass == DispatchRoundValueClass.WastedEnvironmental &&
            round.OutcomeVerdict == DispatchOutcomeKind.ProviderConnectivity &&
            round.ValueEvidence.Contains("verdict=ProviderConnectivity", StringComparison.Ordinal));
        Xunit.Assert.Contains(landedReport.Rounds, round =>
            round.ValueClass == DispatchRoundValueClass.WastedFalseFail &&
            round.WasteSource == "false-file-change-guard" &&
            round.ValueEvidence.Contains("verification.ExitCode=1", StringComparison.Ordinal));

        Xunit.Assert.Equal(GoalTerminalOutcome.Abandoned, abandonedReport.TerminalOutcome);
        Xunit.Assert.Equal(1, abandonedReport.SupersededCount);
        Xunit.Assert.Contains(report.TopWasteSources, source =>
            source.Source == DispatchOutcomeKind.ProviderConnectivity.ToString() &&
            source.Count == 1);
        Xunit.Assert.Contains(report.TopWasteSources, source =>
            source.Source == "false-file-change-guard" &&
            source.Count == 1);
    }

    private static TaskDispatchRecord Dispatch(DateTimeOffset at) =>
        new("codex-cli", Command, WorkDir, at, SandboxLowIntegrity: true);

    private static TaskProcessRecord Process(DateTimeOffset startedAt, DateTimeOffset? completedAt, string artifactBase) =>
        new(1234, Command, WorkDir, $"{artifactBase}.out.log", $"{artifactBase}.err.log", $"{artifactBase}.exit.txt", startedAt, completedAt, completedAt is null ? null : 0);

    private static TaskVerificationRecord Verification(int exitCode, DateTimeOffset at, string stderr, string artifactBase) =>
        Verification(exitCode, at, exitCode == 0 ? "ok" : "failed", stderr, artifactBase);

    private static TaskVerificationRecord Verification(int exitCode, DateTimeOffset at, string stdout, string stderr, string artifactBase) =>
        new(
            Command,
            WorkDir,
            exitCode,
            stdout,
            stderr,
            at,
            StandardOutputPath: $"{artifactBase}.out.log",
            StandardErrorPath: $"{artifactBase}.err.log",
            WorkerResultPresent: exitCode == 0,
            HasCommittedChanges: exitCode == 0,
            HeartbeatStandardOutputBytes: exitCode == 0 ? 2 : null);

    private static void RecordRound(
        AgentOrchestratorKernel kernel,
        FakeClock clock,
        GoalId goalId,
        TaskId taskId,
        TaskVerificationRecord verification)
    {
        clock.Advance(TimeSpan.FromSeconds(1));
        kernel.RecordTaskDispatch(goalId, taskId, Dispatch(clock.UtcNow));
        clock.Advance(TimeSpan.FromSeconds(1));
        kernel.RecordDispatchExecutionResult(goalId, taskId, verification with { CompletedAt = clock.UtcNow });
        clock.Advance(TimeSpan.FromSeconds(1));
    }

    private static TaskVerificationRecord Success(DateTimeOffset at) =>
        new(
            Command,
            WorkDir,
            0,
            "WORKER_RESULT:\nfiles: src/file.cs\ncommands: build\n tests: pass - focused\nblockers: none\nEND_WORKER_RESULT",
            string.Empty,
            at,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            HeartbeatStandardOutputBytes: 100);

    private static string SandboxPrep(DateTimeOffset startedAt, TimeSpan elapsed) =>
        $$"""{"event":"sandbox-prep","phase":"complete","timestamp":"{{startedAt.Add(elapsed):O}}","startedAt":"{{startedAt:O}}","workingDirectory":"C:\\repo","elapsedMs":{{(long)elapsed.TotalMilliseconds}}}""";
}
