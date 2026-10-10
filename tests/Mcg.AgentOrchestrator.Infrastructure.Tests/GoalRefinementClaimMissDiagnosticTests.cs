using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every test owns a unique workspace/SQLite database; console capture is AsyncLocal.
public sealed class GoalRefinementClaimMissDiagnosticTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = CreatedAt.AddMinutes(1);
    private static readonly DateTimeOffset ProcessStartedAt = CreatedAt.AddSeconds(-10);

    [Fact]
    public void Describe_MissingRow_ReportsAbsentAndNoCreationTime()
    {
        using var fixture = new WorkspaceFixture();

        Assert.Equal(
            $"row_state=absent row_created_at=none lease_started_at=none lease_age_seconds=none " +
            $"row_detail=\"none\" observed_at={ObservedAt:O} process_started_at={ProcessStartedAt:O}",
            Describe(fixture));
    }

    [Fact]
    public async Task Describe_PendingRow_ReportsCreationTime()
    {
        using var fixture = new WorkspaceFixture();
        await fixture.Repository.EnsureOutboxMessageAsync(fixture.Message, TestContext.Current.CancellationToken);

        Assert.Equal(
            $"row_state=pending row_created_at={CreatedAt:O} lease_started_at=none lease_age_seconds=none " +
            $"row_detail=\"none\" observed_at={ObservedAt:O} process_started_at={ProcessStartedAt:O}",
            Describe(fixture));
    }

    [Fact]
    public async Task Describe_QuarantinedRow_ReportsCreationTimeAndSingleLineDetail()
    {
        using var fixture = new WorkspaceFixture();
        await fixture.Repository.EnsureOutboxMessageAsync(fixture.Message, TestContext.Current.CancellationToken);
        Assert.True(await fixture.Repository.TryProcessOutboxMessageAsync(
            fixture.Message.Id,
            (_, _) => Task.FromResult(
                OrchestratorStateOutboxProcessingResult.Quarantined("poison-receipt\r\nreason=\"bad\"\trow")),
            TestContext.Current.CancellationToken));

        var diagnostic = Describe(fixture);

        Assert.StartsWith($"row_state=quarantined row_created_at={CreatedAt:O} ", diagnostic);
        Assert.Contains("row_detail=\"poison-receipt reason='bad' row\"", diagnostic);
        Assert.DoesNotContain('\r', diagnostic);
        Assert.DoesNotContain('\n', diagnostic);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-5)]
    public async Task Describe_HeldLease_ReportsProcessingAndSignedAge(int ageSeconds)
    {
        using var fixture = new WorkspaceFixture();
        await fixture.Repository.EnsureOutboxMessageAsync(fixture.Message, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OrchestratorStateOutboxProcessingResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = fixture.Repository.TryProcessOutboxMessageAsync(
            fixture.Message.Id,
            async (_, _) =>
            {
                entered.SetResult();
                return await release.Task;
            }, TestContext.Current.CancellationToken);
        try
        {
            // Hang-only bound: the processor must publish its lease-held signal.
            await entered.Task.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            var state = await fixture.Repository.GetOutboxStateAsync(fixture.Message.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(state);
            Assert.Equal(OrchestratorStateOutboxStatus.Processing, state.Status);
            Assert.NotNull(state.ProcessingStartedAt);
            var leaseStartedAt = state.ProcessingStartedAt.Value;
            var observedAt = leaseStartedAt.AddSeconds(ageSeconds);

            var diagnostic = GoalRefinementClaimMissDiagnostic.Describe(
                fixture.Repository, fixture.Message.Id, () => observedAt, ProcessStartedAt);

            Assert.StartsWith($"row_state=processing row_created_at={CreatedAt:O} ", diagnostic);
            Assert.Contains($"lease_started_at={leaseStartedAt:O} ", diagnostic);
            var reportedAge = int.Parse(Field(diagnostic, "lease_age_seconds"), CultureInfo.InvariantCulture);
            Assert.Equal(ageSeconds, reportedAge);
            Assert.True(reportedAge < 900);
            Assert.Contains($"observed_at={observedAt:O} ", diagnostic);
        }
        finally
        {
            release.TrySetResult(OrchestratorStateOutboxProcessingResult.Completed);
            Assert.True(await processing);
        }
    }

    [Fact]
    public async Task Describe_FailedRow_ReportsFailureDetailAndCreationTime()
    {
        using var fixture = new WorkspaceFixture();
        await fixture.Repository.EnsureOutboxMessageAsync(fixture.Message, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.TryProcessOutboxMessageAsync(
            fixture.Message.Id,
            (_, _) => Task.FromException<OrchestratorStateOutboxProcessingResult>(
                new InvalidOperationException("failed-receipt")), TestContext.Current.CancellationToken));

        var state = await fixture.Repository.GetOutboxStateAsync(fixture.Message.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(state);
        Assert.Equal(OrchestratorStateOutboxStatus.Failed, state.Status);
        // Failure releases the claim token but retains the last processing start timestamp.
        Assert.NotNull(state.ProcessingStartedAt);
        var leaseStartedAt = state.ProcessingStartedAt.Value;
        var observedAt = leaseStartedAt.AddSeconds(42);

        var diagnostic = GoalRefinementClaimMissDiagnostic.Describe(
            fixture.Repository, fixture.Message.Id, () => observedAt, ProcessStartedAt);

        Assert.Equal(
            $"row_state=failed row_created_at={CreatedAt:O} " +
            $"lease_started_at={leaseStartedAt:O} lease_age_seconds=42 " +
            $"row_detail=\"failed-receipt\" observed_at={observedAt:O} process_started_at={ProcessStartedAt:O}",
            diagnostic);
    }

    [Fact]
    public void Report_ClaimMiss_AppendsAllFieldsToStderrAndExitsOne()
    {
        using var fixture = new WorkspaceFixture();
        var result = CaptureMiss(fixture);

        Assert.Equal(1, result.Failure.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith(
            $"SPEC_REFINEMENT_WORK_FAILED goal={fixture.GoalId.Value} reason=claim-miss " +
            $"message_id={fixture.Message.Id} store_path={Path.GetFullPath(fixture.Workspace.SqliteStatePath)} " +
            "row_state=absent row_created_at=none lease_started_at=none lease_age_seconds=none row_detail=\"none\" ",
            result.Stderr);
        foreach (var key in new[] { "observed_at", "process_started_at" })
        {
            var timestamp = Field(result.Stderr, key);
            var parsed = DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture);
            Assert.Equal(TimeSpan.Zero, parsed.Offset);
            Assert.Equal(timestamp, parsed.ToString("O", CultureInfo.InvariantCulture));
        }
        Assert.EndsWith(Environment.NewLine, result.Stderr);
        Assert.DoesNotContain('\n', result.Stderr.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task Report_UnreadableDatabase_StillReportsClaimMissAndExitsOne()
    {
        using var fixture = new WorkspaceFixture(corrupt: true);
        // Prove the real exact-state boundary throws before checking the reporter's fallback.
        var readFailure = await Assert.ThrowsAsync<SqliteException>(() =>
            fixture.Repository.GetOutboxStateAsync(fixture.Message.Id, TestContext.Current.CancellationToken));

        var result = CaptureMiss(fixture);

        Assert.Equal(1, result.Failure.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("SPEC_REFINEMENT_WORK_FAILED", result.Stderr);
        Assert.Contains("row_state=unreadable", result.Stderr);
        Assert.Contains("row_created_at=none lease_started_at=none lease_age_seconds=none", result.Stderr);
        Assert.Contains($"row_detail=\"SqliteException: {readFailure.Message}\"", result.Stderr);
        Assert.DoesNotContain('\n', result.Stderr.TrimEnd('\r', '\n'));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Report_ClaimHit_PreservesExactSuccessLine(bool attached)
    {
        using var fixture = new WorkspaceFixture(corrupt: true);
        var stdout = string.Empty;

        var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
            stdout = AsyncLocalConsoleRouter.Capture(() => GoalRefinementWorkOutcomeReporter.Report(
                new GoalRefinementWorkProcessResult(fixture.GoalId.Value, Claimed: true, Attached: attached),
                fixture.Workspace, fixture.Repository)));

        Assert.Equal(
            $"SPEC_REFINEMENT_WORK_COMPLETE goal={fixture.GoalId.Value} claimed=true " +
            $"attached={attached.ToString().ToLowerInvariant()}{Environment.NewLine}", stdout);
        Assert.Empty(stderr);
    }

    private static string Describe(WorkspaceFixture fixture) =>
        GoalRefinementClaimMissDiagnostic.Describe(
            fixture.Repository, fixture.Message.Id, () => ObservedAt, ProcessStartedAt);

    private static string Field(string diagnostic, string key) =>
        diagnostic.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Single(field => field.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..].TrimEnd('\r', '\n');

    private static (string Stdout, string Stderr, CliExitException Failure) CaptureMiss(WorkspaceFixture fixture)
    {
        var stdout = string.Empty;
        CliExitException? failure = null;
        var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
            stdout = AsyncLocalConsoleRouter.Capture(() =>
                failure = Assert.Throws<CliExitException>(() => GoalRefinementWorkOutcomeReporter.Report(
                    new GoalRefinementWorkProcessResult(fixture.GoalId.Value, Claimed: false, Attached: false),
                    fixture.Workspace, fixture.Repository))));
        Assert.NotNull(failure);
        return (stdout, stderr, failure);
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-claim-miss-" + Guid.NewGuid().ToString("N"));
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public GoalId GoalId { get; } = GoalId.New();
        public OrchestratorStateOutboxMessage Message { get; }

        public WorkspaceFixture(bool corrupt = false)
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            if (corrupt)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Workspace.SqliteStatePath)!);
                File.WriteAllText(Workspace.SqliteStatePath, new string('x', 4096));
            }
            else
            {
                _ = StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            }
            Repository = new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath);
            Message = GoalRefinementWorkCoordinator.CreateMessage(GoalId) with { CreatedAt = CreatedAt };
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
