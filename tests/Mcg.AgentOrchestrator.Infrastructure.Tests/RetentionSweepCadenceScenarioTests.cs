using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the workspace, stores, cadence cache key and writer leases are test-owned.
public sealed class RetentionSweepCadenceScenarioTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task TryRunIfDue_ThreeDirectories_RecordsBoundedSweepAndDailyCadence(bool mutate)
    {
        using var fixture = new RetentionReclaimFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        string[] ids = [RetentionReclaimFixture.CompletedId, RetentionReclaimFixture.ActiveId,
            RetentionReclaimFixture.FailedId];
        int[] counts = [60, 60, 5];
        var paths = ids.Select((id, index) =>
            RetentionSweepPassCostTests.CreateCandidates(fixture, id, counts[index])).ToArray();
        await repository.SaveGoalSnapshotsAsync(ids.Select(id =>
            new GoalSnapshot(id, "retention scenario", GoalStatus.Completed, [], [])).ToArray(),
            Xunit.TestContext.Current.CancellationToken);
        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        var clock = RetentionReclaimFixture.Now;
        var calls = 0;
        var hooks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var passes = new List<AttemptDirectoryPassStats>();
        string? changedId = null;

        StorageRetentionResult Sweep(OrchestratorWorkspace current,
            IReadOnlyCollection<StorageRetentionGoal> goals, DateTimeOffset now, string? mtpRoot)
        {
            calls++;
            Assert.Equal(3, goals.Count);
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            return StorageRetentionMaintenance.Run(current.LogDirectory, current.OrchestratorDirectory,
                current.ExecutionDirectory, goals, now, mtpResultsRoot: mtpRoot,
                beforeEachAttemptCandidateDeletionForTests: path =>
                {
                    var directory = Path.GetDirectoryName(path)!;
                    RetentionSweepGuaranteeTests.AssertLeaseHeld(directory);
                    var id = Path.GetFileName(directory);
                    hooks[id] = hooks.GetValueOrDefault(id) + 1;
                    // Mutate the second directory actually visited, independent of OS enumeration order.
                    if (mutate && hooks.Count == 2 && changedId is null && hooks[id] == 3)
                    {
                        changedId = id;
                        RetentionSweepPassCostTests.ChangeOrdinal(directory);
                    }
                }, attemptDirectoryPassObserverForTests: passes.Add);
        }

        RunEventMaintenanceCadenceResult Run() => RunEventMaintenanceCadence.TryRunIfDue(
            workspace.RunEventStorePath, workspace.ConductEventsLogPath, () => clock,
            workspace: workspace, mtpResultsRootOverride: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"),
            artifactRetentionOperation: Sweep, goalSnapshotArchiveOperation: _ => 0);

        var result = Run();
        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        var sweep = Assert.IsType<StorageRetentionResult>(result.ArtifactRetention);
        Assert.False(sweep.Failed);
        Assert.Equal(1, calls);
        Assert.Equal(3, passes.Count);
        Assert.All(passes, pass =>
        {
            Assert.InRange(pass.Enumerations, 1, 3);
            Assert.InRange(pass.FullPathHashes, 1, 3);
            Assert.True(pass.Candidates >= counts[Array.IndexOf(ids, Path.GetFileName(pass.Directory))]);
        });

        var expectedDeleted = 0;
        for (var index = 0; index < ids.Length; index++)
        {
            var changed = ids[index] == changedId;
            var deleted = changed ? 2 : counts[index];
            expectedDeleted += deleted;
            Assert.Equal(changed ? 3 : counts[index], hooks[ids[index]]);
            Assert.Equal(deleted, Assert.Single(passes.Where(pass =>
                Path.GetFileName(pass.Directory) == ids[index])).Deleted);
            Assert.All(paths[index].Take(deleted), path => Assert.False(File.Exists(path)));
            Assert.All(paths[index].Skip(deleted), path =>
            {
                Assert.True(File.Exists(path));
                Assert.Contains(sweep.Decisions, decision => decision.Path == path &&
                    decision.Action == EvidenceRetentionAction.RetainedUndecidable &&
                    decision.Reason == "retention-facts-changed-before-effect");
            });
            using var lease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(
                Path.GetDirectoryName(paths[index][0])!);
            Assert.NotNull(lease);
        }
        Assert.Equal(mutate, changedId is not null);
        Assert.Equal(expectedDeleted, sweep.AcceptanceArtifactsDeleted);
        var retained = sweep.Decisions.Count(decision =>
            decision.Action == EvidenceRetentionAction.RetainedUndecidable);
        if (mutate)
        {
            Assert.True(retained > 0);
            var directoryDecision = Assert.Single(sweep.Decisions.Where(decision =>
                decision.Reason == "retention-facts-changed-during-sweep"));
            Assert.Equal(changedId, Path.GetFileName(directoryDecision.Path));
        }
        else Assert.Equal(0, retained);

        var receipt = Assert.Single((await store.ReadSinceAsync(maxCount: 100))
            .Where(record => record.Operation == RunEventMaintenanceCadence.ArtifactRetentionOperation));
        Assert.Equal(mutate ? "Partial" : "Completed", receipt.Status);
        Assert.Contains("STORAGE_RETENTION ", receipt.Detail);
        Assert.Contains($"status={(mutate ? "partial" : "completed")}", receipt.Detail);
        Assert.Contains($"Deleted:{expectedDeleted}", receipt.Detail);
        Assert.Contains($"retainedUndecidable={retained}", receipt.Detail);
        using var payload = JsonDocument.Parse(receipt.PayloadJson!);
        Assert.Equal(expectedDeleted, payload.RootElement.GetProperty("AcceptanceArtifactsDeleted").GetInt32());
        Assert.Equal(retained, payload.RootElement.GetProperty("retainedUndecidable").GetInt32());
        var journal = File.ReadAllText(workspace.ConductEventsLogPath);
        Assert.Contains("STORAGE_RETENTION ", journal);
        Assert.Contains($"status={(mutate ? "partial" : "completed")}", journal);
        Assert.Contains($"Deleted:{expectedDeleted}", journal);
        Assert.Contains($"retainedUndecidable={retained}", journal);

        Assert.True(RunEventMaintenanceCadence.IsKnownFresh(workspace.RunEventStorePath, clock.AddHours(23)));
        Assert.False(RunEventMaintenanceCadence.IsKnownFresh(workspace.RunEventStorePath, clock.AddHours(25)));
        clock = clock.AddHours(1);
        Assert.True(Run().Skipped);
        Assert.Equal(1, calls);
    }
}
