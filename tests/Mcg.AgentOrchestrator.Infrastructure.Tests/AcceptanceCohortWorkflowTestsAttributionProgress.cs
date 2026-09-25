using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsAttributionProgress : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void RedCohort_ReportsOrderedAttributionAndProgressDuringEachPartition()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First attribution member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second attribution member", repo);
            _ = CreateWorktreeCandidate(repo, firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var verifier = new GateProgressReportingVerifier(workspace.ConductEventsLogPath,
            [
                FailedVerification(repo, "combined-red-progress.trx", "combined"),
                FailedVerification(repo, "first-partition-red-progress.trx", "first partition"),
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("second partition", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "second-partition-green-progress.trx")])
            ]);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectSelection(driver, firstGoal, secondGoal);

            var result = driver.RunAcceptanceCohort(
                selection, [firstGoal, secondGoal], ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, verifier.EventsAfterProgress.Count);
            var identity = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt).Identity;
            var cohortTag = $"cohort={identity.Value[..18]}";
            var firstId = firstGoal.Id.Value[..8];
            var secondId = secondGoal.Id.Value[..8];
            AssertPartitionProgress(verifier.EventsAfterProgress[1], firstId, cohortTag);
            AssertPartitionProgress(verifier.EventsAfterProgress[2], secondId, cohortTag);

            var events = GateProgressReportingVerifier.ReadEvents(workspace.ConductEventsLogPath);
            var start = Assert.Single(events.Where(record => record.EventKind == "cohort-attribution"));
            Assert.Null(start.GoalId);
            Assert.Equal($"ATTRIBUTION_START {cohortTag} members={firstId},{secondId} scope=attribution", start.Detail);
            var startIndex = Array.IndexOf(events, start);
            Assert.True(startIndex > 0);
            Assert.DoesNotContain(events[..startIndex], record => record.Detail.Contains("scope=attribution", StringComparison.Ordinal));
            Assert.True(Array.FindIndex(events, record => record.Detail.Contains("scope=attribution", StringComparison.Ordinal) &&
                record.EventKind == "gate-progress") > startIndex);

            var comparisonPath = Path.Combine(repo, "combined-progress-comparison.jsonl");
            ConductorDriver.AppendCohortGateProgressEvents(
                new ConductEventLogWriter(comparisonPath), identity, identity.Members, verifier.Progresses[0]);
            var expectedCombined = GateProgressReportingVerifier.ReadEvents(comparisonPath)
                .Select(record => (record.EventKind, record.GoalId, record.Detail));
            var actualCombined = events[..startIndex]
                .Where(record => record.EventKind == "gate-progress")
                .Select(record => (record.EventKind, record.GoalId, record.Detail));
            Assert.Equal(expectedCombined, actualCombined);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static void AssertPartitionProgress(
        IReadOnlyList<ConductEventRecord> events,
        string goalId,
        string cohortTag) =>
        Assert.Contains(events, record => record.EventKind == "gate-progress" &&
            record.GoalId == goalId &&
            record.Detail.Contains(cohortTag, StringComparison.Ordinal) &&
            record.Detail.Contains($"member={goalId}", StringComparison.Ordinal) &&
            record.Detail.Contains("scope=attribution", StringComparison.Ordinal));
}

internal sealed class GateProgressReportingVerifier(
    string logPath,
    IReadOnlyList<AcceptanceVerificationResult> results) : IGoalAcceptanceVerifier
{
    internal List<ConductEventRecord[]> EventsAfterProgress { get; } = [];
    internal List<AcceptanceGateProgress> Progresses { get; } = [];

    public Task<AcceptanceVerificationResult> RunOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        IReadOnlyList<string>? changedFiles,
        int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease,
        IAcceptanceAttemptExecutionOwner executionOwner)
    {
        var index = EventsAfterProgress.Count;
        Assert.True(index < results.Count, "The gate ran more attempts than the fixture supplies.");
        var now = DateTimeOffset.UtcNow;
        var progress = new AcceptanceGateProgress(
            GoalId: goalId?.Value,
            Phase: "controlled-progress",
            CurrentTarget: "gate-progress-fixture",
            SlotIndex: stableSlotIndex,
            ProcessId: Environment.ProcessId,
            ChildProcessId: null,
            StartedAt: now,
            LastObservedAt: now,
            LastProgressAt: now,
            Elapsed: TimeSpan.Zero,
            OutputBytes: 0,
            HeartbeatPath: "fixture-heartbeat.json");
        Progresses.Add(progress);
        ((IAcceptanceRunExecutionContext)executionOwner).ReportProgress(progress);
        EventsAfterProgress.Add(ReadEvents(logPath));
        return Task.FromResult(results[index]);
    }

    public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
        string worktreePath,
        GoalId? goalId,
        string request,
        IAcceptanceFocusedVerificationOwner executionOwner,
        int? stableSlotIndex = null,
        DotnetBuildEnvironmentLease? stableSlotLease = null,
        bool runBaselineArm = false) =>
        throw new NotSupportedException("Focused evidence is not used by the progress fixture.");

    internal static ConductEventRecord[] ReadEvents(string path) =>
        (File.Exists(path) ? File.ReadAllLines(path) : [])
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
            line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .ToArray();
}
