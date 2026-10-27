using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: every test owns its raw stream, root without go.mod and synthetic receipt.
public sealed class GoTestJsonTestResultSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_TopLevelStreams_MatchGateCountersFailuresAndTrx(bool failed)
    {
        var stream = failed ? GoTestJsonGateReceiptTests.StreamB : GoTestJsonGateReceiptTests.StreamA;
        WithReport(stream, (root, path) =>
        {
            Assert.False(File.Exists(Path.Combine(root, "go.mod")));
            ITestResultSource source = new GoTestJsonTestResultSource();
            var result = source.Read(path);
            Assert.Equal("go-test-json", source.ReportedBy);
            Assert.Equal(path, result.Path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            Assert.Equal(new[]
            {
                "example.com/schemadrift/cmd/schemadrift.TestStdoutIdempotency",
                "example.com/schemadrift/cmd/schemadrift.TestStdoutWithJSONOutput",
                "example.com/schemadrift/cmd/schemadrift.TestStderrSeparation",
                "example.com/schemadrift/internal/drift.TestMissingColumn"
            }, result.Records.Select(record => record.Id));
            Assert.All(result.Records, record =>
            {
                Assert.Equal("go-test-json", record.ReportedBy);
                Assert.Equal(record.Id, record.DisplayName);
                var package = Assert.Single(record.SuitePath);
                Assert.StartsWith(package + ".", record.Id, StringComparison.Ordinal);
                Assert.DoesNotContain("/", record.Id[(package.Length + 1)..]);
                Assert.False(record.Id.EndsWith(".TestLargeCorpus", StringComparison.Ordinal));
                Assert.Null(record.File);
                Assert.Null(record.DurationMs);
                Assert.Null(record.Message);
                Assert.Null(record.Trace);
                Assert.Empty(record.Attempts);
                Assert.Null(record.Seed);
                Assert.Null(record.OutputRef);
            });
            var failedIds = result.Records.Where(record => record.Status == TestResultStatus.Failed)
                .Select(record => record.Id).ToArray();
            Assert.Equal(failed ? new[] { "example.com/schemadrift/cmd/schemadrift.TestStdoutWithJSONOutput" }
                : Array.Empty<string>(), failedIds);
            Assert.All(result.Records.Where(record => !failedIds.Contains(record.Id)),
                record => Assert.Equal(TestResultStatus.Passed, record.Status));

            var check = new AcceptanceManifestCheck
            {
                Name = "go test", Type = "command", Command = "go", Arguments = ["test", "-count=1", "./..."]
            };
            var attached = GoTestJsonGateReceipt.Attach(check, ["go", "test", "-json", "-count=1", "./..."],
                new(failed ? 1 : 0, stream), root, Path.Combine(root, "attempt"), value => value,
                new("go test", !failed, failed ? 1 : 0, failed ? "raw" : null));
            Assert.Equal(result.Records.Count, attached.ExecutedTestCount);
            Assert.Equal(failedIds, attached.FailingTestIdentities);
            var trx = new TrxTestResultSource().Read(Assert.Single(attached.TestResultPaths!));
            Assert.Equal(TestResultReadStatus.Readable, trx.Status);
            Assert.Equal(result.Records.Select(record => (record.Id, record.Status)).OrderBy(pair => pair.Id, StringComparer.Ordinal),
                trx.Records.Select(record => (record.Id, record.Status)).OrderBy(pair => pair.Id, StringComparer.Ordinal));
        });
    }

    [Fact]
    public void Read_BuildFailureAndMissingPath_DoNotInventPassedTests()
    {
        WithReport(BuildFailure, (_, path) =>
        {
            var source = new GoTestJsonTestResultSource();
            var result = source.Read(path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            Assert.Empty(result.Records);
            Assert.DoesNotContain(result.Records, record => record.Status == TestResultStatus.Passed);
            var missing = source.Read(path + ".missing");
            Assert.Equal(TestResultReadStatus.Missing, missing.Status);
            Assert.Empty(missing.Records);
        });
    }

    [Fact]
    public void Read_MixedEvents_FailureWinsOverLaterPass()
    {
        var stream = "{\"Output\":\"first\\n\"}\r\nplain stderr\r\n[1,2]\n{broken}\n" +
            "{\"Package\":42,\"Test\":\"Invalid\",\"Action\":\"pass\"}\n" +
            "{\"Package\":\"example.com/schemadrift/cmd/schemadrift\",\"Test\":\"TestOne\",\"Action\":\"fail\"}\n" +
            "{\"Package\":\"example.com/schemadrift/cmd/schemadrift\",\"Test\":\"TestOne\",\"Action\":\"pass\",\"Output\":\"last\"}";
        WithReport(stream, (_, path) =>
        {
            var result = new GoTestJsonTestResultSource().Read(path);
            Assert.Equal(TestResultReadStatus.Readable, result.Status);
            var record = Assert.Single(result.Records);
            Assert.Equal("example.com/schemadrift/cmd/schemadrift.TestOne", record.Id);
            Assert.Equal(TestResultStatus.Failed, record.Status);
        });
    }

    [Fact]
    public void Read_LockedReport_ReturnsUnreadable()
    {
        WithReport(GoTestJsonGateReceiptTests.StreamA, (_, path) =>
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = new GoTestJsonTestResultSource().Read(path);
            Assert.Equal(TestResultReadStatus.Unreadable, result.Status);
            Assert.Empty(result.Records);
        });
    }

    private static void WithReport(string stream, Action<string, string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "go-test.jsonl");
            File.WriteAllText(path, stream);
            assertion(root, path);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private const string BuildFailure = """
        {"ImportPath":"example.com/schemadrift/cmd/schemadrift [example.com/schemadrift/cmd/schemadrift.test]","Action":"build-output","Output":"cmd/schemadrift/main_test.go:9:2: undefined: missingHelper\n"}
        {"ImportPath":"example.com/schemadrift/cmd/schemadrift [example.com/schemadrift/cmd/schemadrift.test]","Action":"build-fail"}
        {"Time":"2026-10-24T15:03:55.1Z","Action":"start","Package":"example.com/schemadrift/cmd/schemadrift"}
        {"Time":"2026-10-24T15:03:55.2Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Output":"FAIL\texample.com/schemadrift/cmd/schemadrift [build failed]\n"}
        {"Time":"2026-10-24T15:03:55.2Z","Action":"fail","Package":"example.com/schemadrift/cmd/schemadrift","Elapsed":0,"FailedBuild":"example.com/schemadrift/cmd/schemadrift [example.com/schemadrift/cmd/schemadrift.test]"}
        """;
}
