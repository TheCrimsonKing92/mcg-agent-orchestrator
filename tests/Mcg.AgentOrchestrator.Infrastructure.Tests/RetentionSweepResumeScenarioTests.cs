using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its migrated workspace, cadence key, clock and leases.
public sealed class RetentionSweepResumeScenarioTests
{
    [Xunit.Fact]
    public async Task TryRunIfDue_CutPass_ResumesBeforeRecordingDailyReceipt()
    {
        using var scenario = await Scenario.Create();
        scenario.InterruptOnDirectory = 3;
        var first = scenario.Run();
        Assert.True(first.Failed);
        Assert.True(scenario.LastSweep!.Failed);
        Assert.Equal(RetentionSweepResumeTests.Ids.Take(2), scenario.Visited);
        Assert.True(File.Exists(scenario.CursorPath));
        Assert.Empty(await scenario.Receipts());

        scenario.Clock = RetentionReclaimFixture.Now.AddHours(2);
        scenario.InterruptOnDirectory = 0;
        var second = scenario.Run();
        Assert.True(second.Attempted);
        Assert.False(second.Failed);
        Assert.False(scenario.LastSweep!.Failed);
        Assert.Equal(RetentionSweepResumeTests.Ids.Skip(2), scenario.Visited);
        Assert.All(scenario.Candidates, path => Assert.False(File.Exists(path)));
        Assert.Equal("Completed", Assert.Single(await scenario.Receipts()).Status);
        Assert.False(File.Exists(scenario.CursorPath));

        scenario.Clock = RetentionReclaimFixture.Now.AddHours(3);
        var calls = scenario.Calls;
        Assert.True(scenario.Run().Skipped);
        Assert.Equal(calls, scenario.Calls);
        Assert.Empty(scenario.Visited);
        Assert.Single(await scenario.Receipts());

        scenario.Clock = RetentionReclaimFixture.Now.AddHours(26);
        var daily = scenario.Run();
        Assert.True(daily.Attempted);
        Assert.False(daily.Failed);
        Assert.Equal(calls + 1, scenario.Calls);
        Assert.Equal(RetentionSweepResumeTests.Ids, scenario.Visited);
        var receipts = await scenario.Receipts();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Equal("Completed", receipt.Status));
        Assert.False(File.Exists(scenario.CursorPath));
    }

    [Xunit.Fact]
    public async Task TryRunIfDue_RepeatedCuts_MakeProgressWithoutReceipts()
    {
        using var scenario = await Scenario.Create();
        scenario.InterruptOnDirectory = 2;
        for (var call = 0; call < 3; call++)
        {
            scenario.Clock = RetentionReclaimFixture.Now.AddMinutes(61 * call);
            var cut = scenario.Run();
            Assert.True(cut.Attempted);
            Assert.True(cut.Failed);
            Assert.True(scenario.LastSweep!.Failed);
            Assert.Equal(new[] { RetentionSweepResumeTests.Ids[call] }, scenario.Visited);
            Assert.Empty(await scenario.Receipts());
            Assert.True(File.Exists(scenario.CursorPath));
            Assert.Equal(RetentionSweepResumeTests.Ids[call], StorageRetentionResumeCursor.Open(
                scenario.Workspace.OrchestratorDirectory, scenario.Clock, RunEventMaintenanceCadence.Interval)
                .LastCompleted(RetentionSweepResumeTests.Acceptance));
        }

        scenario.Clock = RetentionReclaimFixture.Now.AddMinutes(183);
        scenario.InterruptOnDirectory = 0;
        var completed = scenario.Run();
        Assert.True(completed.Attempted);
        Assert.False(completed.Failed);
        Assert.Equal(4, scenario.Calls);
        Assert.Equal(RetentionSweepResumeTests.Ids.Skip(3), scenario.Visited);
        Assert.All(scenario.Candidates, path => Assert.False(File.Exists(path)));
        Assert.Equal("Completed", Assert.Single(await scenario.Receipts()).Status);
        Assert.False(File.Exists(scenario.CursorPath));
    }

    private sealed class Scenario : IDisposable
    {
        private readonly RetentionReclaimFixture _fixture = new();
        internal OrchestratorWorkspace Workspace { get; }
        internal DateTimeOffset Clock { get; set; } = RetentionReclaimFixture.Now;
        internal int InterruptOnDirectory { get; set; }
        internal int Calls { get; private set; }
        internal StorageRetentionResult? LastSweep { get; private set; }
        internal List<string> Visited { get; } = [];
        internal string[] Candidates { get; }
        internal string CursorPath => StorageRetentionResumeCursor.PathFor(Workspace.OrchestratorDirectory);

        private Scenario()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_fixture.ExecutionDirectory);
            Candidates = RetentionSweepResumeTests.Seed(_fixture).SelectMany(group => group).ToArray();
        }

        internal static async Task<Scenario> Create()
        {
            var scenario = new Scenario();
            try
            {
                var repository = InfrastructureTestSupport.CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
                await repository.SaveGoalSnapshotsAsync(RetentionSweepResumeTests.Ids.Select(id =>
                    new GoalSnapshot(id, "retention resume", GoalStatus.Completed,
                        [new TaskSnapshot(TaskId.New().Value, "completed retention work", AgentRole.Developer,
                            WorkTaskStatus.Completed, null, null, null, null, null, null)], [])).ToArray(),
                    Xunit.TestContext.Current.CancellationToken);
                return scenario;
            }
            catch
            {
                scenario.Dispose();
                throw;
            }
        }

        internal RunEventMaintenanceCadenceResult Run()
        {
            Visited.Clear();
            return RunEventMaintenanceCadence.TryRunIfDue(Workspace.RunEventStorePath,
                Workspace.ConductEventsLogPath, () => Clock, workspace: Workspace,
                mtpResultsRootOverride: Path.Combine(_fixture.ExecutionDirectory, "empty-mtp"),
                artifactRetentionOperation: Sweep, goalSnapshotArchiveOperation: _ => 0);
        }

        private StorageRetentionResult Sweep(OrchestratorWorkspace workspace,
            IReadOnlyCollection<StorageRetentionGoal> goals, DateTimeOffset now, string? mtpRoot)
        {
            Calls++;
            var reached = 0;
            LastSweep = StorageRetentionMaintenance.Run(workspace.LogDirectory, workspace.OrchestratorDirectory,
                workspace.ExecutionDirectory, goals, now, mtpResultsRoot: mtpRoot,
                beforeAttemptCandidateDeletionForTests: _ =>
                {
                    if (++reached == InterruptOnDirectory) throw new IOException("cut attempt walk");
                }, attemptDirectoryPassObserverForTests: pass => Visited.Add(Path.GetFileName(pass.Directory)));
            // Discard the partial result as a process exit would: cadence must not append a receipt.
            if (InterruptOnDirectory > 0) throw new InvalidOperationException("cut conductor exit");
            return LastSweep;
        }

        internal async Task<RunEventRecord[]> Receipts()
        {
            var store = new SqliteRunEventStore(Workspace.RunEventStorePath);
            return (await store.ReadSinceAsync(maxCount: 100))
                .Where(record => record.Operation == RunEventMaintenanceCadence.ArtifactRetentionOperation).ToArray();
        }

        public void Dispose() => _fixture.Dispose();
    }
}
