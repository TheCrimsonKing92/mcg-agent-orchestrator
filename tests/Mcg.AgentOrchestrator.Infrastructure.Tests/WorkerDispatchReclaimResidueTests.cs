using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every sweep owns its temporary directory and changes no environment variables.
public sealed class WorkerDispatchReclaimResidueTests
{
    private const string TaskId = "abcdef12111111111111111111111111";
    private const string Dispatch = "11111111-abcdef12-20260905000000";
    private const string FailedDispatch = "11111111-abcdef12-20260806000000";

    public static Xunit.TheoryData<string[], bool> ResidueCases => new()
    {
        { [".out.log.gz", ".err.log.gz"], true },
        { [".out.log.gz", ".out.log.jsonl"], true },
        { [], false },
        { [".err.log"], false },
        { [".out.log.gz", ".exit.txt"], false },
        { [".err.log.gz", ".child-exit.json"], false },
        { [".out.log.gz", ".err.log"], false },
        { [".dispatch.json", ".heartbeat.json"], false },
        { [".OUT.LOG.GZ", ".ERR.LOG.GZ"], true },
        { [".out.log.gz", ".EXIT.TXT"], false },
        { [".out.log.gz", ".CHILD-EXIT.JSON"], false },
        { [".out.log.gz", ".ERR.LOG"], false },
        { [".out.log.gz", ".out.log.part-001"], false },
        { [".out.log.gz", ".OUT.LOG.PART-001"], false },
        { [".out.log.part-001.gz"], true }
    };

    [Xunit.Theory]
    [Xunit.MemberData(nameof(ResidueCases))]
    public void IsResidue_RequiresCompressedArtifactsWithoutExitOrRawLogs(string[] suffixes, bool expected)
    {
        var names = suffixes.Select(suffix => Dispatch + suffix).ToArray();

        Assert.Equal(expected, WorkerDispatchReclaimResidue.IsResidue(names));
    }

    [Xunit.Fact]
    public void Run_ReclaimsResidueAndPreservesOlderGenuineFailure()
    {
        using var fixture = new RetentionReclaimFixture();
        var residue = WriteResidue(fixture, Dispatch, 15);
        var failedLog = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, FailedDispatch + ".err.log"), 45);
        var failedExit = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, FailedDispatch + ".exit.txt"), 45);
        File.WriteAllText(failedExit, "1");
        File.SetLastWriteTimeUtc(failedExit, RetentionReclaimFixture.Now.AddDays(-45).UtcDateTime);

        var result = fixture.Run(goals: [Goal(GoalStatus.Completed)]);

        Assert.False(result.Failed);
        Assert.Equal(2, result.WorkerArtifactsDeleted);
        Assert.All(residue, path => AssertDecision(result, path, EvidenceRetentionAction.Deleted,
            "dispatch-residue-past-deletion-age", exists: false));
        Assert.All(new[] { failedLog, failedExit }, path => AssertDecision(result, path,
            EvidenceRetentionAction.Preserved, "failure-evidence", exists: true));
        Assert.False(File.Exists(failedLog + ".gz"));
    }

    [Xunit.Fact]
    public void Run_KeepsYoungResidueWithinGracePeriod()
    {
        using var fixture = new RetentionReclaimFixture();
        var residue = WriteResidue(fixture, Dispatch, 2);

        var result = fixture.Run(goals: [Goal(GoalStatus.Completed)]);

        Assert.False(result.Failed);
        Assert.All(residue, path => AssertDecision(result, path, EvidenceRetentionAction.Preserved,
            "within-grace-period", exists: true));
    }

    [Xunit.Fact]
    public void Run_KeepsNonTerminalResidueUnboundedByPolicy()
    {
        using var fixture = new RetentionReclaimFixture();
        var residue = WriteResidue(fixture, Dispatch, 45);

        var result = fixture.Run(goals: [Goal(GoalStatus.Active)]);

        Assert.False(result.Failed);
        Assert.All(residue, path => AssertDecision(result, path, EvidenceRetentionAction.Preserved,
            "non-terminal-evidence-unbounded-by-policy", exists: true));
    }

    [Xunit.Fact]
    public void Run_KeepsRawLogWithoutExitAsFailureEvidence()
    {
        using var fixture = new RetentionReclaimFixture();
        var path = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, Dispatch + ".err.log"), 45);
        var original = File.ReadAllText(path);

        var result = fixture.Run(goals: [Goal(GoalStatus.Completed)]);

        Assert.False(result.Failed);
        AssertDecision(result, path, EvidenceRetentionAction.Preserved, "failure-evidence", exists: true);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".gz"));
        Assert.Equal(0, result.WorkerLogsCompressed);
    }

    [Xunit.Fact]
    public void Run_KeepsResidueWithUnmatchedGoalPrefix()
    {
        using var fixture = new RetentionReclaimFixture();
        var residue = WriteResidue(fixture, "99999999-abcdef12-20260905000000", 45);

        var result = fixture.Run(goals: [Goal(GoalStatus.Completed)]);

        Assert.False(result.Failed);
        Assert.All(residue, path => AssertDecision(result, path, EvidenceRetentionAction.RetainedUndecidable,
            "goal-prefix-is-unmatched-or-ambiguous", exists: true));
    }

    private static StorageRetentionGoal Goal(GoalStatus status) =>
        new(RetentionReclaimFixture.CompletedId, status,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Completed });

    private static string[] WriteResidue(RetentionReclaimFixture fixture, string dispatch, int days) =>
        new[] { ".out.log.gz", ".err.log.gz" }
            .Select(suffix => fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, dispatch + suffix), days))
            .ToArray();

    private static void AssertDecision(StorageRetentionResult result, string path,
        EvidenceRetentionAction action, string reason, bool exists)
    {
        Assert.Equal(exists, File.Exists(path));
        var decision = Assert.Single(result.Decisions, decision => decision.Path == path);
        Assert.Equal(EvidenceArtifactFamily.DispatchLogs, decision.Family);
        Assert.Equal(action, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }
}
