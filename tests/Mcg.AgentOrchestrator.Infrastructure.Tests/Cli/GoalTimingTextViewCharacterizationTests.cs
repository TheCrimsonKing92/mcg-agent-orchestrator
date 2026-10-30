using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture, async-context culture, and local fixtures.
// Expected strings derive from ConsoleViews.GoalTiming.cs at base
// 3c9cba3f484b78afc3ccb5a7e7a04d5c33dc0d4b.
public sealed class GoalTimingTextViewCharacterizationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PrintReport_EmptyTasksAndAbsentTimes_PreservesExactText()
    {
        var actual = Capture(() => GoalTimingTextView.PrintReport(EmptyReport()));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal timing abcdef01: Timing sample",
            "Current status: progressing (no active hold)",
            "Task  Role       Rounds  IntakeWait  Prep  Worker  Handoff",
            "Summary: total=0s work=0s (0%) wait=0s (0%) e2eStartedAt=absent e2eStartSource=unavailable e2eEndedAt=absent e2eEndSource=unavailable backlogIntentAt=absent backlogIntentSource=none upstreamBacklogIntentWait=0s intakeWait=0s gate=0s landingWait=0s landedAt=not-landed landingSource=none",
            "", ""), actual);
    }

    [Fact]
    public void PrintReport_HeldGoalWithRounds_PreservesExactText()
    {
        var secondRound = FirstRound() with
        {
            RoundNumber = 2,
            SandboxPrepDuration = TimeSpan.FromMinutes(1),
            WorkerRunDuration = TimeSpan.FromSeconds(90),
            OutcomeVerdict = null,
            ValueClass = DispatchRoundValueClass.Corrective,
            ValueEvidence = "pending"
        };
        var report = EmptyReport() with
        {
            GoalE2EStartedAt = Timestamp,
            GoalE2EStartSource = "created",
            GoalE2EEndedAt = Timestamp.AddMinutes(5),
            GoalE2EEndSource = "landed",
            BacklogIntentAt = Timestamp,
            BacklogIntentSource = "backlog",
            LandedAt = Timestamp.AddMinutes(5),
            LandingSource = "receipt",
            BacklogIntentWait = TimeSpan.FromSeconds(42.5),
            IntakeToFirstDispatchWait = TimeSpan.FromMinutes(1),
            GateDuration = TimeSpan.FromSeconds(30),
            LandingWait = TimeSpan.FromSeconds(30),
            WorkDuration = TimeSpan.FromMinutes(3),
            WaitDuration = TimeSpan.FromMinutes(2),
            TotalDuration = TimeSpan.FromMinutes(5),
            WorkPercent = 0.6,
            WaitPercent = 0.4,
            Tasks =
            [
                new(new TaskId("12345678-rest"), AgentRole.Developer, "Implement",
                    TimeSpan.Zero, [FirstRound(), secondRound]),
                new(new TaskId("87654321-rest"), AgentRole.Tester, "Not dispatched",
                    TimeSpan.Zero, [])
            ],
            CurrentHold = new("Waiting", "Need evidence", Timestamp, TimeSpan.Zero, Timestamp)
        };

        var actual = Capture(() => GoalTimingTextView.PrintReport(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal timing abcdef01: Timing sample",
            "Current status: held state=Waiting since=2026-10-30 08:00:00Z duration=0s stalled=yes blocker=Need evidence",
            "Task  Role       Rounds  IntakeWait  Prep  Worker  Handoff",
            "12345678 Developer       2         0s    1m      2m       0s",
            "  round 1: dispatch=2026-10-30 08:00:00Z prep=0s worker=30s handoff=0s value=Productive verdict=VerifiedSuccess evidence=verified",
            "  round 2: dispatch=2026-10-30 08:00:00Z prep=1m worker=1.5m handoff=0s value=Corrective verdict=Unpaired evidence=pending",
            "Summary: total=5m work=3m (60%) wait=2m (40%) e2eStartedAt=2026-10-30 08:00:00Z e2eStartSource=created e2eEndedAt=2026-10-30 08:05:00Z e2eEndSource=landed backlogIntentAt=2026-10-30 08:00:00Z backlogIntentSource=backlog upstreamBacklogIntentWait=42.5s intakeWait=1m gate=30s landingWait=30s landedAt=2026-10-30 08:05:00Z landingSource=receipt",
            "", ""), actual);
    }

    [Fact]
    public void PrintRollup_EmptyGoals_PreservesExactText()
    {
        var report = new GoalTimingRollupSnapshot(0, TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, [], [], []);

        var actual = Capture(() => GoalTimingTextView.PrintRollup(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal timing rollup: goals=0 total=0s work=0s (0%) wait=0s (0%) productive=0 corrective=0 envWaste=0 falseFail=0 superseded=0",
            "Phase               Total   Share  Median     P90",
            "", ""), actual);
    }

    [Fact]
    public void PrintRollup_PhasesDailyTrendAndWaste_PreservesExactText()
    {
        var report = new GoalTimingRollupSnapshot(1, TimeSpan.FromMinutes(200),
            TimeSpan.FromMinutes(150), TimeSpan.FromMinutes(50), 0.75, 0.25, 3, 4, 1, 2, 5,
            [new("Gate", TimeSpan.Zero, 0, TimeSpan.FromSeconds(42.5), TimeSpan.FromHours(2.5))],
            [new(new DateOnly(2026, 10, 30), 1, TimeSpan.FromMinutes(200),
                TimeSpan.FromMinutes(150), 0.75, 1, 2)],
            [new("provider", 1)]);

        var actual = Capture(() => GoalTimingTextView.PrintRollup(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Goal timing rollup: goals=1 total=200m work=150m (75%) wait=50m (25%) productive=3 corrective=4 envWaste=1 falseFail=2 superseded=5",
            "Phase               Total   Share  Median     P90",
            "Gate                    0s      0%   42.5s    150m",
            "Daily trend:",
            "Day          Goals    Total  Median  Work%  EnvWaste  FalseFail",
            "2026-10-30     1     200m    150m    75%         1          2",
            "Top waste sources:",
            "  provider: 1",
            "", ""), actual);
    }

    [Fact]
    public void PrintDispatchValueReport_EmptyGoals_PreservesExactText()
    {
        var report = new DispatchValueReportSnapshot(null, 0, 0, [], []);

        var actual = Capture(() => GoalTimingTextView.PrintDispatchValueReport(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Dispatch value report (all time): goals=0 rounds=0",
            "Goal      Outcome    Rounds  Productive  Corrective  EnvWaste  FalseFail  Superseded",
            "", ""), actual);
    }

    [Fact]
    public void PrintDispatchValueReport_RoundsAndWaste_PreservesExactText()
    {
        var wasted = FirstRound() with
        {
            RoundNumber = 2,
            OutcomeVerdict = null,
            ValueClass = DispatchRoundValueClass.WastedEnvironmental,
            ValueEvidence = "provider unavailable",
            WasteSource = "provider"
        };
        var report = new DispatchValueReportSnapshot(Timestamp, 1, 2,
            [new(new GoalId("abcdef01-rest"), "Timing sample", GoalTerminalOutcome.Landed,
                2, 1, 0, 1, 0, 0, [FirstRound(), wasted])],
            [new("provider", 1)]);

        var actual = Capture(() => GoalTimingTextView.PrintDispatchValueReport(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "Dispatch value report (since 2026-10-30 08:00:00Z): goals=1 rounds=2",
            "Goal      Outcome    Rounds  Productive  Corrective  EnvWaste  FalseFail  Superseded",
            "abcdef01 Landed          2          1          0         1         0          0",
            "  task=12345678 round=1 value=Productive verdict=VerifiedSuccess source=none evidence=verified",
            "  task=12345678 round=2 value=WastedEnvironmental verdict=Unpaired source=provider evidence=provider unavailable",
            "Top waste sources:",
            "  provider: 1",
            "", ""), actual);
    }

    private static GoalTimingReportSnapshot EmptyReport() => new(
        GoalId: new("abcdef01-rest"),
        Objective: "Timing sample",
        GoalE2EStartedAt: null,
        GoalE2EStartSource: "unavailable",
        GoalE2EEndedAt: null,
        GoalE2EEndSource: "unavailable",
        BacklogIntentAt: null,
        BacklogIntentSource: null,
        IntakeAt: null,
        FirstDispatchAt: null,
        LandedAt: null,
        LandingSource: null,
        BacklogIntentWait: TimeSpan.Zero,
        IntakeToFirstDispatchWait: TimeSpan.Zero,
        GateDuration: TimeSpan.Zero,
        LandingWait: TimeSpan.Zero,
        WorkDuration: TimeSpan.Zero,
        WaitDuration: TimeSpan.Zero,
        TotalDuration: TimeSpan.Zero,
        WorkPercent: 0,
        WaitPercent: 0,
        Tasks: [],
        CurrentHold: null);

    private static GoalTimingRoundReport FirstRound() => new(
        new TaskId("12345678-rest"), 1, Timestamp, Timestamp, Timestamp.AddSeconds(30),
        TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.Zero,
        DispatchOutcomeKind.VerifiedSuccess, DispatchRoundValueClass.Productive,
        "verified", " ");

    private static string Capture(Action print)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return AsyncLocalConsoleRouter.Capture(print);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
