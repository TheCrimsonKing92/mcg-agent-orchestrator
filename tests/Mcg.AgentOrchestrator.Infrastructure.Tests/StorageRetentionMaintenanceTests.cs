using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class StorageRetentionMaintenanceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
    private const string GoalId = "11111111111111111111111111111111";
    private const string TaskId = "22222222222222222222222222222222";

    [Xunit.Fact(DisplayName = "WorkerRetention_artifact_one_day_inside_threshold_is_retained_as_gzip")]
    public void ArtifactOneDayInsideThresholdIsRetainedAsGzip()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("out.log", Now.AddDays(-13));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_artifact_one_day_outside_threshold_is_removed")]
    public void ArtifactOneDayOutsideThresholdIsRemoved()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("exit.txt", Now.AddDays(-15));

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(path));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_failed_dispatch_log_is_kept_past_threshold")]
    public void FailedDispatchLogIsKeptPastThreshold()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("err.log", Now.AddDays(-45));

        fixture.Run(TerminalGoal(WorkTaskStatus.Failed));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_earlier_failed_dispatch_is_kept_after_task_retry_succeeds")]
    public void EarlierFailedDispatchIsKeptAfterTaskRetrySucceeds()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("err.log", Now.AddDays(-45));
        var exitPath = fixture.WorkerArtifactPath("exit.txt");
        DispatchExitArtifacts.Write(exitPath, DispatchExitArtifacts.Native(1, "failed", Now.AddDays(-45)));
        File.SetLastWriteTimeUtc(exitPath, Now.AddDays(-45).UtcDateTime);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(exitPath));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_goal_with_no_terminal_state_is_untouched")]
    public void GoalWithNoTerminalStateIsUntouched()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WriteWorkerArtifact("out.log", Now.AddDays(-45));

        fixture.Run(new StorageRetentionGoal(
            GoalId,
            GoalStatus.Active,
            new Dictionary<string, WorkTaskStatus> { [TaskId] = WorkTaskStatus.Completed }));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".gz"));
    }

    [Xunit.Fact(DisplayName = "DispatchDiagnostics_unread_file_is_not_retained")]
    public void UnreadDispatchDiagnosticsFileIsNotRetained()
    {
        using var fixture = new RetentionFixture();
        var path = Path.Combine(fixture.LogDirectory, "dispatch-diagnostics.jsonl");
        File.WriteAllText(path, "legacy diagnostics");

        fixture.Run();

        Assert.False(File.Exists(path));
    }

    [Xunit.Fact(DisplayName = "WorkerRetention_sweep_locked_appending_file_is_preserved_without_throwing")]
    public async Task SweepLockedAppendingFileIsPreservedWithoutThrowing()
    {
        using var fixture = new RetentionFixture();
        var path = fixture.WorkerArtifactPath("out.log");
        File.WriteAllText(path, "before");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-15).UtcDateTime);
        var writerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Position = stream.Length;
            writerReady.SetResult();
            await releaseWriter.Task;
            await stream.WriteAsync("after"u8.ToArray());
        });

        await writerReady.Task;
        try
        {
            var exception = Record.Exception(() => fixture.Run(TerminalGoal(WorkTaskStatus.Completed)));
            Assert.Null(exception);
            Assert.True(File.Exists(path));
        }
        finally
        {
            releaseWriter.SetResult();
            await writer;
        }

        Assert.Equal("beforeafter", File.ReadAllText(path));
    }

    [Xunit.Fact(DisplayName = "AcceptanceRetention_last_failing_attempt_and_summaries_are_preserved")]
    public void LastFailingAttemptAndSummariesArePreserved()
    {
        using var fixture = new RetentionFixture();
        var goalDirectory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", GoalId);
        Directory.CreateDirectory(goalDirectory);
        var failingMetadata = Path.Combine(goalDirectory, "failure.attempt.json");
        var failingLog = Path.Combine(goalDirectory, "failure.err.log");
        var successfulTrx = Path.Combine(goalDirectory, "success.trx");
        File.WriteAllText(failingMetadata, "{\"outcome\":2}");
        File.WriteAllText(failingLog, "failure detail");
        File.WriteAllText(successfulTrx, SuccessfulTrx("Suite.Test"));
        SetAge(failingMetadata, 30);
        SetAge(failingLog, 30);
        SetAge(successfulTrx, 30);

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.True(File.Exists(failingMetadata));
        Assert.True(File.Exists(failingLog));
        Assert.False(File.Exists(successfulTrx));
        var receiptPath = successfulTrx + ".test-identities.json";
        Assert.True(File.Exists(receiptPath));
        Assert.Contains("Suite.Test", File.ReadAllText(receiptPath), StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_archive_is_skipped_by_ReadAll")]
    public void ArchiveIsSkippedByReadAll()
    {
        using var fixture = new RetentionFixture();
        var source = GoalOperationJournal.PathFor(fixture.ExecutionDirectory, new GoalId(GoalId));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "{\"at\":\"2026-08-01T00:00:00Z\"}\n");

        fixture.Run(TerminalGoal(WorkTaskStatus.Completed));

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(GoalOperationJournal.ArchivePathFor(fixture.ExecutionDirectory, new GoalId(GoalId))));
        Assert.DoesNotContain(new GoalId(GoalId), GoalOperationJournal.ReadAll(fixture.ExecutionDirectory).Keys);
    }

    [Xunit.Fact(DisplayName = "RollingLogWriteStream_crossing_byte_threshold_rolls_without_truncating")]
    public void CrossingByteThresholdRollsWithoutTruncating()
    {
        using var fixture = new RetentionFixture();
        var path = Path.Combine(fixture.LogDirectory, "rolling.out.log");
        using (var stream = new RollingLogWriteStream(path, FileMode.Create, maxBytes: 8))
        {
            stream.Write("12345678"u8);
            stream.Write("90"u8);
        }

        Assert.Equal("12345678", File.ReadAllText(path + ".part-0001"));
        Assert.Equal("90", File.ReadAllText(path));
    }

    private static StorageRetentionGoal TerminalGoal(WorkTaskStatus taskStatus) =>
        new(GoalId, GoalStatus.Completed, new Dictionary<string, WorkTaskStatus> { [TaskId] = taskStatus });

    private static string SuccessfulTrx(string testName) =>
        $"<TestRun><Results><UnitTestResult testName=\"{testName}\" outcome=\"Passed\" /></Results>" +
        "<ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>";

    private static void SetAge(string path, int days) => File.SetLastWriteTimeUtc(path, Now.AddDays(-days).UtcDateTime);

    private sealed class RetentionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-retention-tests", Guid.NewGuid().ToString("n"));

        public RetentionFixture()
        {
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(OrchestratorDirectory);
        }

        public string ExecutionDirectory => _root;
        public string OrchestratorDirectory => Path.Combine(_root, ".orchestrator");
        public string LogDirectory => Path.Combine(OrchestratorDirectory, "logs");

        public string WorkerArtifactPath(string suffix) =>
            Path.Combine(LogDirectory, $"{GoalId[..8]}-{TaskId[..8]}-20260801120000.{suffix}");

        public string WriteWorkerArtifact(string suffix, DateTimeOffset lastWrite)
        {
            var path = WorkerArtifactPath(suffix);
            File.WriteAllText(path, "artifact");
            File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
            return path;
        }

        public StorageRetentionResult Run(params StorageRetentionGoal[] goals) =>
            StorageRetentionMaintenance.Run(LogDirectory, OrchestratorDirectory, ExecutionDirectory, goals, Now);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
