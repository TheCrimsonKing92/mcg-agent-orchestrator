using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its filesystem root; no process or environment state is shared.
public sealed class AcceptanceTimedOutProbeEvidenceTests : IDisposable
{
    private readonly string _root = CreateTempDirectory();
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: Remainder",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~Remainder"]
    };
    private static readonly string[] Failures =
    [
        "TaskCompletionBuildEvidencePersistenceTests.StaleWaitingSnapshot_PendingChildStored_PollsWithoutAnotherBuild",
        "CompletionBuildEvidenceIdentityTests.InvalidTerminalReceiptCannotResumeCompletion"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectVerdict_TimedOutWithWrittenProbe_KeepsRedAndRecordsEvidence(bool passed)
    {
        var cache = CreateCache();
        var first = FirstRun();
        var probe = Probe(passed, writeTrx: true);
        Assert.True(cache.ShouldRerunWithinAttempt(_partition, first.CompletionDecision));
        cache.RecordWithinAttemptRetry(_partition, first, "attempt:remainder:0", "attempt:remainder:1",
            new AcceptanceRetainedDiagnostic("first.err", "diagnostic-sha"));

        var verdict = cache.SelectPartitionVerdict(_partition, first, probe);

        Assert.False(verdict.Passed);
        Assert.Same(first.CompletionDecision, verdict.CompletionDecision);
        Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, verdict.CompletionDecision?.FailedPredicate);
        Assert.Equal(first.FailureClassification, verdict.FailureClassification);
        Assert.Equal(first.TestResultPaths, verdict.TestResultPaths);
        Assert.Equal(first.FailingTestIdentities, verdict.FailingTestIdentities);
        var evidence = Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(verdict.WithinAttemptRerun);
        Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, evidence.FailedPredicate);
        Assert.Equal("remainder", evidence.PartitionId);
        Assert.Equal("attempt:remainder:0", evidence.OriginalInvocationId);
        Assert.Equal("attempt:remainder:1", evidence.RetryInvocationId);
        Assert.Equal(passed, evidence.RerunPassed);
        Assert.Equal(passed ? 9822 : 9962, evidence.ExecutedTestCount);
        Assert.Equal(probe.TestResultPaths, evidence.RerunTestResultPaths);
        Assert.Equal(passed ? [] : Failures, evidence.RerunFailingTestIdentities);
        Assert.False(WithinAttemptRerunApparatusEvidence.IsExplainedIdentitylessCheck(verdict));

        cache.RecordExecution(_partition, verdict);
        var completion = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());
        Assert.Contains("{partition_id=remainder,verdict=RED}", completion.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("verdict_source=first_run", completion.ResultSummary, StringComparison.Ordinal);
        var journal = GoalOperationJournal.ReadStrict(_root, new GoalId("12345678123456781234567812345678"));
        var detail = Assert.Single(journal.Entries,
            entry => entry.Operation == "acceptance:partition-within-attempt-probe").Detail;
        Assert.Contains("first_trx=none rerun_trx_recorded=true rerun_passed=" + passed.ToString().ToLowerInvariant(),
            detail, StringComparison.Ordinal);
        Assert.DoesNotContain("apparatus_reason=", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectVerdict_ProbeWithoutUsableTrx_AttachesNoEvidence(bool hasPath)
    {
        var cache = CreateCache();
        var first = FirstRun();
        var probe = Probe(true, writeTrx: false);
        if (!hasPath) probe = probe with { TestResultPaths = [] };
        cache.RecordWithinAttemptRetry(_partition, first, "attempt:remainder:0", "attempt:remainder:1",
            new AcceptanceRetainedDiagnostic("first.err", "diagnostic-sha"));

        var verdict = cache.SelectPartitionVerdict(_partition, first, probe);

        Assert.Same(first, verdict);
        Assert.Null(verdict.WithinAttemptRerun);
        cache.RecordExecution(_partition, verdict);
        Assert.NotNull(cache.CompleteAttempt());
        var journal = GoalOperationJournal.ReadStrict(_root, new GoalId("12345678123456781234567812345678"));
        var detail = Assert.Single(journal.Entries,
            entry => entry.Operation == "acceptance:partition-within-attempt-probe").Detail;
        Assert.DoesNotContain("rerun_trx_recorded", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuild_NonTimeoutAndUnknownCount_UsesOnlyTimeoutEvidence()
    {
        var first = FirstRun();
        var probe = Probe(false, writeTrx: true) with { CompletionDecision = null, ExecutedTestCount = null };
        var evidence = Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(
            AcceptanceTimedOutProbeEvidence.TryBuild("remainder", first, probe, "first", "retry"));
        Assert.Equal(0, evidence.ExecutedTestCount);
        Assert.Equal(Failures, evidence.RerunFailingTestIdentities);
        Assert.Null(AcceptanceTimedOutProbeEvidence.TryBuild("remainder", first with
        {
            CompletionDecision = first.CompletionDecision! with
            { FailedPredicate = AcceptanceShardCompletionPredicates.NonzeroExit }
        }, probe, "first", "retry"));
    }

    [Fact]
    public void Resolve_TimeoutEvidence_ReplacesOnlyItsMissingPathsAndReturnsNote()
    {
        var first = FirstRun();
        var probe = Probe(false, writeTrx: true);
        var check = first with
        {
            WithinAttemptRerun = AcceptanceTimedOutProbeEvidence.TryBuild("remainder", first, probe, "first", "retry")
        };
        var unrelatedMissing = Path.Combine(_root, "unrelated.trx");
        var verification = Verification([check, new AcceptanceCheckResult("other lane", false, 1, null,
            TestResultPaths: [unrelatedMissing])], [first.TestResultPaths![0], unrelatedMissing]);

        var resolved = GroupedReceiptTestResultPaths.Resolve(verification,
            AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(verification.TestResultPaths));

        Assert.Equal(AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(
            [probe.TestResultPaths![0], unrelatedMissing]), resolved.Paths);
        Assert.DoesNotContain(first.TestResultPaths[0], resolved.Paths);
        Assert.Contains(unrelatedMissing, resolved.Paths);
        var note = Assert.Single(resolved.Notes);
        Assert.Equal(check.Name, note.Lane);
        Assert.Equal(probe.TestResultPaths, note.RerunPaths);
        Assert.False(note.RerunPassed);
    }

    [Theory]
    [InlineData("none")]
    [InlineData(AcceptanceShardCompletionPredicates.MissingTrx)]
    [InlineData(AcceptanceShardCompletionPredicates.MalformedTrx)]
    [InlineData("missing-rerun")]
    public void Resolve_WithoutUsableTimeoutEvidence_KeepsPaths(string kind)
    {
        var first = FirstRun();
        var probe = Probe(true, writeTrx: kind != "missing-rerun");
        var check = first with
        {
            WithinAttemptRerun = kind == "none" ? null : new AcceptanceWithinAttemptRerunEvidence(
                "remainder", kind == "missing-rerun" ? AcceptanceShardCompletionPredicates.TimedOut : kind,
                "first", "retry", 9822, probe.TestResultPaths!)
        };
        var verification = Verification([check], first.TestResultPaths!);

        var resolved = GroupedReceiptTestResultPaths.Resolve(verification, first.TestResultPaths!);

        Assert.Equal(first.TestResultPaths, resolved.Paths);
        Assert.Empty(resolved.Notes);
    }

    [Fact]
    public void Resolve_ExistingFirstTrx_AddsRerunWithoutSubstitutionNote()
    {
        var first = FirstRun();
        File.WriteAllText(first.TestResultPaths![0], "existing first-run evidence");
        var probe = Probe(true, writeTrx: true);
        var check = first with
        {
            WithinAttemptRerun = AcceptanceTimedOutProbeEvidence.TryBuild("remainder", first, probe, "first", "retry")
        };

        var resolved = GroupedReceiptTestResultPaths.Resolve(
            Verification([check], first.TestResultPaths), first.TestResultPaths);

        Assert.Equal(2, resolved.Paths.Count);
        Assert.Contains(first.TestResultPaths[0], resolved.Paths);
        Assert.Contains(probe.TestResultPaths![0], resolved.Paths);
        Assert.Empty(resolved.Notes);
    }

    private AcceptancePartitionVerdictCache CreateCache() => Assert.IsType<AcceptancePartitionVerdictCache>(
        AcceptancePartitionVerdictCache.Create(new AcceptancePartitionVerdictCacheOptions(
            new GoalId("12345678123456781234567812345678"), _root, [_partition], 5, true,
            _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest", () => false)));

    private AcceptanceCheckResult FirstRun() => new(
        _partition.Name, false, -1, "first run timed out", TestResultPaths: [Path.Combine(_root, "first.trx")],
        FailingTestIdentities: [], FailureClassification: AcceptanceShardCompletionPredicates.TimedOut,
        CompletionDecision: new AcceptanceShardCompletionDecision(
            false, AcceptanceShardCompletionPredicates.TimedOut, true, -1, null, null, "missing"));

    private AcceptanceCheckResult Probe(bool passed, bool writeTrx)
    {
        var path = Path.Combine(_root, "rerun.trx");
        var executed = passed ? 9822 : 9962;
        if (writeTrx)
            File.WriteAllText(path, $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    {(passed ? "" : string.Join("", Failures.Select((name, index) => $"<UnitTestResult testId=\"{index}\" testName=\"{name}\" outcome=\"Failed\" />")))}
                  </Results>
                  <ResultSummary outcome="{(passed ? "Passed" : "Failed")}">
                    <Counters total="{executed}" executed="{executed}" passed="{executed - (passed ? 0 : 2)}" failed="{(passed ? 0 : 2)}" />
                  </ResultSummary>
                </TestRun>
                """);
        return new AcceptanceCheckResult(_partition.Name, passed, passed ? 0 : 1, null,
            TestResultPaths: [path], FailingTestIdentities: passed ? [] : [Failures[0], " ", Failures[1]],
            ExecutedTestCount: executed,
            CompletionDecision: new AcceptanceShardCompletionDecision(passed,
                passed ? null : AcceptanceShardCompletionPredicates.FailingTrx, false, passed ? 0 : 1,
                executed, executed, "parsed"));
    }

    private static AcceptanceVerificationResult Verification(
        IReadOnlyList<AcceptanceCheckResult> checks, IReadOnlyList<string> paths) =>
        new(false, false, 1, "RED", Checks: checks, TestResultPaths: paths);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
