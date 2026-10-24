using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ConductorDiagnosticDumpRetentionTests
{
    private static readonly string[] IgnoredNames =
    [
        "other.dmp", "conductor-tick-stall-1-2026.dmp",
        "conductor-tick-stall-1-20261001000000000.dmp.tmp", "operator-loop.out.log"
    ];
    private static readonly int[] UnreadableProcessIds = [3, 1];

    [Xunit.Fact]
    public void RankKeepsNewestAndRecentDumpsAndReclaimsOlderDumps()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = WriteRankDumps(fixture);

        var decisions = Sweep(fixture);

        AssertRankDecisions(paths, decisions);
    }

    [Xunit.Fact]
    public void AgeBoundaryIsInclusiveAndFutureStampIsWithinAge()
    {
        using var fixture = new RetentionReclaimFixture();
        WriteDump(fixture, TimeSpan.FromHours(3), 1);
        WriteDump(fixture, TimeSpan.FromHours(2), 2);
        var future = WriteDump(fixture, TimeSpan.FromHours(1), 3);
        var boundary = WriteDump(fixture, TimeSpan.FromDays(-3), 4);
        var pastBoundary = WriteDump(fixture, TimeSpan.FromDays(-3) - TimeSpan.FromMilliseconds(1), 5);

        var decisions = Sweep(fixture);

        Assert.Equal(5, decisions.Count);
        AssertDecision(decisions.Single(d => d.Path == future), future,
            EvidenceRetentionAction.Preserved, "diagnostic-dump-within-age");
        AssertDecision(decisions.Single(d => d.Path == boundary), boundary,
            EvidenceRetentionAction.Preserved, "diagnostic-dump-within-age");
        AssertDecision(decisions.Single(d => d.Path == pastBoundary), pastBoundary,
            EvidenceRetentionAction.Deleted, "diagnostic-dump-past-age", 5);
    }

    [Xunit.Fact]
    public void OnlyTwoDumpsAreKeptEvenWhenThirtyDaysOld()
    {
        using var fixture = new RetentionReclaimFixture();
        var first = WriteDump(fixture, TimeSpan.FromDays(-30), 1, processId: 1);
        var second = WriteDump(fixture, TimeSpan.FromDays(-30), 2, processId: 2);

        var decisions = Sweep(fixture);

        Assert.Collection(decisions,
            d => AssertDecision(d, second, EvidenceRetentionAction.Preserved, "diagnostic-dump-newest"),
            d => AssertDecision(d, first, EvidenceRetentionAction.Preserved, "diagnostic-dump-newest"));
    }

    [Xunit.Fact]
    public void NonmatchingAndNestedFilesAreIgnoredAndUnreadableStampIsKept()
    {
        using var fixture = new RetentionReclaimFixture();
        var ignored = IgnoredNames.Select(name => Path.Combine(fixture.LogDirectory, name)).ToList();
        foreach (var path in ignored) File.WriteAllText(path, "ignored");
        var nestedDirectory = Path.Combine(fixture.LogDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        ignored.Add(WriteDump(fixture, TimeSpan.FromDays(-30), 7, directory: nestedDirectory));
        var unreadable = Path.Combine(fixture.LogDirectory, "conductor-tick-stall-7-20261341000000000.dmp");
        File.WriteAllText(unreadable, "unreadable");

        var decisions = Sweep(fixture);

        Assert.All(ignored, path =>
        {
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(decisions, d => d.Path == path);
        });
        AssertDecision(Assert.Single(decisions), unreadable,
            EvidenceRetentionAction.Preserved, "diagnostic-dump-stamp-unreadable");
        var missingDecisions = new List<EvidenceRetentionDecision>();
        ConductorDiagnosticDumpRetention.Sweep(Path.Combine(fixture.LogDirectory, "missing"),
            RetentionReclaimFixture.Now, missingDecisions);
        Assert.Empty(missingDecisions);
    }

    [Xunit.Fact]
    public void EqualStampsUseOrdinalNamesAndUnreadableNamesTakeNoRank()
    {
        using var fixture = new RetentionReclaimFixture();
        var lower = WriteDump(fixture, TimeSpan.FromDays(-30), 3, processId: 2);
        var higher = WriteDump(fixture, TimeSpan.FromDays(-30), 4, processId: 9);
        var upper = WriteDump(fixture, TimeSpan.FromDays(-30), 5, processId: 8, upperCase: true);
        var unreadable = UnreadableProcessIds.Select(id => Path.Combine(fixture.LogDirectory,
            $"conductor-tick-stall-{id}-20261341000000000.dmp")).ToArray();
        foreach (var path in unreadable) File.WriteAllText(path, "unreadable");

        var decisions = Sweep(fixture);

        Assert.Collection(decisions,
            d => AssertDecision(d, higher, EvidenceRetentionAction.Preserved, "diagnostic-dump-newest"),
            d => AssertDecision(d, lower, EvidenceRetentionAction.Preserved, "diagnostic-dump-newest"),
            d => AssertDecision(d, upper, EvidenceRetentionAction.Deleted, "diagnostic-dump-past-age", 5),
            d => AssertDecision(d, unreadable[1], EvidenceRetentionAction.Preserved, "diagnostic-dump-stamp-unreadable"),
            d => AssertDecision(d, unreadable[0], EvidenceRetentionAction.Preserved, "diagnostic-dump-stamp-unreadable"));
    }

    [Xunit.Fact]
    public void LockedReclaimableDumpIsDeferredWithFailureType()
    {
        using var fixture = new RetentionReclaimFixture();
        WriteDump(fixture, TimeSpan.FromHours(-1), 1);
        WriteDump(fixture, TimeSpan.FromHours(-2), 2);
        var locked = WriteDump(fixture, TimeSpan.FromDays(-5), 13);
        List<EvidenceRetentionDecision> decisions;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            decisions = Sweep(fixture);
        }

        var decision = Assert.Single(decisions, d => d.Path == locked);
        AssertDecision(decision, locked, EvidenceRetentionAction.DeferredLocked, "diagnostic-dump-locked");
        Assert.NotNull(decision.FailureExceptionType);
    }

    [Xunit.Fact]
    public void MaintenanceAddsDumpDecisionsWithoutChangingOtherResults()
    {
        using var baseline = new RetentionReclaimFixture();
        using var withDumps = new RetentionReclaimFixture();
        foreach (var fixture in new[] { baseline, withDumps })
        {
            fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, "operator-loop-old.out.log"), 30);
            fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, "operator-loop-young.out.log"), 2);
            fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "older", 1, 30);
            fixture.WriteAttempt(RetentionReclaimFixture.CompletedId, "final", 2, 2);
        }
        var paths = WriteRankDumps(withDumps);

        var baselineResult = baseline.Run(null, baseline.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed));
        var dumpResult = withDumps.Run(null, withDumps.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed));

        Assert.Equal(baselineResult.WorkerArtifactsDeleted, dumpResult.WorkerArtifactsDeleted);
        Assert.Equal(baselineResult.WorkerLogsCompressed, dumpResult.WorkerLogsCompressed);
        Assert.Equal(baselineResult.SuccessfulTrxReceiptsWritten, dumpResult.SuccessfulTrxReceiptsWritten);
        Assert.Equal(baselineResult.AcceptanceArtifactsDeleted, dumpResult.AcceptanceArtifactsDeleted);
        Assert.Equal(baselineResult.PromptArtifactsDeleted, dumpResult.PromptArtifactsDeleted);
        Assert.Equal(baselineResult.GoalJournalsArchived, dumpResult.GoalJournalsArchived);
        Assert.Equal(OtherDecisions(baseline, baselineResult), OtherDecisions(withDumps, dumpResult));
        Assert.DoesNotContain(baselineResult.Decisions, d => d.Family == EvidenceArtifactFamily.DiagnosticDumps);
        AssertRankDecisions(paths, dumpResult.Decisions.Where(d => d.Family == EvidenceArtifactFamily.DiagnosticDumps).ToArray());
    }

    [Xunit.Fact]
    public void RegistryDescribesTheFixedDumpPolicy()
    {
        Assert.Equal("operator-*.log files are swept at WorkerDeletionAge; conductor-tick-stall *.dmp dumps keep the newest 2 and older dumps past 3 days are reclaimed by ConductorDiagnosticDumpRetention; spec-refinement, post-landing-canary, conduct-supervisor, supervisor-handoff and supervisor-ready runs past 14 days or past the newest 500 per family are reclaimed by LogDirectoryFamilyRetention, keeping the newest 20; dispatch artifacts follow the worker artifact policy; other files are not swept.",
            OrchestratorArtifactRegistry.Classify("logs")!.Note);
    }

    private static string WriteDump(RetentionReclaimFixture fixture, TimeSpan offset, int length,
        int processId = 1, string? directory = null, bool upperCase = false)
    {
        var stamp = (RetentionReclaimFixture.Now + offset).ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var name = $"conductor-tick-stall-{processId}-{stamp}.dmp";
        var path = Path.Combine(directory ?? fixture.LogDirectory, upperCase ? name.ToUpperInvariant() : name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static string[] WriteRankDumps(RetentionReclaimFixture fixture) =>
    [
        WriteDump(fixture, TimeSpan.FromHours(-2), 11),
        WriteDump(fixture, TimeSpan.FromDays(-1), 12),
        WriteDump(fixture, TimeSpan.FromDays(-2), 13),
        WriteDump(fixture, TimeSpan.FromDays(-5), 14),
        WriteDump(fixture, TimeSpan.FromDays(-10), 15)
    ];

    private static List<EvidenceRetentionDecision> Sweep(RetentionReclaimFixture fixture)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        ConductorDiagnosticDumpRetention.Sweep(fixture.LogDirectory, RetentionReclaimFixture.Now, decisions);
        return decisions;
    }

    private static void AssertRankDecisions(string[] paths, IReadOnlyList<EvidenceRetentionDecision> decisions)
    {
        Assert.Collection(decisions,
            d => AssertDecision(d, paths[0], EvidenceRetentionAction.Preserved, "diagnostic-dump-newest"),
            d => AssertDecision(d, paths[1], EvidenceRetentionAction.Preserved, "diagnostic-dump-newest"),
            d => AssertDecision(d, paths[2], EvidenceRetentionAction.Preserved, "diagnostic-dump-within-age"),
            d => AssertDecision(d, paths[3], EvidenceRetentionAction.Deleted, "diagnostic-dump-past-age", 14),
            d => AssertDecision(d, paths[4], EvidenceRetentionAction.Deleted, "diagnostic-dump-past-age", 15));
    }

    private static void AssertDecision(EvidenceRetentionDecision decision, string path,
        EvidenceRetentionAction action, string reason, long reclaimed = 0)
    {
        Assert.Equal(EvidenceArtifactFamily.DiagnosticDumps, decision.Family);
        Assert.Equal(path, decision.Path);
        Assert.Null(decision.GoalId);
        Assert.Equal(EvidenceOwnerResolution.Unrecorded, decision.OwnerResolution);
        Assert.Equal(action, decision.Action);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(reclaimed, decision.BytesReclaimed);
        if (action == EvidenceRetentionAction.Deleted) Assert.Equal(reclaimed, decision.BytesAttempted);
        Assert.Equal(action != EvidenceRetentionAction.Deleted, File.Exists(path));
    }

    private static (EvidenceArtifactFamily, EvidenceRetentionAction, string, string)[] OtherDecisions(
        RetentionReclaimFixture fixture, StorageRetentionResult result) =>
        result.Decisions.Where(d => d.Family != EvidenceArtifactFamily.DiagnosticDumps)
            .Select(d => (d.Family, d.Action, Path.GetRelativePath(fixture.ExecutionDirectory, d.Path), d.Reason))
            .OrderBy(d => d.Family).ThenBy(d => d.Action)
            .ThenBy(d => d.Item3, StringComparer.Ordinal).ThenBy(d => d.Reason, StringComparer.Ordinal).ToArray();
}
