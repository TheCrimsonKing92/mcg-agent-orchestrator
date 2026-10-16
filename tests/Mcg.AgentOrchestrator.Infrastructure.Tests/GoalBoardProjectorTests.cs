using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalBoardProjectorTests
{
    [Xunit.Fact]
    public void MixedStatusesIncludeExactlyOperationalLifecycleSet()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = Enum.GetValues<GoalStatus>()
            .Select((status, index) => Fact(
                id: index.ToString("x32"),
                status: status,
                title: $"{status} title",
                stage: $"stage-{status}"))
            .ToArray();

        var projection = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now);

        foreach (var included in new[]
                 {
                     GoalStatus.Draft, GoalStatus.Active, GoalStatus.WaitingForHuman, GoalStatus.Parked,
                     GoalStatus.Verifying, GoalStatus.Verified, GoalStatus.AcceptanceFailed, GoalStatus.Failed
                 })
        {
            var row = Xunit.Assert.Single(projection.Rows.Where(candidate => candidate.StartsWith($"{included} title [", StringComparison.Ordinal)));
            Xunit.Assert.Contains($"status={included}", row, StringComparison.Ordinal);
            Xunit.Assert.Contains($"stage=stage-{included}", row, StringComparison.Ordinal);
        }

        Xunit.Assert.DoesNotContain(projection.Rows, row => row.StartsWith("Completed title", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(projection.Rows, row => row.StartsWith("Cancelled title", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(projection.Rows, row => row.StartsWith("Superseded title", StringComparison.Ordinal));
        Xunit.Assert.Equal(8, projection.Shown);
    }

    [Xunit.Fact]
    public void CleanedUpLifecycleIsOmittedFromProjection()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = new[]
        {
            Fact("10000000000000000000000000000000", status: GoalStatus.Verified, title: "Still verified"),
            Fact(
                "20000000000000000000000000000000",
                status: GoalStatus.Verified,
                title: "Already cleaned",
                lifecycleState: GoalLifecycleState.CleanedUp)
        };

        var projection = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now);

        Xunit.Assert.Equal(1, projection.Shown);
        Xunit.Assert.Contains("status=Verified", Xunit.Assert.Single(projection.Rows), StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(projection.Rows, row => row.Contains("Already cleaned", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MergedLifecycleRendersLandedStatus()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = new[]
        {
            Fact(
                "10000000000000000000000000000000",
                status: GoalStatus.Verified,
                title: "Landed goal",
                stage: "cleanup",
                lifecycleState: GoalLifecycleState.Recorded)
        };

        var row = Xunit.Assert.Single(GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now).Rows);

        Xunit.Assert.Contains("status=Landed", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("stage=cleanup", row, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DistinctOperationalFactsChooseRequiredControlAndSignal()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = new[]
        {
            Fact("10000000000000000000000000000000", attention: 1, signals: [new("attention", now.AddHours(-5))]),
            Fact("20000000000000000000000000000000", intents: 2, signals: [new("intent", now.AddMinutes(-4))]),
            Fact("30000000000000000000000000000000", dead: true, recovery: "refresh-dispatch 3", signals: [new("dispatch", now.AddMinutes(-3))]),
            Fact("40000000000000000000000000000000", liveAcceptance: true, work: "gate:live", signals: [new("acceptance", now.AddMinutes(-2))]),
            Fact("50000000000000000000000000000000", liveWorker: true, work: "developer:running", signals: [new("dispatch", now.AddMinutes(-1))]),
            Fact("60000000000000000000000000000000", status: GoalStatus.Parked),
            Fact("70000000000000000000000000000000", status: GoalStatus.Failed)
        };

        var rows = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now).Rows;

        AssertRow(rows, "10000000", "signal=attention:5h", "next=attention show 10000000");
        AssertRow(rows, "20000000", "signal=intent:4m", "held=operator intent pending");
        AssertRow(rows, "30000000", "signal=dispatch:3m", "next=refresh-dispatch 3");
        AssertRow(rows, "40000000", "work=gate:live", "held=acceptance live");
        AssertRow(rows, "50000000", "work=developer:running", "held=worker live");
        AssertRow(rows, "60000000", "held=parked");
        AssertRow(rows, "70000000", "next=next 70000000 --full");
        Xunit.Assert.All(rows, row =>
            Xunit.Assert.True(row.Contains("next=", StringComparison.Ordinal) ^ row.Contains("held=", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void LimitsSummaryOneLineBoundsAndUnknownFactsAreExplicit()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var longTitle = new string('x', 200) + "\r\nfull objective must not render";
        var facts = Enumerable.Range(0, 55)
            .Select(index => Fact(index.ToString("x32"), title: longTitle))
            .ToArray();

        var projection = GoalBoardProjector.Project(facts, new GoalBoardOptions(50), now);

        Xunit.Assert.Equal(50, projection.Shown);
        Xunit.Assert.Equal(5, projection.Omitted);
        Xunit.Assert.Equal("rerun goals --board --all or --limit 55", projection.RerunInstruction);
        Xunit.Assert.All(projection.Rows, row =>
        {
            Xunit.Assert.DoesNotContain('\r', row);
            Xunit.Assert.DoesNotContain('\n', row);
            Xunit.Assert.True(row.Length <= 360, $"row length was {row.Length}");
            Xunit.Assert.DoesNotContain("full objective must not render", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("attention=unknown intents=unknown", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("worktree=unknown ahead=? behind=?", row, StringComparison.Ordinal);
        });
        Xunit.Assert.All(projection.Rows, row => Xunit.Assert.Equal(80, row.IndexOf(" [", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void BacklogAndWorktreeFactsRenderWithoutTreatingAbsenceAsClean()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = new[]
        {
            Fact("10000000000000000000000000000000", backlog: "abcdef12/full", worktree: new("clean", 2, 1)),
            Fact("20000000000000000000000000000000", worktree: GoalBoardWorktreeFact.Absent),
            Fact("30000000000000000000000000000000", worktree: GoalBoardWorktreeFact.Unknown),
            Fact("40000000000000000000000000000000", worktree: new("dirty", 0, 0))
        };

        var rows = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now).Rows;

        AssertRow(rows, "10000000", "backlog=abcdef12/full", "worktree=clean ahead=2 behind=1");
        AssertRow(rows, "20000000", "worktree=- ahead=? behind=?");
        AssertRow(rows, "30000000", "worktree=unknown ahead=? behind=?");
        AssertRow(rows, "40000000", "worktree=dirty ahead=0 behind=0");
    }

    [Xunit.Fact]
    public void GoalEvidenceLeaseRendersRecoveryWithinRowContract()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var lease = new GoalEvidenceLeaseFact(
            "10000000000000000000000000000000",
            "goal-evidence:v1:conductor:developer-branch-integration:42:instance-1",
            "conductor:developer-branch-integration",
            "instance-1",
            now.AddMinutes(-12),
            GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending,
            "reclaimed:prior-operation-instance-with-production-shaped-detail");

        string? row = null;
        var error = Xunit.Record.Exception(() =>
            row = Xunit.Assert.Single(GoalBoardProjector.Project(
                [Fact(
                    "10000000000000000000000000000000",
                    status: GoalStatus.AcceptanceFailed,
                    title: new string('t', 160),
                    stage: "acceptance-failure-requiring-operator-triage",
                    work: "reviewer:failed after production-shaped evidence reconciliation",
                    signals: [new("acceptance-evidence", now.AddMinutes(-12))],
                    backlog: "12345678/slice-with-retained-follow-up",
                    worktree: new("conflicting", 123, 456),
                    recovery: "next 10000000 --full with-production-shaped-detail",
                    evidenceLease: lease)],
                new GoalBoardOptions(null),
                now).Rows));

        Xunit.Assert.Null(error);
        Xunit.Assert.NotNull(row);
        Xunit.Assert.Contains("lease-goal=10000000", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("owner-operation=conductor:developer-branch-integrati", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("lease-age=12m", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("lease-status=terminal-reclaim-pending", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("held=goal-evidence lease terminal-reclaim-pending", row, StringComparison.Ordinal);
        Xunit.Assert.True(row.Length <= 360, $"row length was {row.Length}");
    }

    [Xunit.Fact]
    public void GoalEvidenceLeaseWithInterruptedWorkRecoveryStaysWithinRowContract()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var lease = new GoalEvidenceLeaseFact(
            "10000000000000000000000000000000",
            "goal-evidence:v1:conductor:developer-branch-integration:42:instance-1",
            "conductor:developer-branch-integration",
            "instance-1",
            now.AddMinutes(-12),
            GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending,
            "reclaimed:prior-operation-instance-with-production-shaped-detail");
        const string recovery = "goal-recovery apply 12345678 --action preserve-interrupted-work";

        string? row = null;
        var error = Xunit.Record.Exception(() =>
            row = Xunit.Assert.Single(GoalBoardProjector.Project(
                [Fact(
                    "10000000000000000000000000000000",
                    status: GoalStatus.AcceptanceFailed,
                    title: new string('t', 160),
                    stage: "acceptance-failure-requiring-operator-triage",
                    work: "developer:interrupted after production-shaped evidence reconciliation",
                    signals: [new("acceptance-evidence", now.AddMinutes(-12))],
                    backlog: "12345678/slice-with-retained-follow-up",
                    worktree: new("conflicting", 123, 456),
                    dead: true,
                    recovery: recovery,
                    evidenceLease: lease)],
                new GoalBoardOptions(null),
                now).Rows));

        Xunit.Assert.Null(error);
        Xunit.Assert.NotNull(row);
        Xunit.Assert.Contains($"next={recovery}", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("lease-goal=10000000", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("owner-operation=conductor:developer-branch-integrati", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("lease-age=12m", row, StringComparison.Ordinal);
        Xunit.Assert.Contains("lease-status=terminal-reclaim-pending", row, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("lease-recovery=", row, StringComparison.Ordinal);
        Xunit.Assert.True(row.Length <= 360, $"row length was {row.Length}");
    }

    private static GoalBoardGoalFact Fact(
        string id,
        GoalStatus status = GoalStatus.Active,
        string title = "Board goal",
        string stage = "developing",
        string work = "none",
        IReadOnlyList<GoalBoardSignalFact>? signals = null,
        int? attention = null,
        int? intents = null,
        string backlog = "-",
        GoalBoardWorktreeFact? worktree = null,
        bool dead = false,
        string? recovery = null,
        bool liveAcceptance = false,
        bool liveWorker = false,
        GoalLifecycleState? lifecycleState = null,
        GoalEvidenceLeaseFact? evidenceLease = null) =>
        new(
            id,
            title,
            status,
            stage,
            work,
            signals ?? [],
            attention,
            intents,
            backlog,
            worktree ?? GoalBoardWorktreeFact.Unknown,
            dead,
            recovery,
            liveAcceptance,
            liveWorker,
            $"next {id[..8]} --full",
            lifecycleState,
            evidenceLease);

    private static void AssertRow(IReadOnlyList<string> rows, string id, params string[] expected)
    {
        var row = Xunit.Assert.Single(rows.Where(candidate => candidate.Contains($"[{id}]", StringComparison.Ordinal)));
        foreach (var value in expected)
            Xunit.Assert.Contains(value, row, StringComparison.Ordinal);
    }
}
