using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every filesystem entry and goal belongs to the fixture.
public sealed class LogDirectoryFamilyRetentionTests
{
    internal static readonly string[] Families =
        ["spec-refinement", "post-landing-canary", "conduct-supervisor", "supervisor-handoff", "supervisor-ready"];

    [Xunit.Fact]
    public void Sweep_CountBound_UsesStartRankAndProtectsRecentWrites()
    {
        using var fixture = new RetentionReclaimFixture();
        var old = Enumerable.Range(3, 8).Select(days =>
            WriteRun(fixture, "conduct-supervisor", TimeSpan.FromDays(days), days)).ToArray();
        var young = Enumerable.Range(11, 2).Select(days =>
            WriteRun(fixture, "conduct-supervisor", TimeSpan.FromDays(days), days, writeAge: TimeSpan.FromHours(2)))
            .ToArray();

        var decisions = Sweep(fixture, keepNewestRuns: 2, maxRunsPerFamily: 5);

        Assert.Equal(old.Concat(young).Sum(run => run.Length), decisions.Count);
        for (var rank = 0; rank < old.Length; rank++)
            AssertRun(decisions, old[rank], rank >= 5 ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.Preserved,
                rank < 2 ? "log-family-newest" : rank < 5 ? "log-family-within-bounds" : "log-family-past-count-bound");
        foreach (var run in young)
            AssertRun(decisions, run, EvidenceRetentionAction.Preserved, "log-family-within-age");
    }

    [Xunit.Fact]
    public void Sweep_LockedCanaryFile_DefersItAndReclaimsSiblings()
    {
        using var fixture = new RetentionReclaimFixture();
        var newest = WriteRun(fixture, "post-landing-canary", TimeSpan.FromHours(2), 1);
        var old = WriteRun(fixture, "post-landing-canary", TimeSpan.FromDays(30), 2);
        using var held = new FileStream(old[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var decisions = Sweep(fixture, keepNewestRuns: 1);

        AssertRun(decisions, newest, EvidenceRetentionAction.Preserved, "log-family-newest");
        AssertRun(decisions, [old[0]], EvidenceRetentionAction.DeferredLocked, "log-family-locked");
        var locked = Assert.Single(decisions.Where(decision => decision.Path == old[0]));
        Assert.Equal(nameof(IOException), locked.FailureExceptionType);
        Assert.Equal(0, locked.BytesReclaimed);
        AssertRun(decisions, old.Skip(1), EvidenceRetentionAction.Deleted, "log-family-past-age");
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Sweep_OnlyStandInsOrNoGoals_RetainsSpecRunsAsUndecidable(bool standIn)
    {
        using var fixture = new RetentionReclaimFixture();
        var run = WriteRun(fixture, "spec-refinement", TimeSpan.FromDays(30), 1);
        StorageRetentionGoal[] goals = standIn
            ? [fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Active) with { IsStoreStandIn = true }]
            : [];

        var decisions = Sweep(fixture, goals);

        Assert.Equal(run.Length, decisions.Count);
        AssertRun(decisions, run, EvidenceRetentionAction.RetainedUndecidable, "log-family-goal-rules-skipped");
    }

    [Xunit.Fact]
    public void Sweep_AmbiguousGoalPrefix_RetainsSpecRunAsUndecidable()
    {
        using var fixture = new RetentionReclaimFixture();
        var run = WriteRun(fixture, "spec-refinement", TimeSpan.FromDays(30), 1);
        StorageRetentionGoal[] goals =
        [
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal("11111111999999999999999999999999", GoalStatus.Completed)
        ];

        var decisions = Sweep(fixture, goals);

        Assert.Equal(run.Length, decisions.Count);
        AssertRun(decisions, run, EvidenceRetentionAction.RetainedUndecidable, "log-family-goal-prefix-ambiguous");
    }

    [Xunit.Theory]
    [Xunit.InlineData(nameof(IOException))]
    [Xunit.InlineData(nameof(UnauthorizedAccessException))]
    [Xunit.InlineData(nameof(ArgumentException))]
    public void Sweep_ProtectionUnavailable_ReclaimsNothingAndRecordsOneDecision(string failure)
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = Families.SelectMany((family, index) =>
            WriteRun(fixture, family, TimeSpan.FromDays(30), index)).ToArray();
        Exception exception = failure switch
        {
            nameof(IOException) => new IOException("protection unavailable"),
            nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("protection unavailable"),
            _ => new ArgumentException("protection unavailable")
        };

        var decisions = Sweep(fixture, protectedPaths: () => throw exception);

        Assert.All(paths, path => Assert.True(File.Exists(path), path));
        var decision = Assert.Single(decisions);
        Assert.Equal(fixture.LogDirectory, decision.Path);
        Assert.Equal(EvidenceRetentionAction.RetainedUndecidable, decision.Action);
        Assert.Equal("log-family-protection-unavailable", decision.Reason);
        Assert.Equal(failure, decision.FailureExceptionType);
    }

    [Xunit.Fact]
    public void Sweep_AllFiveFamiliesYoung_LeavesEveryFileAndDeletesNothing()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = new List<string>();
        foreach (var family in Families)
        {
            paths.AddRange(WriteRun(fixture, family, TimeSpan.FromHours(2), 0));
            for (var index = 0; index < LogDirectoryFamilyRetention.KeepNewestRuns + 2; index++)
                paths.AddRange(WriteRun(fixture, family, TimeSpan.FromDays(30 + index), index + 1,
                    writeAge: TimeSpan.FromHours(2)));
        }
        paths.AddRange(WriteRun(fixture, "spec-refinement", TimeSpan.FromDays(70), 100,
            RetentionReclaimFixture.ActiveId, TimeSpan.FromHours(2)));
        var protectedRun = WriteRun(fixture, "conduct-supervisor", TimeSpan.FromDays(70), 100,
            writeAge: TimeSpan.FromHours(2));
        paths.AddRange(protectedRun);
        StorageRetentionGoal[] goals =
        [
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed),
            fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active)
        ];

        var decisions = Sweep(fixture, goals, () => protectedRun,
            keepNewestRuns: LogDirectoryFamilyRetention.KeepNewestRuns);

        Assert.Equal(paths.Count, decisions.Count);
        Assert.DoesNotContain(decisions, decision => decision.Action == EvidenceRetentionAction.Deleted);
        Assert.All(paths, path => Assert.True(File.Exists(path), path));
        Assert.Contains(decisions, decision => decision.Reason == "log-family-within-age");
    }

    [Xunit.Fact]
    public void Sweep_OneProtectedSibling_PreservesTheWholeRunBeforeGoalRules()
    {
        using var fixture = new RetentionReclaimFixture();
        var run = WriteRun(fixture, "spec-refinement", TimeSpan.FromDays(30), 1);

        var decisions = Sweep(fixture, [], () => [run[0].ToUpperInvariant()]);

        AssertRun(decisions, run, EvidenceRetentionAction.Preserved, "log-family-running-conductor-log");
    }

    [Xunit.Fact]
    public void Sweep_AgeBoundaries_UsesNewestSiblingWriteAndInclusiveLimits()
    {
        using var fixture = new RetentionReclaimFixture();
        var minimum = WriteRun(fixture, "conduct-supervisor", LogDirectoryFamilyRetention.MinimumAge, 1);
        var maximum = WriteRun(fixture, "conduct-supervisor", LogDirectoryFamilyRetention.MaxAge, 2);
        var expired = WriteRun(fixture, "conduct-supervisor",
            LogDirectoryFamilyRetention.MaxAge + TimeSpan.FromSeconds(1), 3);
        var refreshed = WriteRun(fixture, "conduct-supervisor", TimeSpan.FromDays(30), 4);
        File.SetLastWriteTimeUtc(refreshed[1], RetentionReclaimFixture.Now.AddHours(-2).UtcDateTime);

        var decisions = Sweep(fixture);

        AssertRun(decisions, minimum, EvidenceRetentionAction.Preserved, "log-family-within-age");
        AssertRun(decisions, maximum, EvidenceRetentionAction.Preserved, "log-family-within-bounds");
        AssertRun(decisions, expired, EvidenceRetentionAction.Deleted, "log-family-past-age");
        AssertRun(decisions, refreshed, EvidenceRetentionAction.Preserved, "log-family-within-age");
    }

    private static List<EvidenceRetentionDecision> Sweep(
        RetentionReclaimFixture fixture, IReadOnlyCollection<StorageRetentionGoal>? goals = null,
        Func<IReadOnlyCollection<string>>? protectedPaths = null, int keepNewestRuns = 0,
        int maxRunsPerFamily = LogDirectoryFamilyRetention.MaxRunsPerFamily)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        LogDirectoryFamilyRetention.Sweep(fixture.LogDirectory,
            goals ?? [fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed)],
            RetentionReclaimFixture.Now, protectedPaths ?? (() => []), decisions, keepNewestRuns, maxRunsPerFamily);
        return decisions;
    }

    internal static string[] WriteRun(RetentionReclaimFixture fixture, string family, TimeSpan age, int id,
        string goalId = RetentionReclaimFixture.CompletedId, TimeSpan? writeAge = null)
    {
        var stamp = RetentionReclaimFixture.Now.Subtract(age).ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var prefix = family switch
        {
            "spec-refinement" => $"spec-refinement-{goalId[..8]}-{stamp}",
            "post-landing-canary" => $"post-landing-canary-abcdef12-{id:x12}",
            "conduct-supervisor" => $"conduct-supervisor-{stamp}-001-{id:x32}",
            "supervisor-handoff" => $"supervisor-handoff-{stamp}-{id:x32}",
            "supervisor-ready" => $"supervisor-ready-{id:x32}",
            _ => throw new ArgumentException("Unknown test family", nameof(family))
        };
        string[] suffixes = family switch
        {
            "spec-refinement" => [".out.log", ".err.log", ".refiner.txt", "-1.refiner.txt"],
            "post-landing-canary" => ["-01-restore.out.log", "-01-restore.err.log", "-02-build.out.log",
                "-02-build.err.log", ".trx", ".gate-heartbeat.log"],
            "supervisor-ready" => [".json"],
            _ => [".out.log", ".err.log"]
        };
        return suffixes.Select(suffix =>
        {
            var path = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, prefix + suffix), 0);
            File.SetLastWriteTimeUtc(path, RetentionReclaimFixture.Now.Subtract(writeAge ?? age).UtcDateTime);
            return path;
        }).ToArray();
    }

    private static void AssertRun(IEnumerable<EvidenceRetentionDecision> decisions, IEnumerable<string> paths,
        EvidenceRetentionAction action, string reason)
    {
        foreach (var path in paths)
        {
            Assert.Equal(action != EvidenceRetentionAction.Deleted, File.Exists(path));
            var decision = Assert.Single(decisions.Where(candidate => candidate.Path == path));
            Assert.Equal(EvidenceArtifactFamily.DispatchLogs, decision.Family);
            Assert.Equal(action, decision.Action);
            Assert.Equal(reason, decision.Reason);
            if (action == EvidenceRetentionAction.Deleted)
            {
                Assert.True(decision.BytesReclaimed > 0);
                Assert.Equal(decision.BytesAttempted, decision.BytesReclaimed);
            }
        }
    }
}
