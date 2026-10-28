using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its temporary reports and starts no processes.
public sealed class NormalizedGateResultStepTests
{
    [Theory]
    [InlineData("trx")]
    [InlineData("go-test-json")]
    public void Apply_ReadableSource_WritesPeerWithSuppliedToolName(string label)
    {
        WithReceipt((receipt, path) =>
        {
            File.WriteAllText(path, Trx);

            var updated = NormalizedGateResultStep.Apply(receipt, path, label);

            var target = Assert.Single(updated.NormalizedTestResultPaths!);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), target);
            Assert.True(File.Exists(target));
            Assert.Null(updated.NormalizedTestResultWriteFailure);
            Assert.Equal(receipt, updated with { NormalizedTestResultPaths = null });
            using var document = JsonDocument.Parse(File.ReadAllText(target));
            Assert.Equal(label, document.RootElement.GetProperty("results")
                .GetProperty("tool").GetProperty("name").GetString());
            var normalized = new NormalizedTestResultSource().Read(target);
            Assert.Equal(TestResultReadStatus.Readable, normalized.Status);
            Assert.Equal(new TrxTestResultSource().Read(path).Records.Select(row => (row.Id, row.Status)),
                normalized.Records.Select(row => (row.Id, row.Status)));
            Assert.Contains(normalized.Records, row => row.Status == TestResultStatus.Failed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_UnreadableSource_RecordsReaderDetailAndPreservesReceipt(bool malformed)
    {
        WithReceipt((receipt, path) =>
        {
            if (malformed) File.WriteAllText(path, "not XML");
            var read = new TrxTestResultSource().Read(path);
            Assert.Equal(malformed ? TestResultReadStatus.Unparseable : TestResultReadStatus.Missing, read.Status);

            var updated = NormalizedGateResultStep.Apply(receipt, path, "trx");

            Assert.Null(updated.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(updated.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.SourceNotReadable, failure.Reason);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), failure.TargetPath);
            Assert.Equal(read.Detail ?? read.Status.ToString(), failure.Detail);
            Assert.Equal(receipt, updated with { NormalizedTestResultWriteFailure = null });
        });
    }

    [Fact]
    public void Apply_ObstructedTarget_RecordsSingleLineFailureAndPreservesReceipt()
    {
        WithReceipt((receipt, path) =>
        {
            File.WriteAllText(path, Trx);
            var target = NormalizedTestResultWriter.PathFor(path);
            Directory.CreateDirectory(target);
            Assert.True(Directory.Exists(target));

            var updated = NormalizedGateResultStep.Apply(receipt, path, "trx");

            Assert.Null(updated.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(updated.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
            Assert.Equal(target, failure.TargetPath);
            Assert.Contains(failure.Detail.Split(':')[0], new[] { nameof(IOException), nameof(UnauthorizedAccessException) });
            Assert.DoesNotContain('\r', failure.Detail);
            Assert.DoesNotContain('\n', failure.Detail);
            Assert.Equal(receipt, updated with { NormalizedTestResultWriteFailure = null });
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        });
    }

    [Fact]
    public void Apply_MixedOutcomes_RetainsPathsAndEveryFailureDiagnostic()
    {
        WithReceipt((receipt, path) =>
        {
            File.WriteAllText(path, Trx);
            var first = NormalizedGateResultStep.Apply(receipt, path, "trx");
            var missing = Path.Combine(Path.GetDirectoryName(path)!, "missing.trx");
            var failed = NormalizedGateResultStep.Apply(first, missing, "trx");
            var firstFailure = Assert.IsType<NormalizedTestResultWriteFailure>(failed.NormalizedTestResultWriteFailure);
            var blocked = Path.Combine(Path.GetDirectoryName(path)!, "blocked.trx");
            File.WriteAllText(blocked, Trx);
            Directory.CreateDirectory(NormalizedTestResultWriter.PathFor(blocked));
            var secondFailure = NormalizedGateResultStep.Apply(receipt, blocked, "trx").NormalizedTestResultWriteFailure!;
            failed = NormalizedGateResultStep.Apply(failed, blocked, "trx");
            var last = Path.Combine(Path.GetDirectoryName(path)!, "last.trx");
            File.WriteAllText(last, Trx);

            var updated = NormalizedGateResultStep.Apply(failed, last, "trx");

            Assert.Equal(new[] { NormalizedTestResultWriter.PathFor(path), NormalizedTestResultWriter.PathFor(last) },
                updated.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(updated.NormalizedTestResultWriteFailure);
            Assert.Equal(firstFailure.TargetPath, failure.TargetPath);
            Assert.Equal(firstFailure.Reason, failure.Reason);
            Assert.StartsWith(firstFailure.Detail, failure.Detail);
            Assert.Contains($"TargetPath={secondFailure.TargetPath}; Reason={secondFailure.Reason}; Detail={secondFailure.Detail}",
                failure.Detail);
            Assert.Equal(receipt, updated with { NormalizedTestResultPaths = null, NormalizedTestResultWriteFailure = null });
        });
    }

    [Fact]
    public void Apply_RetainedDisplayCollision_WritesBothCasesAndRereadsUnchangedData()
    {
        WithReceipt((receipt, path) =>
        {
            File.WriteAllText(path, RetainedCollisionTrx);
            var raw = File.ReadAllBytes(path);

            var updated = NormalizedGateResultStep.Apply(receipt, path, "trx");

            // On the pre-fix adapter this assertion reports the retained repeated-ID write failure.
            Assert.True(updated.NormalizedTestResultWriteFailure is null,
                updated.NormalizedTestResultWriteFailure?.Detail);
            var normalized = new NormalizedTestResultSource().Read(Assert.Single(updated.NormalizedTestResultPaths!));
            Assert.Equal(TestResultReadStatus.Readable, normalized.Status);
            Assert.Equal(2, normalized.Records.Count);
            Assert.Equal(2, normalized.Records.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.EndsWith("2ea3224a-b8e8-7d59-11b6-e71086f18ad0", normalized.Records[0].Id);
            Assert.EndsWith("0a4426d9-e30b-69d7-aa84-1d57e3b04713", normalized.Records[1].Id);
            Assert.All(normalized.Records, row =>
            {
                Assert.False(string.IsNullOrEmpty(row.Id));
                Assert.Equal(RetainedDisplay, row.DisplayName);
                Assert.Equal(TestResultStatus.Passed, row.Status);
                Assert.Equal(new[] { "OwnerExperimentReadingQuestionTests" }, row.SuitePath);
                Assert.Null(row.Message);
                Assert.Null(row.Trace);
                Assert.Empty(row.Attempts);
            });
            Assert.Equal(new long?[] { 213, 58 }, normalized.Records.Select(row => row.DurationMs));
            Assert.Equal(raw, File.ReadAllBytes(path));
            Assert.Equal(receipt, updated with { NormalizedTestResultPaths = null });
        });
    }

    [Fact]
    public void Apply_CollidingMixedStatuses_PreservesEveryOutcomeAndDiagnostic()
    {
        var natives = new[] { "a:<&\"🔥", "a:2", "A:2", " " };
        var outcomes = new[] { "Passed", "Failed", "NotExecuted", "Timeout" };
        var statuses = new[] { TestResultStatus.Passed, TestResultStatus.Failed,
            TestResultStatus.Skipped, TestResultStatus.Error };
        var durations = new long?[] { 1, 2, 3, 4 };
        var rows = natives.Select((native, i) => new XElement("UnitTestResult",
            new XAttribute("testId", native), new XAttribute("testName", "Sample.Tests.Run(value: …)"),
            new XAttribute("outcome", outcomes[i]), new XAttribute("duration", $"00:00:00.00{i + 1}"),
            new XElement("Output", new XElement("ErrorInfo",
                new XElement("Message", "message:" + native), new XElement("StackTrace", "trace:" + native)))));
        var definitions = natives.Where(native => native != " ").Select(native => new XElement("UnitTest",
            new XAttribute("id", native), new XElement("TestMethod",
                new XAttribute("className", "Sample.Tests"), new XAttribute("name", "Run"))));
        WithReceipt((receipt, path) =>
        {
            File.WriteAllText(path, new XElement("TestRun", new XElement("Results", rows),
                new XElement("TestDefinitions", definitions)).ToString());
            var raw = File.ReadAllBytes(path);
            var updated = NormalizedGateResultStep.Apply(receipt, path, "trx");
            Assert.Null(updated.NormalizedTestResultWriteFailure);
            var read = new NormalizedTestResultSource().Read(Assert.Single(updated.NormalizedTestResultPaths!));
            Assert.Equal(TestResultReadStatus.Readable, read.Status);
            Assert.Equal(4, read.Records.Count);
            Assert.Equal(4, read.Records.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
            for (var i = 0; i < natives.Length; i++)
            {
                var row = read.Records[i];
                Assert.Equal("Sample.Tests.Run(value: …)", row.DisplayName);
                Assert.Equal(statuses[i], row.Status);
                Assert.Equal(durations[i], row.DurationMs);
                Assert.Equal("message:" + natives[i], row.Message);
                Assert.Equal("trace:" + natives[i], row.Trace);
                Assert.Equal(i == 3 ? Array.Empty<string>() : new[] { "Sample.Tests" }, row.SuitePath);
                Assert.Equal("trx", row.ReportedBy);
                Assert.Null(row.File);
                Assert.Null(row.Seed);
                Assert.Null(row.OutputRef);
                Assert.Empty(row.Attempts);
            }
            Assert.Equal(raw, File.ReadAllBytes(path));
            Assert.Equal(receipt, updated with { NormalizedTestResultPaths = null });
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("same")]
    public void Apply_AmbiguousNativeCollision_ReportsWriteFailureWithoutChangingVerdict(string? native)
    {
        WithReceipt((receipt, path) =>
        {
            var rows = new[] { "same", native, "distinct" }.Select(id => new XElement("UnitTestResult",
                id is null ? null : new XAttribute("testId", id), new XAttribute("testName", "Cases.Run"),
                new XAttribute("outcome", "Failed")));
            File.WriteAllText(path, new XElement("TestRun", new XElement("Results", rows)).ToString());
            Assert.Equal(3, new TrxTestResultSource().Read(path).Records.Count);
            var raw = File.ReadAllBytes(path);

            var updated = NormalizedGateResultStep.Apply(receipt, path, "trx");

            Assert.Null(updated.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(updated.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
            Assert.Contains("Repeated test Id 'Cases.Run'", failure.Detail);
            Assert.False(File.Exists(NormalizedTestResultWriter.PathFor(path)));
            Assert.Equal(raw, File.ReadAllBytes(path));
            Assert.Equal(receipt, updated with { NormalizedTestResultWriteFailure = null });
        });
    }

    private const string RetainedDisplay = """
        OwnerExperimentReadingQuestionTests.ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion(key: "experiment-reading-due:1a2b3c4d0000000000000000000"···)
        """;

    // Derived from .orchestrator/operator-evidence/trx-collision-20261028/minimal-collision.trx.
    // Candidate aebb2063d01f40a97aec7d46427f56996c657c95; original TRX SHA256
    // 4BAB4F26B039C4F86363C1DE7C39E9389E7A3AB70E986245B577919E4500E905.
    // Receipt: .orchestrator/acceptance-gate-attempts/f54d0284d1d74783b6ff9f68a380643e/
    // f54d0284-0-20261028061302117-072f993b764f4d2789574d26290072ff.result.json.
    // Only irrelevant execution/machine attributes were omitted; native IDs are receipt-scoped.
    private const string RetainedCollisionTrx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="2ea3224a-b8e8-7d59-11b6-e71086f18ad0" testName="OwnerExperimentReadingQuestionTests.ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion(key: &quot;experiment-reading-due:1a2b3c4d0000000000000000000&quot;···)" duration="00:00:00.2133661" outcome="Passed" />
            <UnitTestResult testId="0a4426d9-e30b-69d7-aa84-1d57e3b04713" testName="OwnerExperimentReadingQuestionTests.ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion(key: &quot;experiment-reading-due:1a2b3c4d0000000000000000000&quot;···)" duration="00:00:00.0584351" outcome="Passed" />
          </Results>
          <TestDefinitions>
            <UnitTest id="2ea3224a-b8e8-7d59-11b6-e71086f18ad0" name="OwnerExperimentReadingQuestionTests.ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion(key: &quot;experiment-reading-due:1a2b3c4d0000000000000000000&quot;···)"><TestMethod className="OwnerExperimentReadingQuestionTests" name="ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion" /></UnitTest>
            <UnitTest id="0a4426d9-e30b-69d7-aa84-1d57e3b04713" name="OwnerExperimentReadingQuestionTests.ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion(key: &quot;experiment-reading-due:1a2b3c4d0000000000000000000&quot;···)"><TestMethod className="OwnerExperimentReadingQuestionTests" name="ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion" /></UnitTest>
          </TestDefinitions>
        </TestRun>
        """;

    internal const string Trx = """
        <TestRun>
          <TestDefinitions>
            <UnitTest id="1" name="Sample.Tests.Passes"><TestMethod className="Sample.Tests" name="Passes" /></UnitTest>
            <UnitTest id="2" name="Sample.Tests.Fails"><TestMethod className="Sample.Tests" name="Fails" /></UnitTest>
          </TestDefinitions>
          <Results>
            <UnitTestResult testId="1" testName="Sample.Tests.Passes" outcome="Passed" />
            <UnitTestResult testId="2" testName="Sample.Tests.Fails" outcome="Failed">
              <Output><ErrorInfo><Message>Expected true.</Message><StackTrace>at Sample.Tests.Fails()</StackTrace></ErrorInfo></Output>
            </UnitTestResult>
          </Results>
          <ResultSummary outcome="Failed"><Counters total="2" executed="2" passed="1" failed="1" /></ResultSummary>
        </TestRun>
        """;

    private static void WithReceipt(Action<AcceptanceCheckResult, string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "check.trx");
            var receipt = new AcceptanceCheckResult("gate", false, 1, "failure", root, "broker", "lease", 123,
                ResultSummary: "summary", TestResultPaths: [path], TestResultRunOrdinal: 2,
                FailingTestIdentities: ["Sample.Tests.Fails"], ExecutedTestCount: 2);
            assertion(receipt, path);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
