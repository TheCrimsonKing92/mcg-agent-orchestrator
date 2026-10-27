using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: committed fixtures are read-only and each generated report has its own temp directory.
public sealed class NormalizedTestResultSourceTests
{
    [Fact]
    public void Read_MixedFixture_PreservesOrderIdentitiesAndMetadata()
    {
        ITestResultSource source = new NormalizedTestResultSource();
        var path = FixturePath("valid-mixed.json");
        var result = source.Read(path);
        Assert.Equal("normalized", source.ReportedBy);
        Assert.Equal(path, result.Path);
        Assert.Equal(TestResultReadStatus.Readable, result.Status);
        Assert.Null(result.Detail);
        Assert.Equal(new[]
        {
            "tests/test_parse.py::test_ok", "tests/test_parse.py::test_case[x1-y2]",
            "tests/test_parse.py::test_case[x2-y3]", "tests/test_slow.py::test_big", "tests/test_io.py::test_crash"
        }, result.Records.Select(record => record.Id));
        Assert.Equal(new[] { "test_ok", "test_case[x1-y2]", "test_case[x2-y3]", "test_big", "test_crash" },
            result.Records.Select(record => record.DisplayName));
        Assert.Equal(new[] { TestResultStatus.Passed, TestResultStatus.Failed, TestResultStatus.Passed,
            TestResultStatus.Skipped, TestResultStatus.Error }, result.Records.Select(record => record.Status));
        Assert.Equal(new[] { "tests/test_parse.py", "tests/test_parse.py", "tests/test_parse.py",
            "tests/test_slow.py", "tests/test_io.py" }, result.Records.Select(record => record.File));
        Assert.Equal(5, result.Records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12L, result.Records[0].DurationMs);
        Assert.Equal(new[] { "tests/test_parse.py" }, result.Records[0].SuitePath);
        Assert.Equal("Expected x1, received y2", result.Records[1].Message);
        Assert.Equal("tests/test_parse.py:18: AssertionError", result.Records[1].Trace);
        Assert.All(result.Records, record => Assert.Equal("pytest-junit", record.ReportedBy));
        Assert.All(result.Records.Skip(1), record => Assert.Empty(record.SuitePath));
        Assert.All(result.Records, record => Assert.Empty(record.Attempts));
    }

    [Fact]
    public void Read_RetryFixture_PreservesAttemptAndOverridesToolProvenance()
    {
        var result = new NormalizedTestResultSource().Read(FixturePath("valid-retry.json"));
        Assert.Equal(TestResultReadStatus.Readable, result.Status);
        var record = Assert.Single(result.Records);
        Assert.Equal(TestResultStatus.Passed, record.Status);
        var attempt = Assert.Single(record.Attempts);
        Assert.Equal(TestResultStatus.Failed, attempt.Status);
        Assert.Equal(30L, attempt.DurationMs);
        Assert.Equal("boom", attempt.Message);
        Assert.Null(attempt.Trace);
        Assert.Equal("8431", record.Seed);
        Assert.Equal("vitest-junit", record.ReportedBy);
    }

    [Fact]
    public void Read_EmptyFixture_IsReadable()
    {
        var result = new NormalizedTestResultSource().Read(FixturePath("valid-empty.json"));
        Assert.Equal(TestResultReadStatus.Readable, result.Status);
        Assert.Empty(result.Records);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData("invalid-duplicate-id.json", "/results/tests/1/testId")]
    [InlineData("invalid-status.json", "/results/tests/0/status")]
    [InlineData("invalid-missing-name.json", "/results/tests/0/name")]
    public void Read_InvalidFixture_ReportsExactPointerAndNoPartialRecords(string name, string pointer)
    {
        AssertInvalid(new NormalizedTestResultSource().Read(FixturePath(name)), pointer);
    }

    [Fact]
    public void Read_MissingMalformedAndLockedReports_ClassifiesReadFailure()
    {
        WithReport("{", path =>
        {
            var source = new NormalizedTestResultSource();
            var missing = source.Read(path + ".missing");
            Assert.Equal(TestResultReadStatus.Missing, missing.Status);
            Assert.Empty(missing.Records);
            var malformed = source.Read(path);
            Assert.Equal(TestResultReadStatus.Unparseable, malformed.Status);
            Assert.Empty(malformed.Records);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var unreadable = source.Read(path);
            Assert.Equal(TestResultReadStatus.Unreadable, unreadable.Status);
            Assert.Empty(unreadable.Records);
        });
    }

    [Theory]
    [InlineData("null", "")]
    [InlineData("{}", "/reportFormat")]
    [InlineData("{\"reportFormat\":false,\"schemaVersion\":0,\"results\":{}}", "/reportFormat")]
    [InlineData("{\"reportFormat\":\"CTRF\",\"schemaVersion\":0,\"results\":{}}", "/schemaVersion")]
    [InlineData("{\"reportFormat\":\"CTRF\",\"schemaVersion\":\"mcg-test-result/1\",\"results\":null}", "/results")]
    [InlineData("{\"reportFormat\":\"CTRF\",\"schemaVersion\":\"mcg-test-result/1\",\"results\":{\"tests\":[]}}", "/results/tool")]
    [InlineData("{\"reportFormat\":\"CTRF\",\"schemaVersion\":\"mcg-test-result/1\",\"results\":{\"tool\":{\"name\":\"\"},\"tests\":[]}}", "/results/tool/name")]
    [InlineData("{\"reportFormat\":\"CTRF\",\"schemaVersion\":\"mcg-test-result/1\",\"results\":{\"tool\":{\"name\":\"tool\"},\"tests\":{}}}", "/results/tests")]
    public void Read_InvalidReportStructure_ReportsFirstViolation(string json, string pointer)
    {
        WithReport(json, path => AssertInvalid(new NormalizedTestResultSource().Read(path), pointer));
    }

    [Theory]
    [InlineData("\"testId\":\"\"", "testId")]
    [InlineData("\"name\":null", "name")]
    [InlineData("\"status\":\"other\"", "status")]
    [InlineData("\"duration\":null", "duration")]
    [InlineData("\"duration\":-0.1", "duration")]
    [InlineData("\"duration\":-1e-1000", "duration")]
    [InlineData("\"duration\":9223372036854775807.5", "duration")]
    [InlineData("\"duration\":1e1000", "duration")]
    [InlineData("\"filePath\":null", "filePath")]
    [InlineData("\"suite\":null", "suite")]
    [InlineData("\"suite\":[\"ok\",false]", "suite/1")]
    [InlineData("\"message\":null", "message")]
    [InlineData("\"trace\":42", "trace")]
    [InlineData("\"retryAttempts\":null", "retryAttempts")]
    [InlineData("\"retryAttempts\":[{}]", "retryAttempts/0/status")]
    [InlineData("\"retryAttempts\":[{\"status\":\"pending\"}]", "retryAttempts/0/status")]
    [InlineData("\"retryAttempts\":[{\"status\":\"failed\",\"duration\":9223372036854775808}]", "retryAttempts/0/duration")]
    [InlineData("\"retryAttempts\":[{\"status\":\"failed\",\"message\":null}]", "retryAttempts/0/message")]
    [InlineData("\"retryAttempts\":[{\"status\":\"failed\",\"trace\":false}]", "retryAttempts/0/trace")]
    [InlineData("\"extra\":null", "extra")]
    [InlineData("\"extra\":{\"seed\":1}", "extra/seed")]
    [InlineData("\"extra\":{\"outputRef\":false}", "extra/outputRef")]
    [InlineData("\"extra\":{\"reportedBy\":null}", "extra/reportedBy")]
    public void Read_InvalidKnownProperty_ReportsMemberPointer(string member, string pointer)
    {
        // Last-occurrence semantics allow this table to replace a required property too.
        var test = "{\"testId\":\"id\",\"name\":\"test\",\"status\":\"passed\"," + member + "}";
        WithReport(Report(test), path => AssertInvalid(new NormalizedTestResultSource().Read(path),
            "/results/tests/0/" + pointer));
    }

    [Fact]
    public void Read_OptionalMetadataAndOpaqueIdentities_PreserveValuesAndIgnoreUnknownMembers()
    {
        var tests = """
            {"testId":"id","name":"first","status":"passed","duration":1.5,"suite":["a","b"],
             "extra":{"seed":"7","outputRef":"logs/output.txt","unknown":null},
             "retryAttempts":[{"status":"error","duration":2.5,"trace":"retry trace","unknown":null}],"unknown":null},
            {"testId":"ID","name":"second","status":"passed","duration":9223372036854775807},
            {"testId":" id ","name":"third","status":"passed","extra":{"reportedBy":""}}
            """;
        WithReport(Report(tests), path =>
        {
            var result = new NormalizedTestResultSource().Read(path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            Assert.Equal(new[] { "id", "ID", " id " }, result.Records.Select(record => record.Id));
            var first = result.Records[0];
            Assert.Equal(2L, first.DurationMs);
            Assert.Equal(new[] { "a", "b" }, first.SuitePath);
            Assert.Equal("7", first.Seed);
            Assert.Equal("logs/output.txt", first.OutputRef);
            Assert.Equal("tool", first.ReportedBy);
            Assert.Equal(new TestResultAttempt(TestResultStatus.Error, 3, null, "retry trace"), Assert.Single(first.Attempts));
            Assert.Equal(long.MaxValue, result.Records[1].DurationMs);
            Assert.Equal("", result.Records[2].ReportedBy);
            Assert.Null(result.Records[2].DurationMs);
            Assert.Null(result.Records[2].Message);
            Assert.Null(result.Records[2].Trace);
            Assert.Null(result.Records[2].Seed);
            Assert.Null(result.Records[2].OutputRef);
            Assert.Null(result.Records[2].File);
            Assert.Empty(result.Records[2].SuitePath);
            Assert.Empty(result.Records[2].Attempts);
        });
    }

    [Fact]
    public void Read_MultipleViolations_UsesRequiredOrderThenSchemaOrder()
    {
        WithReport(Report("{\"status\":null}"), path =>
            AssertInvalid(new NormalizedTestResultSource().Read(path), "/results/tests/0/testId"));
        WithReport(Report("{\"trace\":null,\"status\":\"pending\",\"name\":\"n\",\"testId\":\"id\"}"), path =>
            AssertInvalid(new NormalizedTestResultSource().Read(path), "/results/tests/0/status"));
    }

    private static void AssertInvalid(TestResultReadResult result, string pointer)
    {
        Assert.Equal(TestResultReadStatus.Invalid, result.Status);
        Assert.Equal(pointer, result.Detail);
        Assert.Empty(result.Records);
    }

    private static string Report(string tests) =>
        "{\"reportFormat\":\"CTRF\",\"schemaVersion\":\"mcg-test-result/1\",\"unknown\":null," +
        "\"results\":{\"tool\":{\"name\":\"tool\",\"unknown\":null},\"tests\":[" + tests + "],\"unknown\":null}}";

    private static string FixturePath(string name, [CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root))
            return Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "TestResultRecords", name);
        return Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Fixtures", "TestResultRecords", name);
    }

    private static void WithReport(string json, Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "report.json");
            File.WriteAllText(path, json);
            assertion(path);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
