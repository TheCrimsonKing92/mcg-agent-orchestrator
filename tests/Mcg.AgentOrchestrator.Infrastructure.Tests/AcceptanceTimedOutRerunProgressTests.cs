using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its filesystem root; no child processes or shared environment state.
public sealed class AcceptanceTimedOutRerunProgressTests : IDisposable
{
    private readonly string _root = CreateTempDirectory();
    private static readonly GoalId Goal = new("12345678123456781234567812345678");
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 23, 0, 49, 51, TimeSpan.Zero);
    private readonly GoalAcceptanceVerifier.AcceptanceManifestCheck _partition = new()
    {
        Name = "infrastructure tests: Remainder",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~Remainder"]
    };

    [Fact]
    public void FinalHeartbeatKeepsLastRunningBeatAsPreStopProgress()
    {
        var heartbeatPath = Path.Combine(_root, "heartbeat.json");
        var stdoutPath = Path.Combine(_root, "stdout.log");
        var stderrPath = Path.Combine(_root, "stderr.log");
        File.WriteAllText(stdoutPath, "initial output");
        File.WriteAllText(stderrPath, "");
        var runtime = new GateHeartbeatRuntime(
            new GateHeartbeatContext(Goal.Value, "check", _partition.Name, null,
                heartbeatPath, null, "in-process fixture", null),
            Environment.ProcessId, stdoutPath, stderrPath, TimeSpan.FromSeconds(2400));
        runtime.WriteRunning(emitProgress: false);
        File.AppendAllText(stdoutPath, " more running output");
        runtime.WriteRunning(emitProgress: false);
        var running = Assert.IsType<GateHeartbeatSnapshot>(GateHeartbeatArtifacts.TryReadSnapshot(heartbeatPath));

        File.AppendAllText(stdoutPath, " output drained after stop");
        runtime.WriteFinal("timed-out", null, -1);

        var final = Assert.IsType<GateHeartbeatSnapshot>(GateHeartbeatArtifacts.TryReadSnapshot(heartbeatPath));
        Assert.Equal("timed-out", final.State);
        Assert.Equal(running.LastObservedAt, final.PreStopObservedAt);
        Assert.Equal(running.LastProgressAt, final.PreStopProgressAt);
        Assert.True(final.OutputBytes > running.OutputBytes);
    }

    [Fact]
    public void FinalHeartbeatWithoutRunningBeatHasNoPreStopProgress()
    {
        var heartbeatPath = Path.Combine(_root, "heartbeat.json");
        var runtime = new GateHeartbeatRuntime(
            new GateHeartbeatContext(Goal.Value, "check", _partition.Name, null,
                heartbeatPath, null, "in-process fixture", null),
            Environment.ProcessId, Path.Combine(_root, "stdout.log"), Path.Combine(_root, "stderr.log"),
            TimeSpan.FromSeconds(2400));

        runtime.WriteFinal("timed-out", null, -1);

        var final = Assert.IsType<GateHeartbeatSnapshot>(GateHeartbeatArtifacts.TryReadSnapshot(heartbeatPath));
        Assert.Null(final.PreStopObservedAt);
        Assert.Null(final.PreStopProgressAt);
    }

    [Theory]
    [InlineData("timed-out", 0, "growing")]
    [InlineData("timed-out", 121, "growing")]
    [InlineData("timed-out", 299, "growing")]
    [InlineData("timed-out", 300, "stalled")]
    [InlineData("timed-out", 601, "stalled")]
    [InlineData("timed-out", 1672, "stalled")]
    [InlineData("completed", 121, "unknown")]
    [InlineData("no-observed", 121, "unknown")]
    [InlineData("no-progress", 121, "unknown")]
    [InlineData("null", 121, "unknown")]
    [InlineData("missing-file", 121, "unknown")]
    [InlineData("invalid-json", 121, "unknown")]
    [InlineData("blank-path", 121, "unknown")]
    public void ClassifiesFirstRunProgressFromPreStopSilence(string kind, int silence, string expected)
    {
        AcceptanceTimedOutRunProgress progress;
        if (kind is "missing-file" or "invalid-json" or "blank-path")
        {
            var path = kind == "blank-path" ? " " : Path.Combine(_root, "heartbeat.json");
            if (kind == "invalid-json") File.WriteAllText(path, "{ invalid JSON");
            progress = AcceptanceTimedOutProbeEvidence.ClassifyFirstRunProgress(path);
        }
        else
        {
            GateHeartbeatSnapshot? snapshot = Snapshot(silence);
            snapshot = kind switch
            {
                "completed" => snapshot with { State = "completed" },
                "no-observed" => snapshot with { PreStopObservedAt = null },
                "no-progress" => snapshot with { PreStopProgressAt = null },
                "null" => null,
                _ => snapshot
            };
            progress = AcceptanceTimedOutProbeEvidence.ClassifyFirstRunProgress(snapshot);
        }

        Assert.Equal(expected, progress.State);
        Assert.Equal(expected == "unknown" ? (long?)null : silence, progress.SilenceSeconds);
    }

    [Fact]
    public void GrowingTimedOutFirstRunWithPassingRerunPassesLane()
    {
        var cache = CreateCache();
        var first = FirstRun(121);
        var probe = Probe();
        Assert.False(File.Exists(Assert.Single(first.TestResultPaths!)));
        RecordRetry(cache, first);

        var verdict = cache.SelectPartitionVerdict(_partition, first, probe);

        Assert.True(verdict.Passed);
        Assert.Same(probe.CompletionDecision, verdict.CompletionDecision);
        var evidence = Assert.IsType<AcceptanceLaneRerunEvidence>(verdict.LaneRerun);
        Assert.Equal(AcceptanceLaneRerunEvidence.Flake, evidence.Outcome);
        Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, evidence.FirstPredicate);
        Assert.Equal("attempt:remainder:0", evidence.FirstInvocationId);
        Assert.Equal("attempt:remainder:1", evidence.RerunInvocationId);
        Assert.Empty(evidence.FirstTestResultPaths);
        Assert.Empty(evidence.FirstFailingTestIdentities);
        Assert.Empty(evidence.RerunFailingTestIdentities);
        Assert.Null(evidence.RerunPredicate);
        Assert.Equal(probe.TestResultPaths, evidence.RerunTestResultPaths);
        Assert.Null(verdict.WithinAttemptRerun);
        Assert.Equal(probe.TestResultPaths, verdict.TestResultPaths);
        Assert.DoesNotContain(first.TestResultPaths![0], verdict.TestResultPaths!);
        Assert.StartsWith("lane rerun: first_run=failed(timed-out) first_trx=none first_run_progress=growing rerun=passed outcome=flake",
            verdict.ResultSummary);
        var row = Assert.Single(new AcceptanceLaneFlakeLedger(_root).Read());
        Assert.Equal(AcceptanceLaneRerunEvidence.Flake, row.Outcome);
        Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, row.FirstPredicate);
        var journal = ProbeEntry();
        Assert.Equal("GREEN", journal.PartitionVerdict);
        Assert.Contains("verdict_source=rerun", journal.Detail, StringComparison.Ordinal);
        Assert.Contains("first_run_progress=growing first_run_silence_seconds=121", journal.Detail, StringComparison.Ordinal);
        Assert.Contains("first_trx=none rerun_trx_recorded=true rerun_passed=true", journal.Detail, StringComparison.Ordinal);

        cache.RecordExecution(_partition, verdict, "lane_rerun_pass");
        var completion = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());
        Assert.Contains("{partition_id=remainder,verdict=GREEN}", completion.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("within_attempt_retries=[{partition_id=remainder,predicate=timed-out", completion.ResultSummary,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(601)]
    public void StalledTimedOutFirstRunWithPassingRerunStaysRed(int silence)
    {
        var cache = CreateCache();
        var first = FirstRun(silence);
        RecordRetry(cache, first);

        var verdict = cache.SelectPartitionVerdict(_partition, first, Probe());

        Assert.False(verdict.Passed);
        Assert.Same(first.CompletionDecision, verdict.CompletionDecision);
        Assert.Equal(first.TestResultPaths, verdict.TestResultPaths);
        Assert.Null(verdict.LaneRerun);
        Assert.True(Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(verdict.WithinAttemptRerun).RerunPassed);
        Assert.Empty(new AcceptanceLaneFlakeLedger(_root).Read());
        var journal = ProbeEntry();
        Assert.Equal("RED", journal.PartitionVerdict);
        Assert.Contains("verdict_source=first_run", journal.Detail, StringComparison.Ordinal);
        Assert.Contains($"first_run_progress=stalled first_run_silence_seconds={silence}", journal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrowingTimedOutFirstRunWithFailingRerunStaysRed()
    {
        var cache = CreateCache();
        var first = FirstRun(121);
        RecordRetry(cache, first);

        var verdict = cache.SelectPartitionVerdict(_partition, first, Probe(passed: false));

        Assert.False(verdict.Passed);
        Assert.Same(first.CompletionDecision, verdict.CompletionDecision);
        Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, verdict.CompletionDecision?.FailedPredicate);
        Assert.Null(verdict.LaneRerun);
        var evidence = Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(verdict.WithinAttemptRerun);
        Assert.False(evidence.RerunPassed);
        Assert.Contains("Ns.Cls.Fails", evidence.RerunFailingTestIdentities!);
        Assert.Empty(new AcceptanceLaneFlakeLedger(_root).Read());
        var journal = ProbeEntry();
        Assert.Equal("RED", journal.PartitionVerdict);
        Assert.Contains("verdict_source=first_run", journal.Detail, StringComparison.Ordinal);
        Assert.Contains("first_run_progress=growing", journal.Detail, StringComparison.Ordinal);
        var invoked = false;
        var returned = await new AcceptanceLaneTestFailureRerun().RunAsync(_partition, verdict, _ =>
        {
            invoked = true;
            throw new InvalidOperationException("A timeout verdict must not enter the failing-TRX rerun.");
        }, _ => "unused");
        Assert.Same(verdict, returned);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("first-failing-identity")]
    [InlineData("missing-probe-trx")]
    [InlineData("zero-executed")]
    [InlineData("completed-heartbeat")]
    [InlineData("probe-failing-identity")]
    [InlineData("no-probe-decision")]
    [InlineData("failed-probe-decision")]
    [InlineData("empty-probe-paths")]
    public void GrowingTimedOutFirstRunWithoutCleanRerunEvidenceStaysRed(string kind)
    {
        var cache = CreateCache();
        var first = FirstRun(121);
        var probe = Probe(writeTrx: kind != "missing-probe-trx");
        switch (kind)
        {
            case "first-failing-identity":
                first = first with { FailingTestIdentities = ["Ns.Cls.Fails"] };
                break;
            case "zero-executed":
                probe = probe with { ExecutedTestCount = 0,
                    CompletionDecision = probe.CompletionDecision! with { ExecutedTestCount = 0 } };
                break;
            case "completed-heartbeat":
                GateHeartbeatArtifacts.Write(first.GateHeartbeatPath!, Snapshot(121) with
                { State = "completed", PreStopObservedAt = null, PreStopProgressAt = null });
                break;
            case "probe-failing-identity":
                probe = probe with { FailingTestIdentities = ["Ns.Cls.Fails"] };
                break;
            case "no-probe-decision":
                probe = probe with { CompletionDecision = null };
                break;
            case "failed-probe-decision":
                probe = probe with { CompletionDecision = probe.CompletionDecision! with { Passed = false } };
                break;
            case "empty-probe-paths":
                probe = probe with { TestResultPaths = [] };
                break;
        }
        RecordRetry(cache, first);

        var verdict = cache.SelectPartitionVerdict(_partition, first, probe);

        Assert.False(verdict.Passed);
        Assert.Null(verdict.LaneRerun);
        Assert.Same(first.CompletionDecision, verdict.CompletionDecision);
        Assert.Equal(first.TestResultPaths, verdict.TestResultPaths);
        Assert.Empty(new AcceptanceLaneFlakeLedger(_root).Read());
        var journal = ProbeEntry();
        Assert.Equal("RED", journal.PartitionVerdict);
        Assert.Contains("verdict_source=first_run", journal.Detail, StringComparison.Ordinal);
        Assert.Contains(kind == "completed-heartbeat"
                ? "first_run_progress=unknown first_run_silence_seconds=unknown"
                : "first_run_progress=growing first_run_silence_seconds=121",
            journal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTrxFirstRunWithGrowingHeartbeatKeepsApparatusEvidence()
    {
        var cache = CreateCache();
        var first = FirstRun(121);
        first = first with { CompletionDecision = first.CompletionDecision! with
            { FailedPredicate = AcceptanceShardCompletionPredicates.MissingTrx, TimedOut = false } };
        RecordRetry(cache, first);

        var verdict = cache.SelectPartitionVerdict(_partition, first, Probe());

        Assert.False(verdict.Passed);
        Assert.Null(verdict.LaneRerun);
        Assert.Equal(AcceptanceShardCompletionPredicates.MissingTrx,
            Assert.IsType<AcceptanceWithinAttemptRerunEvidence>(verdict.WithinAttemptRerun).FailedPredicate);
        Assert.Empty(new AcceptanceLaneFlakeLedger(_root).Read());
        Assert.Contains($"apparatus_reason={AcceptanceWithinAttemptRerunEvidence.Reason}", ProbeEntry().Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain("first_run_progress=", ProbeEntry().Detail, StringComparison.Ordinal);
    }

    private AcceptancePartitionVerdictCache CreateCache() => Assert.IsType<AcceptancePartitionVerdictCache>(
        AcceptancePartitionVerdictCache.Create(new AcceptancePartitionVerdictCacheOptions(
            Goal, _root, [_partition], 5, true,
            _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest", () => false)));

    private void RecordRetry(AcceptancePartitionVerdictCache cache, AcceptanceCheckResult first)
    {
        Assert.True(cache.ShouldRerunWithinAttempt(_partition, first.CompletionDecision));
        cache.RecordWithinAttemptRetry(_partition, first, "attempt:remainder:0", "attempt:remainder:1",
            new AcceptanceRetainedDiagnostic("first.err", "diagnostic-sha"));
    }

    private GoalOperationJournalEntry ProbeEntry() => Assert.Single(GoalOperationJournal.ReadStrict(_root, Goal).Entries,
        entry => entry.Operation == "acceptance:partition-within-attempt-probe");

    private AcceptanceCheckResult FirstRun(int silence)
    {
        var heartbeatPath = Path.Combine(_root, "first-heartbeat.json");
        GateHeartbeatArtifacts.Write(heartbeatPath, Snapshot(silence));
        return new(_partition.Name, false, -1, "first run timed out", TestResultPaths: [Path.Combine(_root, "first.trx")],
            GateHeartbeatPath: heartbeatPath, FailingTestIdentities: [],
            FailureClassification: AcceptanceShardCompletionPredicates.TimedOut,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false, AcceptanceShardCompletionPredicates.TimedOut, true, -1, null, null, "missing"));
    }

    private static GateHeartbeatSnapshot Snapshot(int silence) =>
        new(Goal.Value, "check", "infrastructure tests: Remainder", null, null, null, "timed-out",
            ObservedAt.AddSeconds(-2400), ObservedAt.AddSeconds(5), ObservedAt.AddSeconds(5), 9086, 0, 9086,
            PreStopObservedAt: ObservedAt, PreStopProgressAt: ObservedAt.AddSeconds(-silence));

    private AcceptanceCheckResult Probe(bool passed = true, bool writeTrx = true)
    {
        var path = Path.Combine(_root, "rerun.trx");
        if (writeTrx)
            File.WriteAllText(path, $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    {(passed ? "" : "<UnitTestResult testId=\"1\" testName=\"Ns.Cls.Fails\" outcome=\"Failed\" />")}
                  </Results>
                  <ResultSummary outcome="{(passed ? "Passed" : "Failed")}">
                    <Counters total="9822" executed="9822" passed="{(passed ? 9822 : 9821)}" failed="{(passed ? 0 : 1)}" />
                  </ResultSummary>
                </TestRun>
                """);
        return new AcceptanceCheckResult(_partition.Name, passed, passed ? 0 : 1, "rerun result",
            TestResultPaths: [path], FailingTestIdentities: passed ? [] : ["Ns.Cls.Fails"], ExecutedTestCount: 9822,
            CompletionDecision: new AcceptanceShardCompletionDecision(passed,
                passed ? null : AcceptanceShardCompletionPredicates.FailingTrx, false, passed ? 0 : 1,
                9822, 9822, "parsed"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
