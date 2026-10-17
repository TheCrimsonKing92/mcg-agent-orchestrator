using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every test owns its directory tree and named leases.
public sealed class RetentionSweepPassCostTests
{
    [Xunit.Fact]
    public void Run_TwoHundredDeletions_UsesThreeFullDirectoryChecks()
    {
        using var fixture = new RetentionReclaimFixture();
        using var control = new RetentionReclaimFixture();
        var paths = CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, 200);
        CreateCandidates(control, RetentionReclaimFixture.CompletedId, 200);
        var passes = new List<AttemptDirectoryPassStats>();
        var enumerations = 0;
        var result = Run(fixture, observer: passes.Add, enumerate: directory =>
        {
            enumerations++;
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        });
        var baseline = Run(control);

        Assert.False(result.Failed);
        Assert.False(baseline.Failed);
        var pass = Assert.Single(passes);
        Assert.Equal(Path.GetDirectoryName(paths[0]), pass.Directory);
        Assert.True(pass.Candidates >= 200);
        Assert.InRange(pass.Enumerations, 1, 3);
        Assert.InRange(pass.FullPathHashes, 1, 3);
        Assert.Equal(pass.Enumerations, enumerations);
        Assert.Equal(200, pass.Deleted);
        Assert.Equal(200, result.AcceptanceArtifactsDeleted);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(DecisionMultiset(baseline), DecisionMultiset(result));
    }

    [Xunit.Theory]
    [Xunit.InlineData(200, 2, 1, 0)]
    [Xunit.InlineData(1, 30, 3, 1)]
    public void Run_AgeBound_ControlsVerificationCount(int count, int days, int checks, int deleted)
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = CreateCandidates(fixture, RetentionReclaimFixture.CompletedId, count, days);
        var passes = new List<AttemptDirectoryPassStats>();
        var result = Run(fixture, observer: passes.Add);

        Assert.False(result.Failed);
        var pass = Assert.Single(passes);
        Assert.True(pass.Candidates >= count);
        Assert.Equal(checks, pass.Enumerations);
        Assert.Equal(checks, pass.FullPathHashes);
        Assert.Equal(deleted, pass.Deleted);
        Assert.All(paths, path => Assert.Equal(deleted == 0, File.Exists(path)));
        if (deleted == 0)
            Assert.All(paths, path => Assert.Contains(result.Decisions, decision =>
                decision.Path == path && decision.Action == EvidenceRetentionAction.Preserved));
    }

    internal static string[] CreateCandidates(RetentionReclaimFixture fixture, string goalId,
        int count, int days = 30)
    {
        File.Delete(fixture.WriteFlatAttempt(goalId, "final", 2, 0));
        File.Delete(fixture.WriteFlatAttempt(goalId, "old", 1, 1));
        return Enumerable.Range(0, count).Select(index =>
        {
            var path = fixture.WriteAgedFile(
                Path.Combine(fixture.AttemptRoot(), goalId, $"old.{index:D3}.out.log"), days);
            File.SetLastWriteTimeUtc(path, RetentionReclaimFixture.Now.AddDays(-days).AddMinutes(index).UtcDateTime);
            return path;
        }).ToArray();
    }

    internal static StorageRetentionResult Run(RetentionReclaimFixture fixture,
        Action<string>? hook = null, Action<AttemptDirectoryPassStats>? observer = null,
        Func<string, IEnumerable<string>>? enumerate = null, Action<string>? beforePass = null) =>
        StorageRetentionMaintenance.Run(fixture.LogDirectory, fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory, [fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed)],
            RetentionReclaimFixture.Now, mtpResultsRoot: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"),
            beforeAttemptCandidateDeletionForTests: beforePass,
            beforeEachAttemptCandidateDeletionForTests: hook,
            attemptDirectoryEnumeratorForTests: enumerate, attemptDirectoryPassObserverForTests: observer);

    internal static void ChangeOrdinal(string directory)
    {
        var path = Path.Combine(directory, "old.attempt.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["ordinal"] = 0;
        File.WriteAllText(path, json.ToJsonString());
    }

    private static string[] DecisionMultiset(StorageRetentionResult result) =>
        result.Decisions.Select(decision => $"{decision.Action}:{decision.Reason}")
            .Order(StringComparer.Ordinal).ToArray();
}
