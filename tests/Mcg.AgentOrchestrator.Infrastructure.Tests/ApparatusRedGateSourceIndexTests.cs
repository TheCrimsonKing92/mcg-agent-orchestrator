using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Unique temporary roots and census files; parallel safe, with no process/global state changes.
public sealed class ApparatusRedGateSourceIndexTests
{
    [Fact]
    public void CompletionSharesSourcesAcrossChecksAndChangedTypeInspection()
    {
        using var fixture = new Fixture();
        fixture.WriteSource("tests/A/Tests.cs", "class Shared { Changed value; }");
        fixture.WriteSource("tests/B/Tests.cs", "class Shared {}");
        fixture.WriteSource("src/Changed.cs", "class Changed {}");
        var identities = Enumerable.Range(0, 3000).Select(i => $"N.Shared.Fact{i}").ToArray();
        var a = new AcceptanceCheckResult("a", false, 1, "assertion", TestProjectPath: "tests/A/A.csproj",
            FailingTestIdentities: identities);
        var b = a with { Name = "b", TestProjectPath = "tests/B/B.csproj", FailingTestIdentities = ["N.Shared.Other"] };
        var reading = fixture.Record(a, a with { Name = "a-again" }, b);
        Assert.Equal(6001, reading.FailingTests.Count);
        Assert.Equal(2, reading.SourceEnumerationCount);
        Assert.Equal(3, reading.SourceReadCount);
        Assert.All(fixture.ReadCounts.Values, count => Assert.Equal(1, count));
        Assert.All(reading.FailingTests.Where(test => test.CheckName != "b"), test =>
        {
            Assert.Equal(new[] { "tests/A/Tests.cs" }, test.ResolvedSourcePaths);
            Assert.Equal(new[] { "Changed" }, test.ReferencedChangedTypes);
        });
        Assert.Empty(reading.FailingTests.Last().ReferencedChangedTypes!);
        fixture.WriteSource("tests/A/Tests.cs", "class Replacement {}");
        var next = fixture.Record(a with { FailingTestIdentities = ["N.Shared.Fact"] });
        Assert.Empty(Assert.Single(next.FailingTests).ResolvedSourcePaths);
        Assert.Equal(2, fixture.ReadCounts[Path.Combine(fixture.Root, "tests/A/Tests.cs")]);
    }

    [Fact]
    public void DefinitionBackedCleanupShapeRetainsOneAssemblyFailureAndNoSyntheticCensus()
    {
        using var fixture = new Fixture();
        var original = fixture.CleanupCheck(9532, 9529, 3);
        var recordedAt = fixture.Now;
        fixture.Index.Append([new AcceptanceFailingTestIndexRecord(AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.GateFailure, "unrelated-goal", recordedAt, TestIdentity: "N.Other.Fact")], recordedAt);
        var rerunPath = fixture.WriteTrx(0, 9529, 3, "rerun");
        var rerun = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([rerunPath]);
        Assert.True(rerun.Passed);
        Assert.Equal(9532, rerun.DiscoveredTestCount);
        Assert.Equal(9529, rerun.ExecutedTestCount);
        Assert.Equal(3, rerun.NotExecutedTestCount);
        Assert.NotEqual(original.AssemblyCleanupAttribution!.Receipts.Single().RunId,
            XDocument.Load(rerunPath).Root!.Attribute("id")!.Value);
        var aggregate = new AcceptanceCheckResult("infrastructure tests", false, 2, "assembly failure",
            CoveredBy: [original.Name], FailingTestIdentities: original.FailingTestIdentities,
            FailureClassification: AcceptanceFailureClassifications.SharedGateApparatusInvalidated);
        var reading = fixture.Record(original, aggregate);
        Assert.Empty(reading.FailingTests);
        var cleanup = Assert.Single(reading.AssemblyCleanupFailures!);
        Assert.False(cleanup.Passed);
        Assert.Equal(2, cleanup.ExitCode);
        Assert.Equal(AcceptanceShardCompletionPredicates.AssemblyCleanupFailure, cleanup.CompletionDecision!.FailedPredicate);
        Assert.Equal(19064, cleanup.CompletionDecision.DiscoveredTestCount);
        Assert.Equal(3, cleanup.CompletionDecision.NotExecutedTestCount);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, cleanup.FailureCauseEvidence!.Cause);
        Assert.Contains("cause undetermined", cleanup.FailureCauseEvidence.Evidence, StringComparison.Ordinal);
        Assert.Equal(Fixture.Attempt, cleanup.TestResultAttemptId);
        Assert.Equal(0, cleanup.TestResultRunOrdinal);
        Assert.Null(cleanup.WithinAttemptRerun); // A green probe does not invent pass evidence for cleanup.
        Assert.Equal(0, reading.SourceEnumerationCount);
        Assert.Equal(0, reading.SourceReadCount);
        Assert.Equal("unrelated-goal", Assert.Single(fixture.Index.Read()).GoalId);
        Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.True(ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(fixture.Summary(original, aggregate)));
    }

    [Fact]
    public void MarkerEmbeddedInOrdinaryNameDoesNotCreateCleanupProof()
    {
        using var fixture = new Fixture();
        var check = fixture.CleanupCheck(2, 1, 0);
        var path = check.TestResultPaths![0];
        var document = XDocument.Load(path);
        foreach (var row in document.Descendants("UnitTestResult").Where(row => (string?)row.Attribute("outcome") == "Failed"))
            row.SetAttributeValue("testName", "ordinary " + row.Attribute("testName")!.Value);
        document.Save(path);
        check = check with { AssemblyCleanupAttribution = null };
        Assert.Null(AcceptanceAssemblyCleanupAttributionReader.Attach(check, fixture.Goal.Id.Value,
            Fixture.Candidate, Fixture.Main).AssemblyCleanupAttribution);
        Assert.Empty(fixture.Record(check).AssemblyCleanupFailures!);
    }

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("candidate")]
    [InlineData("main")]
    [InlineData("goal")]
    [InlineData("attempt")]
    [InlineData("partition")]
    [InlineData("ordinal")]
    [InlineData("run-id")]
    [InlineData("hash")]
    [InlineData("missing-trx")]
    [InlineData("malformed-trx")]
    [InlineData("extra-unreadable-trx")]
    [InlineData("extra-identity")]
    public void MissingOrStaleProofNeverExemptsFailingIdentities(string mutation)
    {
        using var fixture = new Fixture();
        var check = fixture.CleanupCheck(2, 1, 0);
        var proof = check.AssemblyCleanupAttribution!;
        check = mutation switch
        {
            "missing-proof" => check with { AssemblyCleanupAttribution = null },
            "candidate" => check with { AssemblyCleanupAttribution = proof with { CandidateSha = "stale" } },
            "main" => check with { AssemblyCleanupAttribution = proof with { MainSha = "stale" } },
            "goal" => check with { AssemblyCleanupAttribution = proof with { GoalId = "other" } },
            "attempt" => check with { AssemblyCleanupAttribution = proof with { AttemptId = "other" } },
            "partition" => check with { AssemblyCleanupAttribution = proof with { CheckName = "other" } },
            "ordinal" => check with { TestResultRunOrdinal = 1 },
            "run-id" => check with { AssemblyCleanupAttribution = proof with { Receipts = [proof.Receipts[0] with { RunId = "other" }] } },
            "hash" => check with { AssemblyCleanupAttribution = proof with { Receipts = [proof.Receipts[0] with { Sha256 = "other" }] } },
            "extra-unreadable-trx" => check with { TestResultPaths = [check.TestResultPaths![0], Path.Combine(fixture.Root, "missing.trx")] },
            "extra-identity" => check with { FailingTestIdentities = [.. check.FailingTestIdentities!, "Real.Failed.Fact"] },
            _ => check
        };
        if (mutation == "missing-trx") File.Delete(check.TestResultPaths![0]);
        if (mutation == "malformed-trx") File.WriteAllText(check.TestResultPaths![0], "<broken");
        var reading = fixture.Record(check);
        Assert.Empty(reading.AssemblyCleanupFailures!);
        Assert.Equal(check.FailingTestIdentities!.Count, reading.FailingTests.Count);
        Assert.NotEmpty(fixture.Index.Read());
        Assert.False(check.Passed);
    }

    [Fact]
    public void MixedRowsRetainRealFailuresAndDoNotAuthorizeCleanupOnlyProof()
    {
        using var fixture = new Fixture();
        var cleanup = fixture.CleanupCheck(2, 1, 0);
        var path = cleanup.TestResultPaths![0];
        var document = XDocument.Load(path);
        document.Root!.Element("Results")!.Add(new XElement("UnitTestResult",
            new XAttribute("testName", "N.Real.Fact"), new XAttribute("outcome", "Failed")));
        document.Save(path);
        cleanup = cleanup with { FailingTestIdentities = [.. cleanup.FailingTestIdentities!, "N.Real.Fact"] };
        var reading = fixture.Record(cleanup);
        Assert.Empty(reading.AssemblyCleanupFailures!);
        Assert.Equal(3, reading.FailingTests.Count);
        Assert.Contains(reading.FailingTests, test => test.TestIdentity == "N.Real.Fact");
        Assert.Null(AcceptanceAssemblyCleanupAttributionReader.Attach(cleanup with { AssemblyCleanupAttribution = null },
            fixture.Goal.Id.Value, Fixture.Candidate, Fixture.Main).AssemblyCleanupAttribution);
    }

    [Fact]
    public void CompletionRecordsOneFailedDecisionAndNextGoalCanAdvance()
    {
        // Only this test host consumes the input reference; it launches no descendants. Missing input
        // still exercises the full normalized shape below, never claims an actual-copy replay.
        var copiedInput = Environment.GetEnvironmentVariable(AcceptanceAttributionReplayFixture.InputVariable);
        if (!string.IsNullOrWhiteSpace(copiedInput))
        {
            var actual = AcceptanceAttributionReplayFixture.ReplayCopiedAttempt(copiedInput);
            Assert.Equal(1, actual.FailureDecisions);
            Assert.True(actual.NextGoalAdvanced);
            Assert.Equal(0, actual.SourceEnumerations);
            Assert.Equal(0, actual.SourceReads);
            Assert.IsType<Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceOutcome.Held>(actual.Outcome);
            return;
        }
        using var fixture = new Fixture();
        var original = fixture.CleanupCheck(9532, 9529, 3);
        var replay = AcceptanceAttributionReplayFixture.Complete(fixture.Goal, fixture.Summary(original),
            fixture.Root, Path.Combine(fixture.Root, "replay-census.jsonl"), ["src/Changed.cs"]);
        Assert.Equal(1, replay.FailureDecisions);
        Assert.True(replay.NextGoalAdvanced);
        Assert.Equal(0, replay.SourceEnumerations);
        Assert.Equal(0, replay.SourceReads);
        Assert.Equal(Fixture.Candidate, replay.CandidateSha);
        Assert.Equal(Fixture.Main, replay.MainSha);
        Assert.IsType<Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceOutcome.Held>(replay.Outcome);
    }

    [Fact]
    public void ProvenCleanupBesideRealCheckKeepsRealAttributionAndRefusesApparatusRoute()
    {
        using var fixture = new Fixture();
        fixture.WriteSource("tests/A/Real.cs", "class Real {}");
        var cleanup = fixture.CleanupCheck(2, 1, 0);
        var real = new AcceptanceCheckResult("real", false, 1, "real assertion",
            FailingTestIdentities: ["N.Real.Fact"], TestProjectPath: "tests/A/A.csproj");
        var aggregate = new AcceptanceCheckResult("aggregate", false, 1, "failed",
            CoveredBy: [cleanup.Name, real.Name], FailingTestIdentities: [.. cleanup.FailingTestIdentities!, "N.Real.Fact"]);
        var reading = fixture.Record(cleanup, real, aggregate);
        Assert.Single(reading.AssemblyCleanupFailures!);
        Assert.Equal(2, reading.FailingTests.Count);
        Assert.All(reading.FailingTests, test =>
        {
            Assert.Equal("N.Real.Fact", test.TestIdentity);
            Assert.Equal(new[] { "tests/A/Real.cs" }, test.ResolvedSourcePaths);
        });
        Assert.False(ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(fixture.Summary(cleanup, real, aggregate)));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Attempt = "recorded-attempt";
        internal const string Candidate = "candidate-commit";
        internal const string Main = "main-commit";
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal Goal Goal { get; } = ConductorDriverTests.SimpleGoal().Goal;
        internal AcceptanceFailingTestIndex Index { get; }
        internal ApparatusRedGate Gate { get; }
        internal Dictionary<string, int> ReadCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-13T15:00:00Z");
        internal Fixture()
        {
            Index = new(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
            Gate = new(Index, _ => Root, () => Now, createSourceRequest: root => new(root, read: path =>
            {
                ReadCounts[path] = ReadCounts.GetValueOrDefault(path) + 1;
                return File.ReadAllText(path);
            }));
        }
        internal void WriteSource(string path, string source)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);
        }
        internal AcceptanceVerificationSummary Summary(params AcceptanceCheckResult[] checks) =>
            new(false, checks, FailedChecks: checks.Select(check => check.Name).ToArray(), BranchHeadSha: Candidate, MainHeadSha: Main);
        internal ApparatusRedGateReading Record(params AcceptanceCheckResult[] checks) =>
            Assert.IsType<ApparatusRedGateReading>(Gate.RecordGateCompletion(Goal, Summary(checks),
                new Lazy<IReadOnlyList<string>>(() => ["src/Changed.cs"])));
        internal AcceptanceCheckResult CleanupCheck(int failed, int passed, int skipped)
        {
            var path = WriteTrx(failed, passed, skipped, "original");
            var trx = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([path]);
            var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 2, trx);
            var check = new AcceptanceCheckResult("infrastructure tests: Remainder", false, 2, null,
                TestResultPaths: [path], FailureClassification: decision.FailedPredicate, TestResultAttemptId: Attempt,
                FailingTestIdentities: AcceptanceTrxTestIdentityResolver.ExtractTrxFailureIdentities(path),
                CompletionDecision: decision, ProcessStderr: "assembly-temp-cleanup: cause undetermined",
                TestProjectPath: "tests/A/A.csproj");
            var bound = AcceptanceAssemblyCleanupAttributionReader.Attach(check, Goal.Id.Value, Candidate, Main);
            Assert.NotNull(bound.AssemblyCleanupAttribution);
            return bound;
        }
        internal string WriteTrx(int failed, int passed, int skipped, string label)
        {
            var rows = new List<XElement>();
            var definitions = new List<XElement>();
            for (var i = 0; i < Math.Max(failed, passed + skipped); i++)
            {
                var id = Guid.NewGuid().ToString();
                var className = $"CleanupTests{i}";
                definitions.Add(new XElement("UnitTest", new XAttribute("id", id),
                    new XAttribute("name", $"{className}.Fact"), new XElement("TestMethod",
                        new XAttribute("className", className), new XAttribute("name", "Fact"))));
                if (i < passed + skipped) rows.Add(Row(id, $"{className}.Fact", i < passed ? "Passed" : "NotExecuted"));
                if (i < failed) rows.Add(Row(id, $"[Test Assembly Cleanup Failure ({className}.Fact)]", "Failed"));
            }
            var path = Path.Combine(Root, label + ".trx");
            new XDocument(new XElement("TestRun", new XAttribute("id", Guid.NewGuid()),
                new XElement("TestDefinitions", definitions), new XElement("Results", rows),
                new XElement("ResultSummary", new XAttribute("outcome", failed > 0 ? "Failed" : "Passed"),
                    new XElement("Counters", new XAttribute("total", rows.Count),
                        new XAttribute("executed", failed + passed), new XAttribute("passed", passed),
                        new XAttribute("failed", failed), new XAttribute("notExecuted", skipped))))).Save(path);
            return path;
        }
        private static XElement Row(string id, string name, string outcome) => new("UnitTestResult",
            new XAttribute("testId", id), new XAttribute("executionId", Guid.NewGuid()),
            new XAttribute("testName", name), new XAttribute("outcome", outcome));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
