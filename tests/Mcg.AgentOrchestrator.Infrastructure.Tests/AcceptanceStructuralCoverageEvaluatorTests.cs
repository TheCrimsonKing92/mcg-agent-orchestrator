using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceStructuralCoverageEvaluatorTests : IDisposable
{
    private const string PassedTest = "Namespace.SampleTests.Passes";
    private const string MissingTest = "Namespace.SampleTests.Missing";
    private readonly string _root = CreateTempDirectory();
    private int _discoveryInvocations;

    [Fact]
    public async Task Evaluate_DiscoveredEqualsExecuted_Passes()
    {
        var trxPath = WriteTrx(
            "equal.trx",
            [TestResult("passed", PassedTest, "Passed")],
            executed: 1);

        var evaluation = await EvaluateAsync([PassedTest], trxPath);

        var coverage = Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);
        Assert.True(coverage.Passed, coverage.Summary);
        Assert.Equal(
            "structural coverage complete: discovered=1, executed=1, recorded=1, partitions=1",
            coverage.Summary);
        Assert.Equal(1, _discoveryInvocations);
    }

    [Fact]
    public async Task Evaluate_DiscoveredTestMissing_FailsWithTestName()
    {
        var trxPath = WriteTrx(
            "missing.trx",
            [TestResult("passed", PassedTest, "Passed")],
            executed: 1);

        var evaluation = await EvaluateAsync([PassedTest, MissingTest], trxPath);

        var coverage = Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);
        Assert.False(coverage.Passed);
        Assert.Equal([MissingTest], coverage.MissingTests);
        Assert.Equal(
            "structural coverage failed: discovered=2, executed=1, recorded=1, missing=1, emptyPartitions=0",
            coverage.Summary);
        Assert.Equal(1, _discoveryInvocations);
    }

    [Fact]
    public async Task Evaluate_ZeroExecutedTests_FailsCoverage()
    {
        var trxPath = WriteTrx("zero.trx", results: [], executed: 0);

        var evaluation = await EvaluateAsync([PassedTest], trxPath);

        var coverage = Assert.IsType<TestCoverageInvariantResult>(evaluation.Coverage);
        Assert.False(coverage.Passed);
        Assert.Equal(AcceptanceFailureClassifications.StructuralCoverageFailed, coverage.FailureClassification);
        Assert.Equal(["partition"], coverage.EmptyPartitions);
        Assert.Equal([PassedTest], coverage.MissingTests);
        Assert.Contains("executed=0", coverage.Summary, StringComparison.Ordinal);
        Assert.Equal(1, _discoveryInvocations);
    }

    [Fact]
    public async Task Evaluate_TruncatedTrx_ReportsMalformedReceipt()
    {
        var trxPath = Path.Combine(_root, "truncated.trx");
        File.WriteAllText(trxPath, "<TestRun><Results>");

        var exception = await Assert.ThrowsAsync<System.Xml.XmlException>(
            () => EvaluateAsync([PassedTest], trxPath));

        Assert.Contains("Unexpected end of file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _discoveryInvocations);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<AcceptanceStructuralCoverageEvaluation> EvaluateAsync(
        IReadOnlyList<string> discoveredTests,
        string trxPath)
    {
        var evaluator = new AcceptanceStructuralCoverageEvaluator(
            (arguments, workingDirectory, timeout, cancellationToken) =>
            {
                _discoveryInvocations++;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    string.Join(
                        Environment.NewLine,
                        discoveredTests.Select(test => $"DISCOVERED_TEST: {test}"))));
            });
        var request = new AcceptanceStructuralCoverageRequest(
            CandidateDiscoveryArguments: ["discover"],
            CandidateWorktreePath: _root,
            DiscoveryTimeout: TimeSpan.FromMinutes(1),
            BareTestList: false,
            ResolvePartitions: () =>
            [
                new TestPartitionCoverage(
                    "partition",
                    Completed: true,
                    TestResultPaths: [trxPath])
            ],
            ResolveDeletedTestFiles: () => [],
            CurrentAttemptId: null,
            SanctionedRemovedTests: [],
            PrepareBaseline: _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(null));

        return await evaluator.EvaluateAsync(request, CancellationToken.None);
    }

    private string WriteTrx(
        string fileName,
        IReadOnlyList<string> results,
        int executed)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(
            path,
            $"""
            <TestRun>
              <TestDefinitions>
                {string.Join(Environment.NewLine, results.Select(ResultDefinition))}
              </TestDefinitions>
              <Results>
                {string.Join(Environment.NewLine, results)}
              </Results>
              <ResultSummary>
                <Counters total="{results.Count}" executed="{executed}" passed="{executed}" />
              </ResultSummary>
            </TestRun>
            """);
        return path;
    }

    private static string TestResult(string id, string testName, string outcome) =>
        $"<UnitTestResult testId=\"{id}\" testName=\"{testName}\" outcome=\"{outcome}\" />";

    private static string ResultDefinition(string result)
    {
        var id = ReadAttribute(result, "testId");
        var testName = ReadAttribute(result, "testName");
        var separator = testName.LastIndexOf('.');
        return $"""
               <UnitTest id="{id}" name="{testName}">
                 <TestMethod className="{testName[..separator]}" name="{testName[(separator + 1)..]}" />
               </UnitTest>
               """;
    }

    private static string ReadAttribute(string element, string name)
    {
        var prefix = $"{name}=\"";
        var start = element.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = element.IndexOf('"', start);
        return element[start..end];
    }
}
