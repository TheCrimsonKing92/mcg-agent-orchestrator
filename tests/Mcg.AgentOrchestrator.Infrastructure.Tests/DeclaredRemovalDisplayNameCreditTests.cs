using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all source and receipt files belong to this fixture's unique temporary directory.
public sealed class DeclaredRemovalDisplayNameCreditTests : IDisposable
{
    private const string Survivor = "Namespace.OtherTests.Survives";
    private const string FactName = "SemanticAcceptancePlanner_parses_a_valid_fenced_verdict";
    private const string TheoryName = "Custom verdict cases";
    private const string SourceFile = "SampleTests.cs";
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(DeclaredRemovalDisplayNameCreditTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Evaluate_RemovedDisplayNameFact_CorroboratesClassMethod()
    {
        var coverage = await EvaluateAsync([FactName], ["SampleTests.RemovedFact"]);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, "SampleTests.RemovedFact", 1);
        Assert.Equal(new TestCoverageCountComparison(1, 1, 2, 1, false), coverage.CountComparison);
        Assert.Contains("cross-generation-count:candidate=1,minimum=1,main=2,deleted=1", coverage.Summary);
    }

    [Fact]
    public async Task Evaluate_RemovedDisplayNameTheory_CreditsEveryExpansion()
    {
        var coverage = await EvaluateAsync(
            [TheoryName + "(value: 1)", TheoryName + "(value: 2)", TheoryName + "(value: 3)"],
            ["SampleTests.RemovedTheory"]);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, "SampleTests.RemovedTheory", 3);
        Assert.Equal(new TestCoverageCountComparison(1, 1, 4, 3, false), coverage.CountComparison);
    }

    [Fact]
    public async Task Evaluate_UndeclaredDisplayNameFact_KeepsCoverageFloor()
    {
        var coverage = await EvaluateAsync([FactName], []);

        Assert.False(coverage.Passed);
        Assert.Equal(new TestCoverageCountComparison(1, 2, 2, 0, true), coverage.CountComparison);
        Assert.Contains("cross-generation-count:candidate=1,minimum=2,main=2,deleted=0", coverage.MissingTests);
    }

    [Fact]
    public async Task Evaluate_DeclaredFactStillPresent_CreditsZero()
    {
        var coverage = await EvaluateAsync([FactName], ["SampleTests.RemovedFact"], [FactName]);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, "SampleTests.RemovedFact", 0);
        Assert.Equal(new TestCoverageCountComparison(2, 2, 2, 0, false), coverage.CountComparison);
    }

    [Fact]
    public async Task Evaluate_DeclaredTheoryStillPresent_CreditsZero()
    {
        string[] rows = [TheoryName + "(value: 1)", TheoryName + "(value: 2)"];
        var coverage = await EvaluateAsync(rows, ["SampleTests.RemovedTheory"], rows);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, "SampleTests.RemovedTheory", 0);
        Assert.Equal(0, coverage.CountComparison!.DeletedCount);
    }

    [Theory]
    [InlineData("prefix_Custom verdict cases")]
    [InlineData("Custom verdict cases_extra")]
    [InlineData("Custom verdict cases (value: 1)")]
    [InlineData("Custom verdict cases(value: 1) trailing")]
    [InlineData("Custom verdict cases(value: 1")]
    public async Task Evaluate_DisplayNameNearMiss_CreditsZero(string discovered)
    {
        var coverage = await EvaluateAsync([discovered], ["SampleTests.RemovedTheory"]);

        Assert.False(coverage.Passed);
        AssertCredit(coverage, "SampleTests.RemovedTheory", 0);
        Assert.Equal(2, coverage.CountComparison!.MinimumCandidateCount);
    }

    [Fact]
    public async Task Evaluate_DisplayNameCaseDifference_Corroborates()
    {
        var coverage = await EvaluateAsync([FactName.ToUpperInvariant()], ["SampleTests.RemovedFact"]);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, "SampleTests.RemovedFact", 1);
    }

    [Fact]
    public async Task Evaluate_MapOmitted_RetainsPreChangeUnmatchedReceipt()
    {
        var coverage = await EvaluateAsync([FactName], ["SampleTests.RemovedFact"], omitMap: true);

        Assert.False(coverage.Passed);
        AssertCredit(coverage, "SampleTests.RemovedFact", 0);
        Assert.Equal(new TestCoverageCountComparison(1, 2, 2, 0, true), coverage.CountComparison);
    }

    [Fact]
    public async Task Evaluate_ContainedGeneration_ResolvesItsOwnMainSources()
    {
        var coverage = await EvaluateAsync([FactName], ["SampleTests.RemovedFact"], useContainedBaseline: true);

        Assert.True(coverage.Passed, coverage.Summary);
        AssertCredit(coverage, "SampleTests.RemovedFact", 1);
        Assert.Equal(new TestCoverageCountComparison(1, 1, 2, 1, false), coverage.CountComparison);
        Assert.Contains("disposition=integration-stale", coverage.Summary);
    }

    [Fact]
    public void Resolve_QualifiedAndNestedClasses_BindsBothClassAndMethod()
    {
        var mainRoot = WriteSources();
        var resolver = new MainTestDisplayNameResolver(mainRoot, [SourceFile]);

        Assert.Equal([FactName], resolver.Resolve("SampleTests.RemovedFact"));
        Assert.Equal([FactName], resolver.Resolve("Namespace.SampleTests.RemovedFact"));
        Assert.Equal(["Nested display"], resolver.Resolve("Namespace.Outer.Inner.RemovedFact"));
        Assert.Empty(resolver.Resolve("UnrelatedTests.RemovedFact"));
        Assert.Empty(resolver.Resolve("OtherNamespace.SampleTests.RemovedFact"));
        Assert.Empty(resolver.Resolve("Inner.RemovedTheory"));
    }

    [Fact]
    public void Resolve_LiteralAttributes_ExcludesOtherAttributesAndExpressions()
    {
        var mainRoot = WriteSources();
        var resolver = new MainTestDisplayNameResolver(mainRoot, [SourceFile]);

        Assert.Equal(["Verbatim \"name\"", "Second name"], resolver.Resolve("SampleTests.Literals"));
        Assert.Empty(resolver.Resolve("SampleTests.NonLiteral"));
        Assert.Empty(resolver.Resolve("SampleTests.UnrelatedAttribute"));
    }

    [Fact]
    public void Resolve_UnavailableOrUnknownSource_ReturnsNoNames()
    {
        var mainRoot = WriteSources();
        var absoluteSource = Path.Combine(mainRoot, SourceFile);
        Assert.Equal([FactName], new MainTestDisplayNameResolver(mainRoot, [absoluteSource])
            .Resolve("SampleTests.RemovedFact"));
        Assert.Empty(new MainTestDisplayNameResolver(null, [absoluteSource]).Resolve("SampleTests.RemovedFact"));
        Assert.Empty(new MainTestDisplayNameResolver(mainRoot, []).Resolve("SampleTests.RemovedFact"));
        Assert.Empty(new MainTestDisplayNameResolver(mainRoot, ["missing.cs"]).Resolve("SampleTests.RemovedFact"));
        File.WriteAllText(Path.Combine(mainRoot, "malformed.cs"), "class Broken {");
        Assert.Empty(new MainTestDisplayNameResolver(mainRoot, ["malformed.cs"]).Resolve("Broken.RemovedFact"));
    }

    private async Task<TestCoverageInvariantResult> EvaluateAsync(
        string[] mainRemovedTests, string[] declarations, string[]? candidateRetainedTests = null, bool omitMap = false,
        bool useContainedBaseline = false)
    {
        var mainRoot = WriteSources();
        var candidateRoot = Path.Combine(_root, "candidate");
        var containedRoot = Path.Combine(_root, "contained");
        string[] candidateTests = [Survivor, .. candidateRetainedTests ?? []];
        string[] mainTests = [Survivor, .. mainRemovedTests];
        if (useContainedBaseline)
        {
            Directory.CreateDirectory(containedRoot);
            var mainSource = File.ReadAllText(Path.Combine(mainRoot, SourceFile));
            File.WriteAllText(Path.Combine(containedRoot, SourceFile), mainSource);
            File.WriteAllText(Path.Combine(mainRoot, SourceFile), mainSource.Replace(FactName, "Observed generation name"));
        }
        var trx = Path.Combine(candidateRoot, "results.trx");
        new XDocument(new XElement("TestRun", new XElement("Results", candidateTests.Select((test, index) =>
            new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", test),
                new XAttribute("outcome", "Passed")))))).Save(trx);
        var evaluator = new AcceptanceStructuralCoverageEvaluator((_, workingDirectory, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                tests = (workingDirectory == mainRoot
                    ? (useContainedBaseline ? [.. mainTests, "New generation fact"] : mainTests)
                    : workingDirectory == containedRoot ? mainTests : candidateTests).Select(test => new
                {
                    displayName = test,
                    location = new { file = Path.Combine(workingDirectory, SourceFile) }
                })
            }))));
        var request = new AcceptanceStructuralCoverageRequest(
            CandidateDiscoveryArguments: ["candidate"], CandidateWorktreePath: candidateRoot,
            DiscoveryTimeout: TimeSpan.FromMinutes(1), BareTestList: false,
            ResolvePartitions: () => [new TestPartitionCoverage("partition", true, [trx])],
            ResolveDeletedTestFiles: () => [], CurrentAttemptId: null, SanctionedRemovedTests: declarations,
            PrepareBaseline: _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(
                new(["main"], mainRoot, mainRoot, false, false)),
            PrepareContainedBaseline: useContainedBaseline ? _ =>
            {
                var contained = new AcceptanceContainedGenerationBaseline("aaaaaaaa", "bbbbbbbb", containedRoot,
                    useObservedBaseline: false, unresolvedReason: null);
                contained.SetBaseline(new AcceptanceStructuralCoverageBaseline(
                    ["contained"], containedRoot, containedRoot, false, false));
                return Task.FromResult(contained);
            } : null);
        var prepared = await evaluator.PrepareAsync(request, CancellationToken.None);
        Assert.NotNull(prepared.BaselineSnapshot!.SourceFilesByTest);
        if (omitMap)
        {
            prepared = prepared with { DeclaredRemovalDisplayNames = null };
        }
        var evaluation = await evaluator.CompareAsync(request, prepared, CancellationToken.None);
        return Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);
    }

    private string WriteSources()
    {
        var mainRoot = Path.Combine(_root, "main");
        var candidateRoot = Path.Combine(_root, "candidate");
        Directory.CreateDirectory(mainRoot);
        Directory.CreateDirectory(candidateRoot);
        File.WriteAllText(Path.Combine(mainRoot, SourceFile), """"
            namespace Namespace;
            class SampleTests
            {
                [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_a_valid_fenced_verdict")]
                public void RemovedFact() { }
                [Xunit.Theory(DisplayName = "Custom verdict cases")]
                public void RemovedTheory(int value) { }
                [FactAttribute(DisplayName = @"Verbatim ""name""")]
                [Xunit.TheoryAttribute(DisplayName = "Second name")]
                public void Literals() { }
                [Fact(DisplayName = nameof(NonLiteral))]
                public void NonLiteral() { }
                [Other(DisplayName = "Not a test")]
                public void UnrelatedAttribute() { }
            }
            class Outer
            {
                class Inner
                {
                    [Fact(DisplayName = "Nested display")]
                    public void RemovedFact() { }
                }
            }
            """");
        // A candidate read would find a different name; only main can corroborate the requested removal.
        File.WriteAllText(Path.Combine(candidateRoot, SourceFile), """
            class SampleTests
            {
                [Fact(DisplayName = "Wrong candidate name")]
                public void RemovedFact() { }
            }
            """);
        return mainRoot;
    }

    private static void AssertCredit(TestCoverageInvariantResult coverage, string identity, int credit) =>
        Assert.Contains($"cross-generation-attribution:source=declared-test-removal,identity={identity}," +
            $"status={(credit > 0 ? "corroborated" : "unmatched")},credit={credit}", coverage.Summary);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
