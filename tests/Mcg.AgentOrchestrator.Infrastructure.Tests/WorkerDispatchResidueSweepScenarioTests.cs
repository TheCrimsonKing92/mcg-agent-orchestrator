using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: workspace, state store, cadence key and log paths are fixture-owned.
public sealed class WorkerDispatchResidueSweepScenarioTests
{
    [Xunit.Fact]
    public async Task TryRunIfDue_ReclaimsResidueOnNextSweepAndKeepsGenuineFailure()
    {
        using var fixture = new RetentionReclaimFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var taskId = TaskId.New().Value;
        var goal = new GoalSnapshot(RetentionReclaimFixture.CompletedId, "completed residue goal", GoalStatus.Completed,
            [new TaskSnapshot(taskId, "Completed residue task", AgentRole.Developer,
                WorkTaskStatus.Completed, null, null, null, null, null, null)], []);
        await repository.SaveGoalSnapshotsAsync([goal], Xunit.TestContext.Current.CancellationToken);

        var dispatchedAt = RetentionReclaimFixture.Now.AddDays(-17.5);
        var stem = $"{goal.Id[..8]}-{taskId[..8]}-";
        var success = stem + dispatchedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var failure = stem + dispatchedAt.AddDays(-30).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var successfulExit = WriteArtifact(success + ".exit.txt", "0", dispatchedAt);
        var successfulDispatch = WriteArtifact(success + ".dispatch.json", "{}", dispatchedAt);
        string[] compressedLogs =
        [
            WriteArtifact(success + ".out.log.gz", "compressed output", dispatchedAt.AddDays(1.5)),
            WriteArtifact(success + ".err.log.gz", "compressed error", dispatchedAt.AddDays(1.5))
        ];
        string[] failedArtifacts =
        [
            WriteArtifact(failure + ".exit.txt", "1", dispatchedAt.AddDays(-30)),
            WriteArtifact(failure + ".err.log", "genuine failure", dispatchedAt.AddDays(-30))
        ];
        var calls = 0;
        StorageRetentionResult Sweep(OrchestratorWorkspace current, IReadOnlyCollection<StorageRetentionGoal> goals,
            DateTimeOffset now, string? mtpRoot)
        {
            calls++;
            var savedGoal = Assert.Single(goals);
            Assert.Equal(goal.Id, savedGoal.GoalId);
            Assert.True(savedGoal.IsTerminal);
            Assert.Equal(WorkTaskStatus.Completed, savedGoal.TaskStatuses[taskId]);
            return StorageRetentionMaintenance.Run(current.LogDirectory, current.OrchestratorDirectory,
                current.ExecutionDirectory, goals, now, mtpResultsRoot: mtpRoot);
        }

        var first = RunAt(dispatchedAt.AddDays(14.5));

        Assert.True(first.Attempted);
        Assert.False(first.Failed);
        Assert.Equal(1, calls);
        var firstSweep = Assert.IsType<StorageRetentionResult>(first.ArtifactRetention);
        Assert.False(firstSweep.Failed);
        Assert.All(new[] { successfulExit, successfulDispatch }, path =>
            AssertDecision(firstSweep, path, EvidenceRetentionAction.Deleted, "past-deletion-age", exists: false));
        Assert.All(compressedLogs, path => AssertDecision(firstSweep, path, EvidenceRetentionAction.Preserved,
            "within-grace-period", exists: true));
        Assert.All(failedArtifacts, path => AssertDecision(firstSweep, path, EvidenceRetentionAction.Preserved,
            "failure-evidence", exists: true));

        var second = RunAt(dispatchedAt.AddDays(17.5));

        Assert.True(second.Attempted);
        Assert.False(second.Failed);
        Assert.Equal(2, calls);
        var secondSweep = Assert.IsType<StorageRetentionResult>(second.ArtifactRetention);
        Assert.False(secondSweep.Failed);
        Assert.All(compressedLogs, path => AssertDecision(secondSweep, path, EvidenceRetentionAction.Deleted,
            "dispatch-residue-past-deletion-age", exists: false));
        Assert.All(failedArtifacts, path => AssertDecision(secondSweep, path, EvidenceRetentionAction.Preserved,
            "failure-evidence", exists: true));
        Assert.False(File.Exists(failedArtifacts[1] + ".gz"));

        RunEventMaintenanceCadenceResult RunAt(DateTimeOffset now) =>
            RunEventMaintenanceCadence.TryRunIfDue(workspace.RunEventStorePath, workspace.ConductEventsLogPath,
                () => now, workspace: workspace,
                mtpResultsRootOverride: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"),
                artifactRetentionOperation: Sweep, goalSnapshotArchiveOperation: _ => 0);

        string WriteArtifact(string name, string content, DateTimeOffset writtenAt)
        {
            var path = Path.Combine(fixture.LogDirectory, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
            return path;
        }
    }

    private static void AssertDecision(StorageRetentionResult result, string path,
        EvidenceRetentionAction action, string reason, bool exists)
    {
        Assert.Equal(exists, File.Exists(path));
        var decision = Assert.Single(result.Decisions, decision => decision.Path == path);
        Assert.Equal(action, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }
}
