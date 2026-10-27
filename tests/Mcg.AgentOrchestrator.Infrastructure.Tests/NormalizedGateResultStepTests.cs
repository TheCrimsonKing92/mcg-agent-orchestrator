using System.Text.Json;
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
