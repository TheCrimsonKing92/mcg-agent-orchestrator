using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using System.Xml.Linq;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

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
        var result = await attempt.RunAsync(Check(), original, _ => Runner(), Invocation);
        var repeat = await attempt.RunAsync(Check(), original, _ => Runner(), Invocation);

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
            check, original, _ => Runner(), Invocation, scenario == "shared-apparatus", token);
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
        var pending = attempt.RunAsync(Check(), first, _ => Runner(), Invocation);
        var repeat = await attempt.RunAsync(Check(), first, _ => Runner(), Invocation);
        Assert.Equal(1, calls);
        Assert.Same(first, repeat);
        released.SetResult(Result(2, true));
        Assert.True((await pending).Passed);
        Assert.True((await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
            Check(), first, _ => Task.FromResult(Result(3, true)), Invocation)).Passed);
    }

    [Xunit.Fact]
    public async Task RunAsync_RerunApparatusFailure_ReturnsSecondFailureWithFirstEvidence()
    {
        var second = Result(2, false, AcceptanceShardCompletionPredicates.TimedOut);
        var result = await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
            Check(), Result(1, false), _ => Task.FromResult(second), Invocation);
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
                check, probeVerdict, _ => Runner(), Invocation);

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

    [Xunit.Fact]
    public void Selection_NormalizesAndDeduplicatesMethods()
    {
        var plan = AcceptanceLaneRerunSelection.Create([
            "Ns.First.Plain", "Ns.Second.Plain", "Cls.M(a: 1)", "Cls.M(a: 2)",
            "Nested+Cls.Method[case 1]", "DisplayClass.DisplayMethod"]);
        Assert.True(plan.IsNarrowed);
        Assert.Null(plan.FallbackReason);
        Assert.Equal(["Cls.M", "DisplayClass.DisplayMethod", "Nested+Cls.Method", "Ns.First.Plain", "Ns.Second.Plain"],
            plan.Tokens.Select(token => token.Value));
        Assert.All(plan.Tokens, token =>
        {
            Assert.Equal(FocusedEvidenceTokenKind.Method, token.Kind);
            Assert.Equal("FullyQualifiedName~" + token.Value, token.CanonicalToken);
            Assert.Equal(token.Value[..token.Value.LastIndexOf('.')], token.ContainingClass);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData("", AcceptanceLaneRerunSelection.BlankIdentity)]
    [Xunit.InlineData("  ", AcceptanceLaneRerunSelection.BlankIdentity)]
    [Xunit.InlineData("unknown test", AcceptanceLaneRerunSelection.UnknownIdentity)]
    [Xunit.InlineData("[Test Assembly Cleanup Failure (Fixture)]", AcceptanceLaneRerunSelection.AssemblyCleanupIdentity)]
    [Xunit.InlineData("Bare display name", AcceptanceLaneRerunSelection.NoClassForm)]
    [Xunit.InlineData("Cls.Method with space", AcceptanceLaneRerunSelection.UnsafeSelectorCharacter)]
    [Xunit.InlineData("Cls.M*", AcceptanceLaneRerunSelection.UnsafeSelectorCharacter)]
    [Xunit.InlineData("Cls..M", AcceptanceLaneRerunSelection.NoClassForm)]
    public void Selection_UnsafeIdentityFallsBack(string identity, string reason)
    {
        var plan = AcceptanceLaneRerunSelection.Create(["Valid.Method", identity]);
        Assert.False(plan.IsNarrowed);
        Assert.Equal(reason, plan.FallbackReason);
        Assert.Empty(plan.Tokens);
    }

    [Xunit.Fact]
    public void Selection_EmptyAndMethodLimitFallBack()
    {
        Assert.Equal(AcceptanceLaneRerunSelection.NoIdentities, AcceptanceLaneRerunSelection.Create([]).FallbackReason);
        var identities = Enumerable.Range(0, 26).Select(index => $"Cls.M{index}").ToArray();
        Assert.True(AcceptanceLaneRerunSelection.Create(identities[..25]).IsNarrowed);
        var overLimit = AcceptanceLaneRerunSelection.Create(identities);
        Assert.False(overLimit.IsNarrowed);
        Assert.Equal(AcceptanceLaneRerunSelection.TooManyMethods, overLimit.FallbackReason);
        Assert.Empty(overLimit.Tokens);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RunAsync_DelegateReceivesNarrowedCheckOrOriginalFallback(bool overLimit)
    {
        var check = MtpCheck();
        var first = Result(1, false) with
        {
            FailingTestIdentities = overLimit
                ? Enumerable.Range(0, 26).Select(index => $"Cls.M{index}").ToArray()
                : ["Cls.M(a: 1)", "Cls.M(a: 2)", "Other.N[1]", "Other.N[2]"]
        };
        AcceptanceManifestCheck? received = null;
        var lines = new List<string>();
        var result = await new AcceptanceLaneTestFailureRerun(emit: lines.Add).RunAsync(check, first,
            selected => { received = selected; return Task.FromResult(Result(2, false)); }, Invocation);
        Assert.NotNull(received);
        if (overLimit)
            Assert.Same(check, received);
        else
        {
            Assert.NotSame(check, received);
            Assert.Equal(["Cls.M", "Other.N"], received.FocusedEvidenceTokens.Select(token => token.Value));
            Assert.All(received.FocusedEvidenceTokens, token => Assert.Equal(FocusedEvidenceTokenKind.Method, token.Kind));
            foreach (var property in typeof(AcceptanceManifestCheck).GetProperties()
                         .Where(property => property.GetMethod is { IsStatic: false } && property.Name != nameof(check.FocusedEvidenceTokens)))
                Assert.Equal(property.GetValue(check), property.GetValue(received));
        }
        var mode = overLimit ? "mode=whole-lane reason=too-many-methods" : "mode=failed-only";
        Assert.Contains(mode, result.ResultSummary);
        Assert.Contains(mode, Assert.Single(lines));
    }

    [Xunit.Theory]
    [Xunit.InlineData("all-executed", true)]
    [Xunit.InlineData("too-few", false)]
    [Xunit.InlineData("zero", false)]
    [Xunit.InlineData("failing-method", false)]
    [Xunit.InlineData("unreadable-trx", false)]
    [Xunit.InlineData("no-receipt", false)]
    [Xunit.InlineData("no-decision", false)]
    [Xunit.InlineData("contradictory-failures", false)]
    public async Task RunAsync_NarrowedVerdictRequiresCompletePassingEvidence(string scenario, bool expectedPass)
    {
        var first = Result(1, false) with { FailingTestIdentities = ["Cls.M", "Other.N"] };
        var count = scenario == "zero" ? 0 : scenario == "too-few" ? 1 : 2;
        var second = Result(2, scenario != "failing-method") with
        {
            ExecutedTestCount = count,
            CompletionDecision = scenario == "no-decision" ? null :
                new(scenario != "unreadable-trx" && scenario != "failing-method",
                    scenario == "unreadable-trx" ? "malformed-trx" : scenario == "failing-method" ? "failing-trx" : null,
                    false, 0, 2, count, "Completed"),
            TestResultPaths = scenario == "no-receipt" ? [] : ["rerun.trx"],
            FailingTestIdentities = scenario is "failing-method" or "contradictory-failures" ? ["Other.N"] : []
        };
        var writes = new List<AcceptanceLaneFlakeRow>();
        var lines = new List<string>();
        var ledger = new AcceptanceLaneFlakeLedger("unused-ledger-root", writes.Add);
        var attempt = new AcceptanceLaneTestFailureRerun(ledger, emit: lines.Add);
        var calls = 0;
        Task<AcceptanceCheckResult> Runner(AcceptanceManifestCheck _) { calls++; return Task.FromResult(second); }
        var result = await attempt.RunAsync(MtpCheck(), first, Runner, Invocation);
        var repeat = await attempt.RunAsync(MtpCheck(), first, Runner, Invocation);
        Assert.Equal(expectedPass, result.Passed);
        var outcome = expectedPass ? AcceptanceLaneRerunEvidence.Flake : AcceptanceLaneRerunEvidence.ConfirmedFailure;
        Assert.Equal(outcome, result.LaneRerun!.Outcome);
        Assert.Equal(outcome, Assert.Single(writes).Outcome);
        var line = Assert.Single(lines);
        Assert.StartsWith("LANE_RERUN ", line);
        Assert.Contains("outcome=" + outcome, line);
        Assert.Contains("mode=failed-only", line);
        Assert.Contains("mode=failed-only", result.ResultSummary);
        Assert.Same(first, repeat);
        Assert.Equal(1, calls);
    }

    [Xunit.Fact]
    public void NarrowedArguments_ReplaceLongNegativeLaneFilter()
    {
        var check = MtpCheck(string.Join('&', Enumerable.Range(0, 200).Select(index => $"FullyQualifiedName!~Excluded{index}Tests")));
        var narrowed = AcceptanceLaneRerunNarrowing.BuildRerunCheck(check,
            AcceptanceLaneRerunSelection.Create(["Ns.Cls.M", "Ns.Other.N"]));
        var environment = new DotnetBuildEnvironment("fake-slot", "root", "artifacts", "lock", [], "build-0", BuildPermitIndex: 0);
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations = [new() { Project = check.Project!, ExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.dll",
                Arguments = ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"] }]
        };
        var args = AcceptanceCheckCommandBuilder.BuildMtpTestArguments(narrowed, settings, environment,
            new DotnetTestTelemetry([Path.Combine("results", "rerun.trx")], []), false,
            (_, _) => throw new InvalidOperationException("Lane filter must be replaced."));
        Assert.Equal(["*Ns.Cls.M*", "*Ns.Other.N*"], args.Select((arg, index) => (arg, index))
            .Where(pair => pair.arg == "--filter-method").Select(pair => args[pair.index + 1]));
        Assert.DoesNotContain("--filter-class", args);
        Assert.DoesNotContain("--filter-not-class", args);
    }

    [Xunit.Fact]
    public async Task RunAsync_NarrowedResultPreservesWholeLaneStructuralCoverage()
    {
        var root = Directory.CreateTempSubdirectory("lane-coverage-").FullName;
        try
        {
            var firstPath = Path.Combine(root, "first.trx");
            var rerunPath = Path.Combine(root, "rerun.trx");
            string[] discovered = ["Cls.Passed", "Other.Passed", "Cls.M", "Other.N"];
            WriteCoverageTrx(firstPath, discovered, failed: ["Cls.M", "Other.N"]);
            WriteCoverageTrx(rerunPath, ["Cls.M", "Other.N"], failed: []);
            var first = Result(1, false) with { TestResultPaths = [firstPath], FailingTestIdentities = ["Cls.M", "Other.N"],
                ExecutedTestCount = 4, DiscoveredTestCount = 4 };
            var rerun = Result(2, true) with { TestResultPaths = [rerunPath], ExecutedTestCount = 2, DiscoveredTestCount = 2,
                CompletionDecision = new(true, null, false, 0, 2, 2, "Completed") };
            var result = await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
                MtpCheck(), first, _ => Task.FromResult(rerun), Invocation);
            Assert.True(result.Passed);
            Assert.Equal([firstPath, rerunPath], result.TestResultPaths);
            Assert.Equal(4, result.ExecutedTestCount);
            Assert.Equal(4, result.DiscoveredTestCount);
            var partition = new TestPartitionCoverage(result.Name, result.Passed, result.TestResultPaths!,
                AttemptId: result.TestResultAttemptId, RunOrdinal: result.TestResultRunOrdinal);
            var coverage = TestCoverageInvariant.Evaluate(discovered, [partition]);
            Assert.True(coverage.Passed, "TestCoverageInvariant.Evaluate: " + coverage.Summary);
            Assert.Equal(discovered.Order(), coverage.ExecutedTests.Order());
            Assert.Equal(4, TestCoverageInvariant.ReadRecordedTests(result.TestResultPaths!).Count);
            var subsetOnly = TestCoverageInvariant.Evaluate(discovered, [partition with { TestResultPaths = [rerunPath] }]);
            Assert.False(subsetOnly.Passed);
            Assert.Equal(["Cls.Passed", "Other.Passed"], subsetOnly.MissingTests.Order());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("vstest", true, AcceptanceLaneRerunNarrowing.RunnerNotMtp)]
    [Xunit.InlineData("mtp", false, AcceptanceLaneRerunNarrowing.MissingLaneFilter)]
    public void Narrowing_UnsupportedRunnerOrMissingLaneFilterPreservesCheck(string runner, bool hasFilter, string reason)
    {
        var check = new AcceptanceManifestCheck { Runner = runner, Arguments = hasFilter ? ["--filter", "FullyQualifiedName~AlphaTests"] : [] };
        var plan = AcceptanceLaneRerunNarrowing.Select(check, ["Cls.M"]);
        Assert.False(plan.IsNarrowed);
        Assert.Equal(reason, plan.FallbackReason);
        Assert.Same(check, AcceptanceLaneRerunNarrowing.BuildRerunCheck(check, plan));
    }

    private static AcceptanceManifestCheck MtpCheck(string filter = "FullyQualifiedName~AlphaTests") => new()
    {
        Name = "infrastructure tests: Alpha", Type = "dotnet-test", Runner = "mtp",
        Project = Check().Project, Arguments = ["--filter", filter], IsResolvedInfrastructureLane = true,
        Command = "dotnet", Pattern = "pattern", FilePath = "path", Advisory = true, TimeoutMinutes = 3,
        RequiresBuildSystemChange = true, EstimatedSerialSeconds = 17, ExclusiveResourceKeys = ["owned-resource"],
        IsFocusedEvidenceSelection = true, FocusedEvidenceSelections = [[new("old", "old", FocusedEvidenceTokenKind.Class, "Cls", "Cls")]],
        FocusedEvidenceMatchedClasses = [new("old", ["Cls"])]
    };

    private static void WriteCoverageTrx(string path, IReadOnlyList<string> identities, IReadOnlyList<string> failed) =>
        new XDocument(new XElement("TestRun", new XElement("Results", identities.Select(identity =>
            new XElement("UnitTestResult", new XAttribute("testName", identity),
                new XAttribute("outcome", failed.Contains(identity) ? "Failed" : "Passed")))))).Save(path);

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
