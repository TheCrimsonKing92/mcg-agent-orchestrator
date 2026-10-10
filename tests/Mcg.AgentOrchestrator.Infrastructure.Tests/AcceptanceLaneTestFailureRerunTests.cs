using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: fake runners, instance-owned attempt memory, and no shared process or environment state.
public sealed class AcceptanceLaneTestFailureRerunTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task RunAsync_TestFailure_RerunsOnceAndRetainsBothOutcomes(bool rerunPasses)
    {
        var calls = new List<int>();
        var first = Result(1, false);
        var second = Result(2, rerunPasses);
        Task<AcceptanceCheckResult> Runner()
        {
            calls.Add(calls.Count + 1);
            return Task.FromResult(calls.Count == 1 ? first : second);
        }

        var attempt = new AcceptanceLaneTestFailureRerun(emit: _ => { });
        var original = await Runner();
        var result = await attempt.RunAsync(Check(), original, Runner, Invocation);
        var repeat = await attempt.RunAsync(Check(), original, Runner, Invocation);

        Assert.Equal([1, 2], calls);
        Assert.Same(original, repeat);
        Assert.Equal(rerunPasses, result.Passed);
        Assert.Equal(second.TestResultPaths, result.TestResultPaths);
        Assert.Equal(second.FailingTestIdentities, result.FailingTestIdentities);
        var evidence = Assert.IsType<AcceptanceLaneRerunEvidence>(result.LaneRerun);
        Assert.Equal(rerunPasses ? "flake" : "confirmed-failure", evidence.Outcome);
        Assert.Equal("invocation-1", evidence.FirstInvocationId);
        Assert.Equal(first.TestResultPaths, evidence.FirstTestResultPaths);
        Assert.Equal(first.FailingTestIdentities, evidence.FirstFailingTestIdentities);
        Assert.Equal("failing-trx", evidence.FirstPredicate);
        Assert.Equal("invocation-2", evidence.RerunInvocationId);
        Assert.Equal(second.TestResultPaths, evidence.RerunTestResultPaths);
        Assert.Equal(second.FailingTestIdentities, evidence.RerunFailingTestIdentities);
        Assert.Equal(second.CompletionDecision!.FailedPredicate, evidence.RerunPredicate);
        Assert.Contains("first_run=failed(failing-trx)", result.ResultSummary);
        Assert.Contains(rerunPasses ? "rerun=passed" : "rerun=failed", result.ResultSummary);
        Assert.EndsWith(second.ResultSummary!, result.ResultSummary);
    }

    [Xunit.Theory]
    [Xunit.InlineData("build-error")]
    [Xunit.InlineData("timed-out")]
    [Xunit.InlineData("missing-trx")]
    [Xunit.InlineData("malformed-trx")]
    [Xunit.InlineData("zero-tests")]
    [Xunit.InlineData("incomplete-execution")]
    [Xunit.InlineData("assembly-cleanup")]
    [Xunit.InlineData("empty-identities")]
    [Xunit.InlineData("shared-apparatus")]
    [Xunit.InlineData("cancelled")]
    [Xunit.InlineData("non-partition")]
    [Xunit.InlineData("passed")]
    [Xunit.InlineData("no-decision")]
    [Xunit.InlineData("unknown-predicate")]
    public async Task RunAsync_IneligibleResult_DoesNotCallRunner(string scenario)
    {
        var predicate = scenario switch
        {
            "build-error" => AcceptanceShardCompletionPredicates.NonzeroExit,
            "assembly-cleanup" => AcceptanceShardCompletionPredicates.AssemblyCleanupFailure,
            "timed-out" or "missing-trx" or "malformed-trx" or "zero-tests" or
                "incomplete-execution" or "unknown-predicate" => scenario,
            _ => AcceptanceShardCompletionPredicates.FailingTrx
        };
        var first = Result(1, scenario == "passed", predicate) with
        {
            FailingTestIdentities = scenario == "empty-identities" ? [] : ["First.Failed"],
            CompletionDecision = scenario == "no-decision" ? null : Decision(predicate),
            FailureClassification = scenario == "build-error" ? "build phase failed" : predicate
        };
        var check = Check(scenario == "non-partition" ? "dotnet-build" : "dotnet-test");
        var calls = 0;
        Task<AcceptanceCheckResult> Runner()
        {
            calls++;
            return Task.FromResult(first);
        }

        var original = await Runner();
        var token = new CancellationToken(scenario == "cancelled");
        var result = await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
            check, original, Runner, Invocation, scenario == "shared-apparatus", token);
        Assert.Same(original, result);
        Assert.Null(result.LaneRerun);
        Assert.Equal(1, calls);
    }

    [Xunit.Fact]
    public async Task RunAsync_ConcurrentAsk_ClaimsLaneBeforeRunningDelegate()
    {
        var released = new TaskCompletionSource<AcceptanceCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Result(1, false);
        var attempt = new AcceptanceLaneTestFailureRerun(emit: _ => { });
        var calls = 0;
        Task<AcceptanceCheckResult> Runner() { calls++; return released.Task; }
        var pending = attempt.RunAsync(Check(), first, Runner, Invocation);
        var repeat = await attempt.RunAsync(Check(), first, Runner, Invocation);
        Assert.Equal(1, calls);
        Assert.Same(first, repeat);
        released.SetResult(Result(2, true));
        Assert.True((await pending).Passed);
        Assert.True((await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
            Check(), first, () => Task.FromResult(Result(3, true)), Invocation)).Passed);
    }

    [Xunit.Fact]
    public async Task RunAsync_RerunApparatusFailure_ReturnsSecondFailureWithFirstEvidence()
    {
        var second = Result(2, false, AcceptanceShardCompletionPredicates.TimedOut);
        var result = await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
            Check(), Result(1, false), () => Task.FromResult(second), Invocation);
        Assert.False(result.Passed);
        Assert.Equal(second.CompletionDecision, result.CompletionDecision);
        Assert.Equal(second.TestResultPaths, result.TestResultPaths);
        Assert.Equal("timed-out", result.LaneRerun!.RerunPredicate);
        Assert.Equal("failing-trx", result.LaneRerun.FirstPredicate);
        Assert.Equal("confirmed-failure", result.LaneRerun.Outcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task RunAsync_MissingTrxProbe_DoesNotRerunTheProbeVerdict(bool probePasses)
    {
        var root = Directory.CreateTempSubdirectory("lane-probe-").FullName;
        try
        {
            var check = Check();
            var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
                new AcceptancePartitionVerdictCacheOptions(
                    new GoalId("12345678123456781234567812345678"), root, [check], 5, true,
                    _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest", () => false)));
            var calls = new List<int>();
            var first = Result(1, false, AcceptanceShardCompletionPredicates.MissingTrx) with
            {
                TestResultPaths = [], FailingTestIdentities = [], ExecutedTestCount = 0,
                CompletionDecision = new(false, "missing-trx", false, 1, 1, 0, "missing")
            };
            Task<AcceptanceCheckResult> Runner()
            {
                calls.Add(calls.Count + 1);
                return Task.FromResult(calls.Count == 1 ? first : Result(2, probePasses) with { ExecutedTestCount = 1 });
            }

            var original = await Runner();
            Assert.True(cache.ShouldRerunWithinAttempt(check, original.CompletionDecision));
            cache.RecordWithinAttemptRetry(check, original, "invocation-1", "invocation-2",
                new AcceptanceRetainedDiagnostic("first.err", "first-sha"));
            var probe = await Runner();
            var probeVerdict = cache.SelectPartitionVerdict(check, original, probe);
            var result = await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
                check, probeVerdict, Runner, Invocation);

            Assert.Equal([1, 2], calls);
            Assert.Same(probeVerdict, result);
            Assert.False(result.Passed);
            Assert.Equal("missing-trx", result.CompletionDecision!.FailedPredicate);
            Assert.Null(result.LaneRerun);
            Assert.Equal(probePasses, result.WithinAttemptRerun is not null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceManifestCheck Check(string type = "dotnet-test") => new()
    {
        Name = "infrastructure tests: Alpha", Type = type,
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
    };

    private static AcceptanceShardCompletionDecision Decision(string? predicate) =>
        new(predicate is null, predicate, predicate == "timed-out", predicate is null ? 0 : 1, 1, 1,
            predicate is null ? "Passed" : "Failed");

    private static AcceptanceCheckResult Result(int ordinal, bool passed, string predicate = "failing-trx") =>
        new("infrastructure tests: Alpha", passed, passed ? 0 : 1, $"output-{ordinal}",
            ResultSummary: $"summary-{ordinal}", TestResultPaths: [$"run-{ordinal}.trx"],
            TestResultRunOrdinal: ordinal, FailingTestIdentities: passed ? [] : [$"Run{ordinal}.Failed"],
            CompletionDecision: Decision(passed ? null : predicate));

    private static string Invocation(AcceptanceCheckResult result) => $"invocation-{result.TestResultRunOrdinal}";
}
