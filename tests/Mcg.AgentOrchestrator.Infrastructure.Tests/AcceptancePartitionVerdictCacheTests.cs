using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionVerdictCacheTests : IDisposable
{
    private static readonly GoalId GoalId = new("12345678123456781234567812345678");
    private readonly string _root = CreateTempDirectory();
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: Cache",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AcceptancePartitionVerdictCacheTests"]
    };

    [Fact]
    public void Reuse_MatchingPairKeyWithGreenJournalRecord_ReusesSourceAttemptVerdict()
    {
        var first = CreateCache("attempt-one");
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two");
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(_partition));

        Assert.True(reused.Passed);
        Assert.Equal("attempt-one", reused.TestResultAttemptId);
        Assert.True(reused.TestResultIsExplicitCrossAttemptReuse);
        Assert.Contains("source_attempt_id=attempt-one", reused.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Reuse_ChangedCandidateTreeSha_ForcesRerun()
    {
        var first = CreateCache("attempt-one", candidateTreeSha: "tree-a");
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two", candidateTreeSha: "tree-b");

        Assert.Null(second.TryReuse(_partition));
    }

    [Fact]
    public void Backstop_AtConfiguredInterval_ForcesFullRerunAndResetsReuseCounter()
    {
        var first = CreateCache("attempt-one", fullRerunEveryN: 2);
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two", fullRerunEveryN: 2);
        Assert.NotNull(second.TryReuse(_partition));
        Assert.NotNull(second.CompleteAttempt());

        var third = CreateCache("attempt-three", fullRerunEveryN: 2);
        Assert.True(third.ForceFullRerun);
        Assert.Null(third.TryReuse(_partition));
        third.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(third.CompleteAttempt());

        var fourth = CreateCache("attempt-four", fullRerunEveryN: 2);
        Assert.False(fourth.ForceFullRerun);
        Assert.NotNull(fourth.TryReuse(_partition));
    }

    [Fact]
    public void WithinAttemptRerun_GreenRerunAfterRedFirstRun_DoesNotPoisonNextAttemptVerdict()
    {
        var first = CreateCache("attempt-one", withinAttemptRerunEnabled: true);
        var firstRun = FailedResult(AcceptanceShardCompletionPredicates.NonzeroExit);
        Assert.True(first.ShouldRerunWithinAttempt(_partition, firstRun.CompletionDecision));
        first.RecordWithinAttemptRetry(
            _partition,
            firstRun,
            "attempt-one:cache:0",
            "attempt-one:cache:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));
        var verdict = first.SelectPartitionVerdict(_partition, firstRun, Result(passed: true));
        first.RecordExecution(_partition, verdict);
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two");
        Assert.Null(second.TryReuse(_partition));
    }

    [Theory]
    [InlineData(AcceptanceShardCompletionPredicates.TimedOut)]
    [InlineData(AcceptanceShardCompletionPredicates.ZeroTests)]
    [InlineData(AcceptanceShardCompletionPredicates.IncompleteExecution)]
    [InlineData(AcceptanceShardCompletionPredicates.NonzeroExit)]
    public void SelectPartitionVerdict_HardRedFirstRun_PassingProbeNeverChangesVerdict(string predicate)
    {
        var cache = CreateCache($"hard-red-{predicate}", withinAttemptRerunEnabled: true);
        var firstRun = FailedResult(predicate);
        cache.RecordWithinAttemptRetry(
            _partition,
            firstRun,
            $"hard-red-{predicate}:cache:0",
            $"hard-red-{predicate}:cache:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));

        var verdict = cache.SelectPartitionVerdict(_partition, firstRun, Result(passed: true));
        cache.RecordExecution(_partition, verdict);
        var completion = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());

        Assert.False(verdict.Passed);
        Assert.Equal(predicate, verdict.CompletionDecision?.FailedPredicate);
        Assert.Contains("{partition_id=cache,verdict=RED}", completion.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("verdict_source=first_run", completion.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("probe_ran=true", completion.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("flake_confirmed=true", completion.ResultSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AcceptanceShardCompletionPredicates.MissingTrx)]
    [InlineData(AcceptanceShardCompletionPredicates.MalformedTrx)]
    [InlineData(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure)]
    public void SelectPartitionVerdict_ApparatusFirstRun_InvalidatesAttemptAfterProbe(string predicate)
    {
        var cache = CreateCache($"apparatus-{predicate}", withinAttemptRerunEnabled: true);
        var firstRun = FailedResult(predicate);
        cache.RecordWithinAttemptRetry(
            _partition,
            firstRun,
            $"apparatus-{predicate}:cache:0",
            $"apparatus-{predicate}:cache:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));

        var verdict = cache.SelectPartitionVerdict(_partition, firstRun, Result(passed: true));

        Assert.False(verdict.Passed);
        Assert.Equal(AcceptanceFailureClassifications.SharedGateApparatusInvalidated, verdict.FailureClassification);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, verdict.FailureCauseEvidence?.Cause);
        Assert.Equal($"apparatus-predicate:{predicate}", cache.SharedApparatusInvalidation?.FirstReceipt.ReceiptId);
        Assert.Empty(cache.SharedApparatusInvalidation?.AffectedOwners ?? []);
    }

    [Fact]
    public void Reuse_LatestJournalRecordRed_DoesNotReuseOlderGreen()
    {
        var first = CreateCache("attempt-one", fullRerunEveryN: 10);
        first.RecordExecution(_partition, Result(passed: true));
        Assert.NotNull(first.CompleteAttempt());

        var second = CreateCache("attempt-two", fullRerunEveryN: 1);
        Assert.True(second.ForceFullRerun);
        second.RecordExecution(_partition, Result(passed: false));
        Assert.NotNull(second.CompleteAttempt());

        var third = CreateCache("attempt-three", fullRerunEveryN: 10);
        Assert.Null(third.TryReuse(_partition));
    }

    [Fact]
    public void Reuse_StructuralCoverageEnforcedWithMissingTrx_ExecutesInsteadOfReusing()
    {
        var missingTrx = Path.Combine(_root, "missing.trx");
        var first = CreateCache("attempt-one", enforceStructuralCoverage: false);
        first.RecordExecution(_partition, Result(passed: true, testResultPaths: [missingTrx]));
        Assert.NotNull(first.CompleteAttempt());

        var enforced = CreateCache("attempt-two", enforceStructuralCoverage: true);
        Assert.Null(enforced.TryReuse(_partition));

        var notEnforced = CreateCache("attempt-three", enforceStructuralCoverage: false);
        Assert.NotNull(notEnforced.TryReuse(_partition));
    }

    [Fact]
    public void ShouldRerunWithinAttempt_DisabledOrNonPartitionOrPassed_ReturnsFalse()
    {
        var enabled = CreateCache("attempt-one", withinAttemptRerunEnabled: true);
        var disabled = CreateCache("attempt-two", withinAttemptRerunEnabled: false);
        var nonPartition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "not a partition",
            Type = "command",
            Command = "git",
            Arguments = ["diff", "--check"]
        };

        Assert.False(disabled.ShouldRerunWithinAttempt(_partition, passed: false));
        Assert.False(enabled.ShouldRerunWithinAttempt(nonPartition, passed: false));
        Assert.False(enabled.ShouldRerunWithinAttempt(_partition, passed: true));
    }

    [Fact]
    public void SharedApparatusInvalidation_TwoCorrelatedFailures_StopPartitionRetries()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var receipt = new TempRootApparatusLossReceiptV1(
            1,
            "shared-apparatus",
            "receipt-first",
            sharedRoot,
            [
                new(101, startedAt, TempRootJanitor.BuildOwnedRootPath(sharedRoot, 101)),
                new(202, startedAt.AddSeconds(1), TempRootJanitor.BuildOwnedRootPath(sharedRoot, 202))
            ],
            startedAt.AddMinutes(1));
        var cache = CreateCache(
            "shared-apparatus",
            withinAttemptRerunEnabled: true,
            resolveApparatusLossReceipts: () => [receipt]);
        var secondPartition = Partition("Process");

        var first = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        Assert.True(cache.ShouldRerunWithinAttempt(_partition, first.CompletionDecision));

        var second = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));

        var invalidation = Assert.IsType<AcceptanceSharedApparatusInvalidation>(cache.SharedApparatusInvalidation);
        Assert.Equal("receipt-first", invalidation.FirstReceipt.ReceiptId);
        Assert.Equal(["cache", "process"], invalidation.AffectedOwners.Select(owner => owner.PartitionId));
        Assert.Equal(AcceptanceFailureClassifications.SharedGateApparatusInvalidated, second.FailureClassification);
        Assert.False(cache.ShouldRerunWithinAttempt(secondPartition, second.CompletionDecision));
    }

    [Fact]
    public void SharedApparatusInvalidation_AbsentOrAmbiguousCorrelation_PreservesCandidateRetry()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var otherInvocation = Receipt(
            "other-attempt",
            "other-invocation",
            sharedRoot,
            startedAt,
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var cache = CreateCache(
            "this-attempt",
            resolveApparatusLossReceipts: () => [otherInvocation]);
        var secondPartition = Partition("Process");

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        var second = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));

        Assert.Null(cache.SharedApparatusInvalidation);
        Assert.Null(second.FailureCauseEvidence);
        Assert.True(cache.ShouldRerunWithinAttempt(secondPartition, second.CompletionDecision));
    }

    [Fact]
    public void SharedApparatusInvalidation_OneOwnerOrSeparateReceipts_PreservesCandidateRetry()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var receipts = new[]
        {
            Receipt("one", "attempt", sharedRoot, startedAt, (101, startedAt)),
            Receipt("two", "attempt", sharedRoot, startedAt, (202, startedAt.AddSeconds(1)))
        };
        var cache = CreateCache("attempt", resolveApparatusLossReceipts: () => receipts);
        var secondPartition = Partition("Process");

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        var second = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));

        Assert.Null(cache.SharedApparatusInvalidation);
        Assert.True(cache.ShouldRerunWithinAttempt(secondPartition, second.CompletionDecision));
    }

    [Fact]
    public void SharedApparatusInvalidation_OneTickStartMismatch_PreservesCandidateRetry()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var receipt = new TempRootApparatusLossReceiptV1(
            1,
            "attempt",
            "identity-mismatch",
            sharedRoot,
            [
                new(101, startedAt.AddTicks(-1), TempRootJanitor.BuildOwnedRootPath(sharedRoot, 101)),
                new(202, startedAt.AddSeconds(1), TempRootJanitor.BuildOwnedRootPath(sharedRoot, 202))
            ],
            startedAt.AddMinutes(1));
        var cache = CreateCache("attempt", resolveApparatusLossReceipts: () => [receipt]);
        var secondPartition = Partition("Process");

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        var second = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));

        Assert.Null(cache.SharedApparatusInvalidation);
        Assert.True(cache.ShouldRerunWithinAttempt(secondPartition, second.CompletionDecision));
    }

    [Fact]
    public void SharedApparatusInvalidation_FirstReceiptAndExactOwners_ArePreserved()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var first = Receipt(
            "first",
            "attempt",
            sharedRoot,
            startedAt,
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var later = Receipt(
            "later",
            "attempt",
            sharedRoot,
            startedAt.AddMinutes(2),
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var cache = CreateCache("attempt", resolveApparatusLossReceipts: () => [first, later]);
        var secondPartition = Partition("Process");

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        _ = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));
        var decorated = cache.ApplySharedApparatusInvalidation(
            [
                FailedOwnerResult(_partition.Name, 101, startedAt),
                FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)),
                new AcceptanceCheckResult("unrelated candidate failure", false, 1, "ordinary failure")
            ]);

        var invalidation = Assert.IsType<AcceptanceSharedApparatusInvalidation>(cache.SharedApparatusInvalidation);
        Assert.Equal("first", invalidation.FirstReceipt.ReceiptId);
        Assert.Equal([101, 202], invalidation.AffectedOwners.Select(owner => owner.OwnerProcessId));
        Assert.Equal(2, decorated.Count);
        Assert.Single(
            decorated,
            result => result.FailureClassification == AcceptanceFailureClassifications.SharedGateApparatusInvalidated);
        var unrelated = Assert.Single(decorated, result => result.Name == "unrelated candidate failure");
        Assert.Null(unrelated.FailureClassification);
    }

    [Fact]
    public void SharedApparatusInvalidation_LatchedReceiptAccumulatesLaterCorrelatedOwner()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var firstReceipt = Receipt(
            "first",
            "attempt",
            sharedRoot,
            startedAt,
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var laterReceipt = Receipt(
            "later",
            "attempt",
            sharedRoot,
            startedAt.AddSeconds(2),
            (202, startedAt.AddSeconds(1)),
            (303, startedAt.AddSeconds(2)));
        var cache = CreateCache("attempt", resolveApparatusLossReceipts: () => [firstReceipt, laterReceipt]);
        var secondPartition = Partition("Process");
        var thirdPartition = Partition("Dispatch");

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        _ = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));
        var third = cache.ObserveSharedApparatusEvidence(
            thirdPartition,
            FailedOwnerResult(thirdPartition.Name, 303, startedAt.AddSeconds(2)));

        var invalidation = Assert.IsType<AcceptanceSharedApparatusInvalidation>(cache.SharedApparatusInvalidation);
        Assert.Equal("first", invalidation.FirstReceipt.ReceiptId);
        Assert.Equal(
            ["cache", "process", "dispatch"],
            invalidation.AffectedOwners.Select(owner => owner.PartitionId));
        Assert.Equal(AcceptanceFailureClassifications.SharedGateApparatusInvalidated, third.FailureClassification);
    }

    [Fact]
    public void SharedApparatusInvalidation_CompleteAttemptJournalsFirstReceiptAndExactOwnerIdentities()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var receipt = Receipt(
            "durable-first",
            "durable-attempt",
            sharedRoot,
            startedAt,
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var cache = CreateCache(
            "durable-attempt",
            resolveApparatusLossReceipts: () => [receipt]);
        var secondPartition = Partition("Process");
        var firstResult = FailedOwnerResult(_partition.Name, 101, startedAt);
        var secondResult = FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1));

        _ = cache.ObserveSharedApparatusEvidence(_partition, firstResult);
        _ = cache.ObserveSharedApparatusEvidence(secondPartition, secondResult);
        cache.RecordExecution(_partition, firstResult);
        cache.RecordExecution(secondPartition, secondResult);
        Assert.NotNull(cache.CompleteAttempt());

        var entryLine = Assert.Single(
            SharedJsonlFile.ReadAllLines(cache.JournalPath),
            line => line.Contains("acceptance:shared-apparatus-invalidated", StringComparison.Ordinal));
        using var entry = System.Text.Json.JsonDocument.Parse(entryLine);
        var invalidation = entry.RootElement.GetProperty("sharedApparatusInvalidation");
        Assert.Equal(
            "durable-first",
            invalidation.GetProperty("firstReceipt").GetProperty("receiptId").GetString());
        var owners = invalidation.GetProperty("affectedOwners").EnumerateArray().ToArray();
        Assert.Equal(2, owners.Length);
        Assert.Equal([101, 202], owners.Select(owner => owner.GetProperty("ownerProcessId").GetInt32()));
        Assert.Equal(
            [
                TempRootJanitor.BuildOwnedRootPath(sharedRoot, 101),
                TempRootJanitor.BuildOwnedRootPath(sharedRoot, 202)
            ],
            owners.Select(owner => owner.GetProperty("ownedRootPath").GetString()));
    }

    [Fact]
    public void SharedApparatusInvalidation_OriginalIdentitySurvivesRerunObservation()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var receipt = Receipt(
            "rerun-first",
            "rerun-attempt",
            sharedRoot,
            startedAt,
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var cache = CreateCache(
            "rerun-attempt",
            resolveApparatusLossReceipts: () => [receipt]);
        var secondPartition = Partition("Process");
        var rerunStartedAt = startedAt.AddSeconds(10);

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        var rerun = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 909, rerunStartedAt));
        _ = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));

        var collapsed = cache.ApplySharedApparatusInvalidation([rerun]);
        var invalidated = Assert.Single(collapsed);
        Assert.Equal(AcceptanceFailureClassifications.SharedGateApparatusInvalidated, invalidated.FailureClassification);
        Assert.Contains("receipt_id=rerun-first", invalidated.FailureCauseEvidence?.Evidence, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Shared apparatus invalidation passing rerun does not consume incident")]
    public void SharedApparatusInvalidation_PassingRerunDoesNotConsumeIncident()
    {
        var startedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var sharedRoot = Path.Combine(_root, "owned-shared-root");
        var receipt = Receipt(
            "rerun-pass",
            "rerun-pass-attempt",
            sharedRoot,
            startedAt,
            (101, startedAt),
            (202, startedAt.AddSeconds(1)));
        var cache = CreateCache(
            "rerun-pass-attempt",
            resolveApparatusLossReceipts: () => [receipt]);
        var secondPartition = Partition("Process");

        _ = cache.ObserveSharedApparatusEvidence(
            _partition,
            FailedOwnerResult(_partition.Name, 101, startedAt));
        var affectedFailure = cache.ObserveSharedApparatusEvidence(
            secondPartition,
            FailedOwnerResult(secondPartition.Name, 202, startedAt.AddSeconds(1)));
        var passingRerun = new AcceptanceCheckResult(
            _partition.Name,
            true,
            0,
            null,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                true,
                null,
                false,
                1,
                1,
                1,
                "passed"),
            ChildProcessId: 909,
            ChildProcessStartedAt: startedAt.AddSeconds(10));

        var collapsed = cache.ApplySharedApparatusInvalidation(
            [passingRerun, affectedFailure, new AcceptanceCheckResult("unrelated pass", true, 0, null)]);

        var invalidated = Assert.Single(
            collapsed,
            result => result.FailureClassification == AcceptanceFailureClassifications.SharedGateApparatusInvalidated);
        Assert.False(invalidated.Passed);
        Assert.Equal(secondPartition.Name, invalidated.Name);
        Assert.Contains(collapsed, result => result.Name == _partition.Name && result.Passed);
        Assert.Contains(collapsed, result => result.Name == "unrelated pass" && result.Passed);
    }

    [Theory]
    [InlineData(true, -1, 1, 1, "passed", null, null, AcceptanceShardCompletionPredicates.TimedOut, true)]
    [InlineData(false, 1, 1, 1, "passed", null, null, AcceptanceShardCompletionPredicates.NonzeroExit, true)]
    [InlineData(false, 0, null, null, "missing", AcceptanceShardCompletionPredicates.MissingTrx, null, AcceptanceShardCompletionPredicates.MissingTrx, true)]
    [InlineData(false, 0, null, null, "malformed", AcceptanceShardCompletionPredicates.MalformedTrx, null, AcceptanceShardCompletionPredicates.MalformedTrx, true)]
    [InlineData(false, 0, 0, 0, "passed", null, null, AcceptanceShardCompletionPredicates.ZeroTests, true)]
    [InlineData(false, 0, 2, 1, "passed", null, null, AcceptanceShardCompletionPredicates.IncompleteExecution, true)]
    [InlineData(false, 1, 1, 1, "failed", null, null, AcceptanceShardCompletionPredicates.FailingTrx, false)]
    [InlineData(false, 0, 1, 1, "passed", null, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, true)]
    [InlineData(false, 0, 1, 1, "passed", null, AcceptanceFailureClassifications.StructuralCoverageFailed, AcceptanceFailureClassifications.StructuralCoverageFailed, false)]
    [InlineData(false, 0, 1, 1, "passed", null, AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline, AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline, false)]
    public void CompletionDecision_FailedPredicate_RerunsOnlyIndeterminateInfrastructureFailure(
        bool timedOut,
        int exitCode,
        int? discovered,
        int? executed,
        string trxOutcome,
        string? trxFailedPredicate,
        string? policyFailure,
        string expectedPredicate,
        bool expectedRerun)
    {
        var cache = CreateCache("typed-retry", withinAttemptRerunEnabled: true);
        var decision = GoalAcceptanceVerifier.DecideTestShardCompletionForTests(
            new GoalAcceptanceVerifier.CommandResult(exitCode, string.Empty, TimedOut: timedOut),
            discovered,
            executed,
            trxOutcome,
            trxFailedPredicate,
            policyFailure);

        Assert.False(decision.Passed);
        Assert.Equal(expectedPredicate, decision.FailedPredicate);
        Assert.Equal(expectedRerun, cache.ShouldRerunWithinAttempt(_partition, decision));
    }

    [Fact]
    public void CompletionDecision_CompletedTrxCountsSkippedTestsWithoutHidingMissingExecution()
    {
        var completePath = Path.Combine(_root, "completed-with-skip.trx");
        File.WriteAllText(
            completePath,
            "<TestRun><Results><UnitTestResult outcome=\"Passed\"/><UnitTestResult outcome=\"NotExecuted\"/></Results>" +
            "<ResultSummary outcome=\"Completed\"><Counters total=\"2\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"1\"/></ResultSummary></TestRun>");
        var complete = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(0, string.Empty),
            [completePath]);

        Assert.True(complete.Passed);
        Assert.Equal("Completed", complete.TrxOutcome);
        Assert.Equal(2, complete.DiscoveredTestCount);
        Assert.Equal(1, complete.ExecutedTestCount);
        Assert.Equal(1, complete.NotExecutedTestCount);

        var incompletePath = Path.Combine(_root, "completed-but-incomplete.trx");
        File.WriteAllText(
            incompletePath,
            "<TestRun><Results><UnitTestResult outcome=\"Passed\"/></Results>" +
            "<ResultSummary outcome=\"Completed\"><Counters total=\"2\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\"/></ResultSummary></TestRun>");
        var incomplete = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(0, string.Empty),
            [incompletePath]);

        Assert.False(incomplete.Passed);
        Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteExecution, incomplete.FailedPredicate);
    }

    [Fact]
    public void WithinAttemptRetryReceipt_RetainsCapturedStderrAndTypedDecision()
    {
        const string stderr = "retry-driving stderr from the original process";
        var retained = AcceptanceAttemptArtifactCustody.RetainRetryDiagnostic(
            Path.Combine(_root, "attempt", "acceptance"),
            fallbackArtifactsPath: null,
            "cache-run-0",
            Path.Combine(_root, "deleted-temp.err"),
            stderr,
            "{\"fallback\":true}");
        var retainedBytes = File.ReadAllBytes(retained.Path);
        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(retainedBytes));
        Assert.EndsWith(".err", retained.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stderr, File.ReadAllText(retained.Path));
        Assert.Equal(expectedHash, retained.Sha256);

        var cache = CreateCache("receipt-attempt", withinAttemptRerunEnabled: true);
        var decision = new AcceptanceShardCompletionDecision(
            false,
            AcceptanceShardCompletionPredicates.IncompleteExecution,
            false,
            0,
            110,
            109,
            "passed",
            NotExecutedTestCount: 0);
        var original = new AcceptanceCheckResult(
            _partition.Name,
            false,
            0,
            "incomplete execution",
            CompletionDecision: decision,
            ProcessStderrPath: Path.Combine(_root, "deleted-temp.err"),
            ProcessStderr: stderr);

        cache.RecordWithinAttemptRetry(
            _partition,
            original,
            "receipt-attempt:cache:0",
            "receipt-attempt:cache:1",
            retained);

        var retryLine = SharedJsonlFile.ReadAllLines(cache.JournalPath)
            .Single(line => line.Contains("acceptance:partition-within-attempt-retry", StringComparison.Ordinal));
        using var document = System.Text.Json.JsonDocument.Parse(retryLine);
        var receipt = document.RootElement.GetProperty("partitionRetryReceipt");
        Assert.Equal(AcceptanceShardCompletionPredicates.IncompleteExecution, receipt.GetProperty("failedPredicate").GetString());
        Assert.Equal("receipt-attempt:cache:0", receipt.GetProperty("originalInvocationId").GetString());
        Assert.Equal("receipt-attempt:cache:1", receipt.GetProperty("retryInvocationId").GetString());
        Assert.Equal(0, receipt.GetProperty("exitCode").GetInt32());
        Assert.Equal(110, receipt.GetProperty("discoveredTestCount").GetInt32());
        Assert.Equal(109, receipt.GetProperty("executedTestCount").GetInt32());
        Assert.Equal("passed", receipt.GetProperty("trxOutcome").GetString());
        Assert.Equal(0, receipt.GetProperty("notExecutedTestCount").GetInt32());
        Assert.Equal(retained.Path, receipt.GetProperty("diagnosticPath").GetString());
        Assert.Equal(expectedHash, receipt.GetProperty("diagnosticSha256").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AcceptancePartitionVerdictCache CreateCache(
        string attemptId,
        string candidateTreeSha = "tree-a",
        string mainSha = "main-a",
        int fullRerunEveryN = 5,
        bool withinAttemptRerunEnabled = true,
        bool enforceStructuralCoverage = false,
        Func<IReadOnlyList<TempRootApparatusLossReceiptV1>>? resolveApparatusLossReceipts = null)
    {
        var cache = AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                GoalId,
                _root,
                [_partition],
                fullRerunEveryN,
                withinAttemptRerunEnabled,
                _ => candidateTreeSha,
                _ => mainSha,
                _ => $"commit-{attemptId}",
                () => attemptId,
                () => "manifest-a",
                () => enforceStructuralCoverage,
                resolveApparatusLossReceipts));
        return Assert.IsType<AcceptancePartitionVerdictCache>(cache);
    }

    private AcceptanceCheckResult Result(
        bool passed,
        IReadOnlyList<string>? testResultPaths = null) =>
        new(
            _partition.Name,
            passed,
            passed ? 0 : 1,
            passed ? null : "failed",
            TestResultPaths: testResultPaths ?? []);

    private AcceptanceCheckResult FailedResult(string predicate) =>
        new(
            _partition.Name,
            false,
            predicate == AcceptanceShardCompletionPredicates.NonzeroExit ? 1 : 0,
            "first run failed",
            FailureClassification: predicate,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false,
                predicate,
                predicate == AcceptanceShardCompletionPredicates.TimedOut,
                predicate == AcceptanceShardCompletionPredicates.NonzeroExit ? 1 : 0,
                predicate == AcceptanceShardCompletionPredicates.ZeroTests ? 0 : 1,
                predicate == AcceptanceShardCompletionPredicates.IncompleteExecution ? 0 : 1,
                predicate is AcceptanceShardCompletionPredicates.MissingTrx ? "missing" :
                    predicate is AcceptanceShardCompletionPredicates.MalformedTrx ? "malformed" : "passed"));

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck Partition(string id) => new()
    {
        Name = $"infrastructure tests: {id}",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", $"FullyQualifiedName~{id}"]
    };

    private static AcceptanceCheckResult FailedOwnerResult(
        string name,
        int processId,
        DateTimeOffset startedAt) =>
        new(
            name,
            false,
            1,
            "ordinary shard failure",
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false,
                AcceptanceShardCompletionPredicates.NonzeroExit,
                false,
                1,
                1,
                1,
                "failed"),
            ChildProcessId: processId,
            ChildProcessStartedAt: startedAt);

    private static TempRootApparatusLossReceiptV1 Receipt(
        string receiptId,
        string attemptId,
        string sharedRoot,
        DateTimeOffset recordedAt,
        params (int ProcessId, DateTimeOffset StartedAt)[] owners) =>
        new(
            1,
            attemptId,
            receiptId,
            sharedRoot,
            owners.Select(owner => new TempRootApparatusDestroyedOwner(
                owner.ProcessId,
                owner.StartedAt,
                TempRootJanitor.BuildOwnedRootPath(sharedRoot, owner.ProcessId))).ToArray(),
            recordedAt);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-partition-verdict-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
