using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class TerminalGoalJournalMetadataCacheTests : ConductorBatchLoopTests
{
    public TerminalGoalJournalMetadataCacheTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "Terminal_journal_metadata_is_reused_until_file_identity_changes")]
    public void TerminalJournalMetadataIsReusedUntilFileIdentityChanges()
    {
        var root = CreateTempDirectory("mcg-terminal-journal-cache");
        try
        {
            var (kernel, landed, retired, plain) = SeedTerminalGoals(root);
            var repository = OpenStateRepository(Path.Combine(root, "state.db"));
            repository.SaveAsync(kernel).GetAwaiter().GetResult();
            var terminalIds = new[] { landed.Id, retired.Id, plain.Id };
            var originals = terminalIds.ToDictionary(
                id => id,
                id => File.ReadAllBytes(GoalOperationJournal.PathFor(root, id)));

            var first = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var second = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            AssertMetadata(second, landed.Id, retired.Id, plain.Id);
            foreach (var id in terminalIds)
            {
                var path = GoalOperationJournal.PathFor(root, id);
                var timestamp = File.GetLastWriteTimeUtc(path);
                File.WriteAllBytes(path, Enumerable.Repeat((byte)'!', originals[id].Length).ToArray());
                File.SetLastWriteTimeUtc(path, timestamp);
            }

            var third = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            AssertMetadata(first, landed.Id, retired.Id, plain.Id);
            AssertMetadata(third, landed.Id, retired.Id, plain.Id);

            var plainPath = GoalOperationJournal.PathFor(root, plain.Id);
            File.WriteAllBytes(plainPath, originals[plain.Id]);
            GoalOperationJournal.RecordLandingIntent(root, plain, "goal/plain", "main", "def456", "test");
            var fourth = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);

            Xunit.Assert.True(fourth.IsKnownCompletedDependencyGoal(landed.Id));
            Xunit.Assert.True(fourth.IsKnownCompletedDependencyGoal(plain.Id));
            Xunit.Assert.False(fourth.IsKnownCompletedDependencyGoal(retired.Id));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_journal_cache_tracks_live_archive_and_missing_identity")]
    public void TerminalJournalCacheTracksLiveArchiveAndMissingIdentity()
    {
        var root = CreateTempDirectory("mcg-terminal-journal-archive-cache");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel, "Archived terminal");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            GoalOperationJournal.RecordTerminalDisposition(root, goal,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired before archival."));
            var repository = OpenStateRepository(Path.Combine(root, "state.db"));
            repository.SaveAsync(kernel).GetAwaiter().GetResult();

            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var live = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var liveTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();
            var livePath = GoalOperationJournal.PathFor(root, goal.Id);
            var archivePath = GoalOperationJournal.ArchivePathFor(root, goal.Id);
            Directory.CreateDirectory(GoalOperationJournal.ArchiveDirectoryFor(root));
            File.Move(livePath, archivePath);

            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var archived = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var archivedTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();
            File.Delete(archivePath);
            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var missing = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var missingTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();
            GoalOperationJournal.RecordLandingIntent(root, goal, "goal/recreated", "main", "abc123", "test");
            TerminalGoalJournalMetadataCache.BeginMeasurement();
            var recreated = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var recreatedTiming = TerminalGoalJournalMetadataCache.CompleteMeasurement();

            AssertDependencyStatus(live, goal.Id, "Retired");
            AssertDependencyStatus(archived, goal.Id, "Retired");
            AssertDependencyStatus(missing, goal.Id, GoalStatus.Completed.ToString());
            Xunit.Assert.False(live.IsKnownCompletedDependencyGoal(goal.Id));
            Xunit.Assert.False(archived.IsKnownCompletedDependencyGoal(goal.Id));
            Xunit.Assert.False(missing.IsKnownCompletedDependencyGoal(goal.Id));
            Xunit.Assert.True(recreated.IsKnownCompletedDependencyGoal(goal.Id));
            Xunit.Assert.Equal(1, liveTiming.JournalsRead);
            Xunit.Assert.Equal(1, archivedTiming.JournalsRead);
            Xunit.Assert.Equal(0, missingTiming.JournalsRead);
            Xunit.Assert.Equal(1, recreatedTiming.JournalsRead);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "Sweep_phase_timing_reports_dependency_metadata_cache_reads")]
    public void SweepPhaseTimingReportsDependencyMetadataCacheReads()
    {
        var root = CreateTempDirectory("mcg-terminal-journal-timing");
        try
        {
            var (storedKernel, _, _, _) = SeedTerminalGoals(root);
            var repository = OpenStateRepository(Path.Combine(root, "state.db"));
            repository.SaveAsync(storedKernel).GetAwaiter().GetResult();
            var (loopKernel, _) = SimpleGoal("Keep timing loop active");
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => 1);

            var output = CaptureConsole(() => new ConductorBatchLoop(
                measuredSweep: _ =>
                {
                    _ = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
                    return null;
                }).Run(
                    loopKernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 2,
                    watchInterval: TimeSpan.FromMilliseconds(1),
                    sleepFunc: _ => false));

            var sweepLines = output.Split(Environment.NewLine)
                .Where(line => line.Contains("PHASE_TIMING", StringComparison.Ordinal) &&
                    line.Contains("phase=sweep", StringComparison.Ordinal))
                .ToArray();
            Xunit.Assert.Equal(2, sweepLines.Length);
            Xunit.Assert.Contains("dependency_metadata_ms=", sweepLines[0], StringComparison.Ordinal);
            Xunit.Assert.Contains("dependency_journals_read=3", sweepLines[0], StringComparison.Ordinal);
            Xunit.Assert.Contains("dependency_metadata_ms=", sweepLines[1], StringComparison.Ordinal);
            Xunit.Assert.Contains("dependency_journals_read=0", sweepLines[1], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Landed, Goal Retired, Goal Plain) SeedTerminalGoals(string root)
    {
        var kernel = new AgentOrchestratorKernel();
        var landed = CreateActiveGoal(kernel, "Landed terminal");
        var retired = CreateActiveGoal(kernel, "Retired terminal");
        var plain = CreateActiveGoal(kernel, "Plain terminal");
        _ = CreateActiveGoal(kernel, "Active one");
        _ = CreateActiveGoal(kernel, "Active two");
        kernel = WithGoalStatus(WithGoalStatus(WithGoalStatus(
            kernel, landed.Id, GoalStatus.Completed), retired.Id, GoalStatus.Completed), plain.Id, GoalStatus.Completed);
        GoalOperationJournal.RecordLandingIntent(root, landed, "goal/landed", "main", "abc123", "test");
        GoalOperationJournal.RecordTerminalDisposition(root, retired,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired without landing."));
        GoalOperationJournal.Completed(root, plain, "test:plain", "Plain terminal journal.");
        return (kernel, landed, retired, plain);
    }

    private static Goal CreateActiveGoal(AgentOrchestratorKernel kernel, string objective)
    {
        var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), "Test", AgentRole.Planner)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return goal;
    }

    private static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
    }

    private static void AssertMetadata(
        AgentOrchestratorKernel kernel,
        GoalId landed,
        GoalId retired,
        GoalId plain)
    {
        Xunit.Assert.True(kernel.IsKnownCompletedDependencyGoal(landed));
        Xunit.Assert.False(kernel.IsKnownCompletedDependencyGoal(retired));
        Xunit.Assert.False(kernel.IsKnownCompletedDependencyGoal(plain));
        Xunit.Assert.True(kernel.TryGetKnownDependencyGoalStatus(retired, out var retiredStatus));
        Xunit.Assert.Equal("Retired", retiredStatus);
    }

    private static void AssertDependencyStatus(AgentOrchestratorKernel kernel, GoalId goalId, string expected)
    {
        Xunit.Assert.True(kernel.TryGetKnownDependencyGoalStatus(goalId, out var status));
        Xunit.Assert.Equal(expected, status);
    }
}
