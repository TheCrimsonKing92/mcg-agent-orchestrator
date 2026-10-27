using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;

// Parallel-safe: each test owns its report directory and no process or global state is changed.
public sealed class TrxTestResultSourceTests
{
    [Fact]
    public void Read_GateReport_PreservesRowsAndMetadata()
    {
        WithReport(GateReport, path =>
        {
            ITestResultSource source = new TrxTestResultSource();
            var result = source.Read(path);
            Assert.Equal("trx", source.ReportedBy);
            Assert.Equal(path, result.Path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            Assert.Null(result.Detail);
            Assert.Equal(new[]
            {
                "Sample.Tests.ParserTests.Parses_valid_row", "Sample.Tests.ParserTests.Parses_case(value: 2)",
                "Sample.Tests.ParserTests.Parses_case(value: 3)", "Sample.Tests.ParserTests.Skips_on_linux",
                "Sample.Tests.ParserTests.Times_out"
            }, result.Records.Select(record => record.Id));
            Assert.Equal(new[] { TestResultStatus.Passed, TestResultStatus.Failed, TestResultStatus.Passed,
                TestResultStatus.Skipped, TestResultStatus.Error }, result.Records.Select(record => record.Status));
            Assert.Equal(new long?[] { 125, 40, 10, null, 120000 }, result.Records.Select(record => record.DurationMs));
            Assert.Equal("Assert.Equal() Failure: expected 2, actual 3", result.Records[1].Message);
            Assert.Equal("at Sample.Tests.ParserTests.Parses_case(Int32 value) in ParserTests.cs:line 18",
                result.Records[1].Trace);
            Assert.All(result.Records, record =>
            {
                Assert.Equal("trx", record.ReportedBy);
                Assert.Equal(new[] { "Sample.Tests.ParserTests" }, record.SuitePath);
                Assert.Equal(record.Id, record.DisplayName);
                Assert.Null(record.File);
                Assert.Empty(record.Attempts);
                Assert.Null(record.Seed);
                Assert.Null(record.OutputRef);
            });
        });
    }

    [Fact]
    public void Read_GateReport_MatchesExistingFailureViewsAndDetectsChangedOutcome()
    {
        WithReport(GateReport, path =>
        {
            var records = new TrxTestResultSource().Read(path).Records;
            var failures = AcceptanceTrxFailureReader.Read(path);
            Assert.Equal(AcceptanceTrxReadStatus.Readable, failures.Status);
            Assert.Equal(3, failures.Failures.Count);
            Assert.Empty(FailureDifference(records, failures.Failures));
            Assert.Equal(records.Where(record => record.Status is TestResultStatus.Failed or TestResultStatus.Error)
                    .Select(record => record.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                AcceptanceTrxTestIdentityResolver.ExtractTrxFailureIdentities(path).Order(StringComparer.Ordinal));

            WithReport(GateReport.Replace("outcome=\"Failed\"", "outcome=\"Passed\"", StringComparison.Ordinal), changed =>
                Assert.Equal(new[] { "Sample.Tests.ParserTests.Parses_case(value: 2)" },
                    FailureDifference(records, AcceptanceTrxFailureReader.Read(changed).Failures)));
        });
    }

    [Fact]
    public void Read_MissingMalformedAndLockedReports_ReturnAtomicFailures()
    {
        WithReport("not xml", path =>
        {
            var source = new TrxTestResultSource();
            var missing = source.Read(path + ".missing");
            Assert.Equal(TestResultReadStatus.Missing, missing.Status);
            Assert.Empty(missing.Records);
            var malformed = source.Read(path);
            Assert.Equal(TestResultReadStatus.Unparseable, malformed.Status);
            Assert.Empty(malformed.Records);
            Assert.False(string.IsNullOrEmpty(malformed.Detail));
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unreadable = source.Read(path);
            Assert.Equal(TestResultReadStatus.Unreadable, unreadable.Status);
            Assert.Empty(unreadable.Records);
        });
    }

    [Fact]
    public void Read_RepeatedRowsAndFallbackIdentities_AreNotGrouped()
    {
        WithReport("""
            <TestRun><Results>
              <UnitTestResult testId="opaque" outcome="passed" duration="00:00:00.0005000" />
              <UnitTestResult testId="opaque" outcome="Inconclusive" duration="invalid" />
              <UnitTestResult testId="" />
              <UnitTestResult outcome="Warning" testName="display" />
            </Results></TestRun>
            """, path =>
        {
            var result = new TrxTestResultSource().Read(path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            Assert.Equal(new[] { "opaque", "opaque", "unknown test", "display" }, result.Records.Select(r => r.Id));
            Assert.Equal(new[] { TestResultStatus.Passed, TestResultStatus.Skipped, TestResultStatus.Error,
                TestResultStatus.Error }, result.Records.Select(r => r.Status));
            Assert.Equal(1L, result.Records[0].DurationMs);
            Assert.Null(result.Records[1].DurationMs);
            Assert.All(result.Records, record => Assert.Empty(record.SuitePath));
            Assert.Equal("opaque", result.Records[0].DisplayName);
        });
        WithReport("<TestRun />", path =>
        {
            var result = new TrxTestResultSource().Read(path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            Assert.Empty(result.Records);
        });
    }

    private static string[] FailureDifference(IReadOnlyList<TestResult> records, IReadOnlyList<AcceptanceTrxFailure> failures)
    {
        var difference = records.Where(record => record.Status != TestResultStatus.Passed)
            .Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
        difference.SymmetricExceptWith(failures.Select(failure => failure.TestName).OfType<string>());
        return difference.Order(StringComparer.Ordinal).ToArray();
    }

    private static void WithReport(string text, Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "report.trx");
            File.WriteAllText(path, text);
            assertion(path);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private const string GateReport = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="00000000-0000-0000-0000-0000000000aa" name="gate" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="00000000-0000-0000-0000-000000000001" testName="Sample.Tests.ParserTests.Parses_valid_row" outcome="Passed" duration="00:00:00.1250000" />
            <UnitTestResult testId="00000000-0000-0000-0000-000000000002" testName="Sample.Tests.ParserTests.Parses_case(value: 2)" outcome="Failed" duration="00:00:00.0400000">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure: expected 2, actual 3</Message>
                  <StackTrace>at Sample.Tests.ParserTests.Parses_case(Int32 value) in ParserTests.cs:line 18</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testId="00000000-0000-0000-0000-000000000003" testName="Sample.Tests.ParserTests.Parses_case(value: 3)" outcome="Passed" duration="00:00:00.0100000" />
            <UnitTestResult testId="00000000-0000-0000-0000-000000000004" testName="Sample.Tests.ParserTests.Skips_on_linux" outcome="NotExecuted" />
            <UnitTestResult testId="00000000-0000-0000-0000-000000000005" testName="Sample.Tests.ParserTests.Times_out" outcome="Timeout" duration="00:02:00.0000000" />
          </Results>
          <TestDefinitions>
            <UnitTest id="00000000-0000-0000-0000-000000000001" name="Sample.Tests.ParserTests.Parses_valid_row" storage="sample.tests.dll"><TestMethod codeBase="sample.tests.dll" className="Sample.Tests.ParserTests" name="Parses_valid_row" /></UnitTest>
            <UnitTest id="00000000-0000-0000-0000-000000000002" name="Sample.Tests.ParserTests.Parses_case(value: 2)" storage="sample.tests.dll"><TestMethod codeBase="sample.tests.dll" className="Sample.Tests.ParserTests" name="Parses_case" /></UnitTest>
            <UnitTest id="00000000-0000-0000-0000-000000000003" name="Sample.Tests.ParserTests.Parses_case(value: 3)" storage="sample.tests.dll"><TestMethod codeBase="sample.tests.dll" className="Sample.Tests.ParserTests" name="Parses_case" /></UnitTest>
            <UnitTest id="00000000-0000-0000-0000-000000000004" name="Sample.Tests.ParserTests.Skips_on_linux" storage="sample.tests.dll"><TestMethod codeBase="sample.tests.dll" className="Sample.Tests.ParserTests" name="Skips_on_linux" /></UnitTest>
            <UnitTest id="00000000-0000-0000-0000-000000000005" name="Sample.Tests.ParserTests.Times_out" storage="sample.tests.dll"><TestMethod codeBase="sample.tests.dll" className="Sample.Tests.ParserTests" name="Times_out" /></UnitTest>
          </TestDefinitions>
        </TestRun>
        """;
}
