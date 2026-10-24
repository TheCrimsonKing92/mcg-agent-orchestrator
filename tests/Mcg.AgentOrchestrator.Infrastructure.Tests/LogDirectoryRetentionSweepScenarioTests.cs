using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: workspace, databases, cadence key and all log paths are fixture-owned.
public sealed class LogDirectoryRetentionSweepScenarioTests
{
    [Xunit.Fact]
    public async Task TryRunIfDue_FiveLogFamilies_ReclaimsOldRunsAndRecordsExactCount()
    {
        using var fixture = new RetentionReclaimFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.ExecutionDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveGoalSnapshotsAsync(
        [
            new GoalSnapshot(RetentionReclaimFixture.CompletedId, "completed retention goal", GoalStatus.Completed, [], []),
            new GoalSnapshot(RetentionReclaimFixture.ActiveId, "active retention goal", GoalStatus.Active, [], [])
        ], Xunit.TestContext.Current.CancellationToken);

        var removed = new List<string>();
        var retained = new List<string>();
        foreach (var family in LogDirectoryFamilyRetentionTests.Families)
        {
            retained.AddRange(LogDirectoryFamilyRetentionTests.WriteRun(fixture, family, TimeSpan.FromHours(2), 0));
            for (var index = 0; index < LogDirectoryFamilyRetention.KeepNewestRuns + 2; index++)
            {
                var age = TimeSpan.FromDays(30 + index);
                var run = LogDirectoryFamilyRetentionTests.WriteRun(fixture, family, age, index + 1);
                // The young run plus 19 old runs fill the newest-20 allowance; the last three are reclaimed.
                (index >= LogDirectoryFamilyRetention.KeepNewestRuns - 1 ? removed : retained).AddRange(run);
            }
        }
        var activeRun = LogDirectoryFamilyRetentionTests.WriteRun(fixture, "spec-refinement", TimeSpan.FromDays(70),
            100, RetentionReclaimFixture.ActiveId);
        var protectedRun = LogDirectoryFamilyRetentionTests.WriteRun(fixture, "conduct-supervisor",
            TimeSpan.FromDays(70), 100);
        retained.AddRange(activeRun);
        retained.AddRange(protectedRun);
        string[] nonFamilyNames =
        [
            "conduct-events.log", "conduct-events-20260801000000.log", "change-stream-20260801000000.log",
            "host-load-20260801T000000Z.csv", "operator-conduct-loop-daemon-20260801000000.out.log",
            "loop-stdout.log", $"{RetentionReclaimFixture.ActiveId[..8]}-abcdef12-20260801000000.out.log"
        ];
        var nonFamily = nonFamilyNames.Select(name =>
            fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, name), 70)).ToArray();
        var options = StorageRetentionReclaimOptions.Default with
        {
            ProtectedOperatorLogPaths = () => [.. protectedRun, nonFamily[4]]
        };
        var calls = 0;
        StorageRetentionResult Sweep(OrchestratorWorkspace current, IReadOnlyCollection<StorageRetentionGoal> goals,
            DateTimeOffset now, string? mtpRoot)
        {
            calls++;
            Assert.Equal(2, goals.Count);
            Assert.Contains(goals, goal => goal.GoalId == RetentionReclaimFixture.CompletedId && goal.IsTerminal);
            Assert.Contains(goals, goal => goal.GoalId == RetentionReclaimFixture.ActiveId && !goal.IsTerminal);
            return StorageRetentionMaintenance.Run(current.LogDirectory, current.OrchestratorDirectory,
                current.ExecutionDirectory, goals, now, mtpResultsRoot: mtpRoot, reclaimOptions: options);
        }

        var result = RunEventMaintenanceCadence.TryRunIfDue(workspace.RunEventStorePath, workspace.ConductEventsLogPath,
            () => RetentionReclaimFixture.Now, workspace: workspace,
            mtpResultsRootOverride: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"),
            artifactRetentionOperation: Sweep, goalSnapshotArchiveOperation: _ => 0);

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Equal(1, calls);
        var sweep = Assert.IsType<StorageRetentionResult>(result.ArtifactRetention);
        Assert.False(sweep.Failed);
        Assert.All(removed, path => Assert.False(File.Exists(path), path));
        Assert.All(retained.Concat(nonFamily), path => Assert.True(File.Exists(path), path));
        var familyDecisions = sweep.Decisions.Where(decision => decision.Reason.StartsWith("log-family-",
            StringComparison.Ordinal)).ToArray();
        Assert.Equal(removed.Count + retained.Count, familyDecisions.Length);
        Assert.All(nonFamily, path => Assert.DoesNotContain(familyDecisions, decision => decision.Path == path));
        Assert.All(activeRun, path => Assert.Contains(familyDecisions, decision => decision.Path == path &&
            decision.Action == EvidenceRetentionAction.Preserved && decision.Reason == "log-family-goal-non-terminal"));
        Assert.All(protectedRun, path => Assert.Contains(familyDecisions, decision => decision.Path == path &&
            decision.Action == EvidenceRetentionAction.Preserved && decision.Reason == "log-family-running-conductor-log"));
        var deleted = familyDecisions.Where(decision => decision.Action == EvidenceRetentionAction.Deleted).ToArray();
        Assert.Equal(removed.Order(StringComparer.Ordinal), deleted.Select(decision => decision.Path).Order(StringComparer.Ordinal));
        Assert.All(deleted, decision => Assert.Equal("log-family-past-age", decision.Reason));
        Assert.Equal(removed.Count, sweep.WorkerArtifactsDeleted);

        var store = new SqliteRunEventStore(workspace.RunEventStorePath);
        var receipt = Assert.Single((await store.ReadSinceAsync(maxCount: 100))
            .Where(record => record.Operation == RunEventMaintenanceCadence.ArtifactRetentionOperation));
        Assert.Equal("Completed", receipt.Status);
        var countPattern = @"\blog-family-past-age:" + removed.Count.ToString(CultureInfo.InvariantCulture) + @"(?=,|\s|$)";
        Assert.Matches(countPattern, receipt.Detail!);
        var journalReceipt = Assert.Single(File.ReadLines(workspace.ConductEventsLogPath)
            .Where(line => line.Contains("STORAGE_RETENTION ", StringComparison.Ordinal)));
        Assert.Matches(countPattern, journalReceipt);
        Assert.Contains(receipt.Detail!, journalReceipt);
    }
}
