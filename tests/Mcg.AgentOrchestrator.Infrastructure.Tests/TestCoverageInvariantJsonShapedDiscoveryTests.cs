using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: discovery is an in-memory stub; no filesystem or process is used.
public sealed class TestCoverageInvariantJsonShapedDiscoveryTests
{
    private const string JsonShapedOutput =
        "Microsoft.Testing.Platform v1.8.0\n{\"version\": 2,\n  \"items\": [\"A.B.C\"]\n}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseDiscovery_JsonWithoutSchema_ThrowsTypedError(bool bareTestList)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => TestCoverageInvariant.ParseDiscovery(JsonShapedOutput, bareTestList));

        Assert.Contains("JSON-shaped", exception.Message, StringComparison.Ordinal);
        Assert.Contains("schemaVersion schema marker and a tests array", exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("\"{\"version\": 2,\"", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDiscoveredTests_JsonWithoutSchema_PropagatesTypedError()
    {
        Assert.Throws<InvalidDataException>(
            () => TestCoverageInvariant.ParseDiscovery(JsonShapedOutput, bareTestList: true).Tests);
    }

    [Fact]
    public void ParseDiscovery_LongIndentedJsonLine_QuotesFirst120Characters()
    {
        var expectedPrefix = "{\"" + new string('x', 118);
        var output = "diagnostic\r\n  " + expectedPrefix + "tail\"}\r\n{\"second\": 2}";

        var exception = Assert.Throws<InvalidDataException>(
            () => TestCoverageInvariant.ParseDiscovery(output));

        Assert.EndsWith($"\"{expectedPrefix}\".", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDiscovery_SchemaWithoutTests_ThrowsJsonShapedError()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => TestCoverageInvariant.ParseDiscovery("{\"schemaVersion\": 1}"));

        Assert.Contains("JSON-shaped", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDiscovery_OrdinaryBareList_PreservesIdentitiesAndNullSourceMap()
    {
        var discovery = TestCoverageInvariant.ParseDiscovery(
            "Alpha.Beta.One\nAlpha.Beta.Two\nAlpha.Beta.Three", bareTestList: true);

        Assert.Equal(["Alpha.Beta.One", "Alpha.Beta.Two", "Alpha.Beta.Three"], discovery.Tests);
        Assert.Null(discovery.SourceFilesByTest);
    }

    [Fact]
    public void ParseDiscovery_DiscoveredTestLines_PreservesIdentities()
    {
        var discovery = TestCoverageInvariant.ParseDiscovery(
            "DISCOVERED_TEST: Alpha.Beta.One\nDISCOVERED_TEST: Alpha.Beta.Two");

        Assert.Equal(["Alpha.Beta.One", "Alpha.Beta.Two"], discovery.Tests);
        Assert.Null(discovery.SourceFilesByTest);
    }

    [Fact]
    public void ParseDiscovery_StructuredJson_PreservesIdentitiesAndSourceMap()
    {
        var discovery = TestCoverageInvariant.ParseDiscovery(
            "{\"schemaVersion\":1,\"tests\":[{\"uid\":\"u1\",\"displayName\":\"Alpha.Beta.One\"}]}",
            bareTestList: true);

        Assert.Equal(["Alpha.Beta.One"], discovery.Tests);
        Assert.NotNull(discovery.SourceFilesByTest);
        Assert.Empty(discovery.SourceFilesByTest);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{ \"version\": 2}")]
    [InlineData("[\"A.B.C\"]")]
    public void ParseDiscovery_OtherJsonPrefixes_KeepBareListBehavior(string output)
    {
        var discovery = TestCoverageInvariant.ParseDiscovery(output, bareTestList: true);

        Assert.Equal([output], discovery.Tests);
        Assert.Null(discovery.SourceFilesByTest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseDiscovery_EmptyOutput_PreservesEmptySnapshot(bool bareTestList)
    {
        var discovery = TestCoverageInvariant.ParseDiscovery("", bareTestList);

        Assert.Empty(discovery.Tests);
        Assert.Null(discovery.SourceFilesByTest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluate_JsonShapedDiscovery_PropagatesTypedError(bool baseline)
    {
        var discoveryPaths = new List<string>();
        var evaluator = new AcceptanceStructuralCoverageEvaluator(
            (arguments, workingDirectory, timeout, cancellationToken) =>
            {
                discoveryPaths.Add(workingDirectory);
                var output = baseline && workingDirectory == "candidate"
                    ? "DISCOVERED_TEST: A.B.C"
                    : JsonShapedOutput;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, output));
            },
            GoalAcceptanceVerifierBuildArtifactLock.IsBuildArtifactIoException);
        var request = new AcceptanceStructuralCoverageRequest(
            CandidateDiscoveryArguments: ["discover"],
            CandidateWorktreePath: "candidate",
            DiscoveryTimeout: TimeSpan.FromMinutes(1),
            BareTestList: true,
            ResolvePartitions: () => [],
            ResolveDeletedTestFiles: () => [],
            CurrentAttemptId: null,
            SanctionedRemovedTests: [],
            PrepareBaseline: _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(
                baseline ? new AcceptanceStructuralCoverageBaseline(
                    ["discover-baseline"], "baseline", "baseline", true, false) : null));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => evaluator.EvaluateAsync(request, CancellationToken.None));

        Assert.Contains("{\"version\": 2,", exception.Message, StringComparison.Ordinal);
        Assert.Equal(baseline ? new[] { "candidate", "baseline" } : ["candidate"], discoveryPaths);
    }
}
