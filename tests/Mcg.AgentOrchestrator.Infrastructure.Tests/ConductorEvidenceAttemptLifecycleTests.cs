using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorEvidenceAttemptLifecycleTests
{
    [Fact]
    public void SupersedingLiveAttempt_RecordsTypedEndBeforeReplacementStartExactlyOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var writer = new ConductEventLogWriter(logPath);
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Capture focused evidence lifecycle");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var firstCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7101),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer);

            var first = firstCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            Assert.True(firstCoordinator.InvalidateCurrent(goal.Id.Value, "operator retry replaced attempt"));

            var replacementCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102),
                recentHeartbeatGrace: TimeSpan.Zero,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer);
            var replacement = replacementCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            replacementCoordinator.RunAttemptForTests(
                first.Attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(
                    attemptCandidate,
                    PassingEvidence(attemptCandidate.Goal, "run focused tests", null, CancellationToken.None)));

            var events = ReadEvents(logPath);
            var firstStart = Assert.Single(events.Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_START" &&
                item.GetProperty("attempt").GetString() == first.Attempt.AttemptId));
            Assert.Equal(goal.Id.Value, firstStart.GetProperty("goal").GetString());
            Assert.Equal(first.Attempt.AttemptId, firstStart.GetProperty("attempt").GetString());
            Assert.Equal(1, firstStart.GetProperty("ordinal").GetInt32());
            Assert.Equal(JsonValueKind.String, firstStart.GetProperty("timestamp").ValueKind);
            var endIndexes = events
                .Select((item, index) => (item, index))
                .Where(pair => pair.item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                    pair.item.GetProperty("attempt").GetString() == first.Attempt.AttemptId)
                .ToArray();
            var replacementStartIndex = events.FindIndex(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_START" &&
                item.GetProperty("attempt").GetString() == replacement.Attempt.AttemptId);

            var end = Assert.Single(endIndexes);
            Assert.True(end.index < replacementStartIndex);
            Assert.Equal("superseded", end.item.GetProperty("outcome").GetString());
            Assert.Equal(replacement.Attempt.AttemptId, end.item.GetProperty("superseded_by").GetString());
            Assert.Equal("retry_invalidated", end.item.GetProperty("cause").GetString());
            Assert.Equal(goal.Id.Value, end.item.GetProperty("goal").GetString());
            Assert.Equal(first.Attempt.AttemptId, end.item.GetProperty("attempt").GetString());
            Assert.Equal(1, end.item.GetProperty("ordinal").GetInt32());
            Assert.Equal(JsonValueKind.Number, end.item.GetProperty("duration_s").ValueKind);
            Assert.Equal("unknown", end.item.GetProperty("tests_executed").GetString());
            Assert.Equal(2, replacement.Attempt.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.Failed), "failed")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.Faulted), "faulted")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.Cancelled), "cancelled")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.LaunchFailed), "launch_failed")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.BlockedBuildSlot), "blocked_build_slot")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.BlockedBuildLock), "blocked_build_lock")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.InfrastructureDeferred), "infrastructure_deferred")]
    [InlineData(nameof(ConductorEvidenceAttemptOutcome.CorruptArtifacts), "corrupt_artifacts")]
    public void TerminalOutcome_EmitsExactTypedTaxonomyValue(
        string expectedOutcomeName,
        string expectedToken)
    {
        var root = CreateTempDirectory();
        try
        {
            var expectedOutcome = Enum.Parse<ConductorEvidenceAttemptOutcome>(expectedOutcomeName);
            var logPath = Path.Combine(root, "conduct-events.log");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal($"Record {expectedToken} evidence outcome");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                launchOwnedProcess: expectedOutcome == ConductorEvidenceAttemptOutcome.LaunchFailed
                    ? _ => throw new InvalidOperationException("launch failed")
                    : _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7110),
                acquireStableSlotLease: expectedOutcome == ConductorEvidenceAttemptOutcome.CorruptArtifacts
                    ? (_, _) => throw new IOException("receipt write failed")
                    : (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));

            var decision = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            if (expectedOutcome != ConductorEvidenceAttemptOutcome.LaunchFailed)
            {
                coordinator.RunAttemptForTests(
                    decision.Attempt,
                    candidate,
                    ConductorAutonomyPolicy.Permissive,
                    (_, _, _, _) => RunResultForOutcome(expectedOutcome, candidate));
            }

            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                item.GetProperty("attempt").GetString() == decision.Attempt.AttemptId));
            Assert.Equal(expectedToken, end.GetProperty("outcome").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, "run focused tests", "candidate_changed")]
    [InlineData(false, "run different focused tests", "focused_request_changed")]
    public void ReplacingCompletedAttempt_EmitsTypedMismatchCause(
        bool changeCandidate,
        string replacementRequest,
        string expectedCause)
    {
        var root = CreateTempDirectory();
        try
        {
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Record focused evidence replacement cause");
            var firstCandidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-1", "main-1");
            var firstCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7111),
                acquireStableSlotLease: (_, _) => null);
            var first = firstCoordinator.EvaluateFocusedEvidence(
                firstCandidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            firstCoordinator.RunAttemptForTests(
                first.Attempt,
                firstCandidate,
                ConductorAutonomyPolicy.Permissive,
                (attemptCandidate, _, _, _) => ConductorParallelAcceptanceRunResult.Focused(
                    attemptCandidate,
                    PassingEvidence(attemptCandidate.Goal, "run focused tests", null, CancellationToken.None)));

            var replacementCandidate = changeCandidate
                ? ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch-2", "main-1")
                : firstCandidate;
            var logPath = Path.Combine(root, "conduct-events.log");
            var replacementCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7112),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));
            var replacement = replacementCoordinator.EvaluateFocusedEvidence(
                replacementCandidate,
                ConductorAutonomyPolicy.Permissive,
                replacementRequest,
                PassingEvidence);

            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                item.GetProperty("attempt").GetString() == first.Attempt.AttemptId));
            Assert.Equal("superseded", end.GetProperty("outcome").GetString());
            Assert.Equal(expectedCause, end.GetProperty("cause").GetString());
            Assert.Equal(replacement.Attempt.AttemptId, end.GetProperty("superseded_by").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HeldReason_RefreshesElapsedWithoutChangingHoldIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Render focused evidence progress");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103),
                acquireStableSlotLease: (_, _) => null,
                timeProvider: clock);
            var started = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);

            var firstReason = coordinator.DescribeFocusedEvidenceHold(started.Attempt);
            clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(41));
            var secondReason = coordinator.DescribeFocusedEvidenceHold(started.Attempt);
            var stableIdentity = $"pre-review-evidence:{started.Attempt.AttemptId}";
            var firstHold = kernel.ObserveGoalHold(
                goal.Id,
                "AwaitingReview",
                firstReason,
                clock.GetUtcNow() - TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(10),
                stableIdentity);
            var secondHold = kernel.ObserveGoalHold(
                goal.Id,
                "AwaitingReview",
                secondReason,
                clock.GetUtcNow(),
                TimeSpan.FromMinutes(10),
                stableIdentity);

            Assert.Contains("attempt 1, 0m0s elapsed", firstReason, StringComparison.Ordinal);
            Assert.Contains("attempt 1, 9m41s elapsed", secondReason, StringComparison.Ordinal);
            Assert.NotEqual(firstReason, secondReason);
            Assert.Equal(firstHold.Hold.Identity, secondHold.Hold.Identity);
            Assert.False(secondHold.StateChanged);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompletedRun_RecordsNumericExecutedTestCountIncludingZero()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var trxPath = Path.Combine(root, "zero.trx");
            File.WriteAllText(trxPath, "<TestRun><ResultSummary><Counters total=\"0\" executed=\"0\" passed=\"0\" failed=\"0\" /></ResultSummary></TestRun>");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Count focused evidence tests");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));

            var completed = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                (_, request, _, _) => new FocusedEvidenceRunResult(
                    request,
                    Accepted: true,
                    Passed: true,
                    Summary: "1 check(s) passed; mode=focused reason=explicit-focused-mapping; receipts: zero.trx",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "zero-test receipt",
                            Passed: true,
                            ExitCode: 0,
                            OutputTail: null,
                            TestResultPaths: [trxPath])
                    ]));

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END"));
            Assert.Equal("passed", end.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Number, end.GetProperty("tests_executed").ValueKind);
            Assert.Equal(0, end.GetProperty("tests_executed").GetInt64());
            Assert.Equal(
                "1 check(s) passed; mode=focused reason=explicit-focused-mapping; receipts: zero.trx",
                end.GetProperty("detail").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeadOwnerWithoutTerminalArtifacts_RecordsTypedUnknownOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 7, 13, 0, 0, TimeSpan.Zero));
            var logPath = Path.Combine(root, "conduct-events.log");
            var writer = new ConductEventLogWriter(logPath);
            var attemptRoot = Path.Combine(root, "attempts");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Record an indeterminate focused evidence outcome");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7104),
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer,
                timeProvider: clock);

            var started = startingCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);
            clock.Advance(TimeSpan.FromSeconds(5));

            var recoveringCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7105),
                recentHeartbeatGrace: TimeSpan.Zero,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: writer,
                timeProvider: clock);
            var terminal = recoveringCoordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                PassingEvidence);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, terminal.Kind);
            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END" &&
                item.GetProperty("attempt").GetString() == started.Attempt.AttemptId));
            Assert.Equal("unknown", end.GetProperty("outcome").GetString());
            Assert.Equal("unknown", end.GetProperty("tests_executed").GetString());
            Assert.Equal(JsonValueKind.Number, end.GetProperty("duration_s").ValueKind);
            Assert.True(end.GetProperty("duration_s").GetDouble() > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompletedRun_SumsPassedAndFailedAcrossDistinctTrxReceipts()
    {
        var root = CreateTempDirectory();
        try
        {
            var logPath = Path.Combine(root, "conduct-events.log");
            var firstTrxPath = Path.Combine(root, "first.trx");
            var secondTrxPath = Path.Combine(root, "second.trx");
            File.WriteAllText(firstTrxPath, TrxCounters(passed: 2, failed: 1, notExecuted: 4));
            File.WriteAllText(secondTrxPath, TrxCounters(passed: 3, failed: 2, notExecuted: 5));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Aggregate focused evidence test receipts");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(logPath));

            var completed = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                (_, request, _, _) => new FocusedEvidenceRunResult(
                    request,
                    Accepted: true,
                    Passed: true,
                    Summary: "passed",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "multi-receipt count",
                            Passed: true,
                            ExitCode: 0,
                            OutputTail: null,
                            TestResultPaths: [firstTrxPath, secondTrxPath, firstTrxPath])
                    ]));

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, completed.Kind);
            var end = Assert.Single(ReadEvents(logPath).Where(item =>
                item.GetProperty("eventKind").GetString() == "EVIDENCE_END"));
            Assert.Equal(JsonValueKind.Number, end.GetProperty("tests_executed").ValueKind);
            Assert.Equal(8, end.GetProperty("tests_executed").GetInt64());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FocusedEvidenceRunResult PassingEvidence(
        Goal _,
        string request,
        DotnetBuildEnvironmentLease? __,
        CancellationToken ___) =>
        new(request, Accepted: true, Passed: true, Summary: "passed", Checks: []);

    private static ConductorParallelAcceptanceRunResult RunResultForOutcome(
        ConductorEvidenceAttemptOutcome outcome,
        ConductorParallelAcceptanceCandidate candidate) =>
        outcome switch
        {
            ConductorEvidenceAttemptOutcome.Failed => ConductorParallelAcceptanceRunResult.Focused(
                candidate,
                new FocusedEvidenceRunResult("run focused tests", Accepted: true, Passed: false, "failed", [])),
            ConductorEvidenceAttemptOutcome.Faulted => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new InvalidOperationException("evidence faulted")),
            ConductorEvidenceAttemptOutcome.Cancelled => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new OperationCanceledException("evidence cancelled")),
            ConductorEvidenceAttemptOutcome.BlockedBuildSlot => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy("evidence", []))),
            ConductorEvidenceAttemptOutcome.BlockedBuildLock => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new BuildLockBlockedException(new BuildLockAttribution("locked.dll", [], "test"))),
            ConductorEvidenceAttemptOutcome.InfrastructureDeferred => ConductorParallelAcceptanceRunResult.Fault(
                candidate,
                new AcceptanceInfrastructureDeferredException(
                    "trusted-main-build-failed",
                    1,
                    "baseline assembly unavailable")),
            ConductorEvidenceAttemptOutcome.CorruptArtifacts => ConductorParallelAcceptanceRunResult.Focused(
                candidate,
                new FocusedEvidenceRunResult("run focused tests", Accepted: true, Passed: true, "unused", [])),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Outcome requires a dedicated lifecycle test.")
        };

    private static List<JsonElement> ReadEvents(string path) =>
        File.ReadLines(path)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private static string TrxCounters(int passed, int failed, int notExecuted) =>
        $"<TestRun><ResultSummary><Counters total=\"{passed + failed + notExecuted}\" " +
        $"executed=\"{passed + failed}\" passed=\"{passed}\" failed=\"{failed}\" " +
        $"notExecuted=\"{notExecuted}\" /></ResultSummary></TestRun>";

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-evidence-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            _utcNow += elapsed;
        }
    }
}
