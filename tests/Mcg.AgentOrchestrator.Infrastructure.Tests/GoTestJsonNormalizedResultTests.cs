using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: Go sources and reports live in a unique temporary directory; no processes are started.
public sealed class GoTestJsonNormalizedResultTests
{
    private const string FailedIdentity = "example.com/schemadrift/cmd/schemadrift.TestStdoutWithJSONOutput";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Attach_TopLevelRows_WritesNormalizedPeerWithTrxOutcomes(bool failed)
    {
        WithRoot(root =>
        {
            var receipt = Attach(root, failed);

            var target = Assert.Single(receipt.NormalizedTestResultPaths!);
            var trxPath = Assert.Single(receipt.TestResultPaths!);
            Assert.Equal(Path.Combine(root, "attempt.go-test.ctrf.json"), target);
            Assert.Equal(Path.Combine(root, "attempt.go-test.trx"), trxPath);
            Assert.Equal(NormalizedTestResultWriter.PathFor(trxPath), target);
            Assert.True(File.Exists(target));
            Assert.Null(receipt.NormalizedTestResultWriteFailure);
            var normalized = new NormalizedTestResultSource().Read(target);
            var trx = new TrxTestResultSource().Read(trxPath);
            Assert.Equal(TestResultReadStatus.Readable, normalized.Status);
            Assert.Equal(TestResultReadStatus.Readable, trx.Status);
            Assert.Equal(4, normalized.Records.Count);
            Assert.Equal(trx.Records.Select(record => (record.Id, record.Status)),
                normalized.Records.Select(record => (record.Id, record.Status)));
            var failing = normalized.Records.Where(record => record.Status == TestResultStatus.Failed)
                .Select(record => record.Id).ToArray();
            Assert.Equal(failed ? new[] { FailedIdentity } : Array.Empty<string>(), failing);
            Assert.Equal(4, receipt.ExecutedTestCount);
            Assert.Equal(failing, receipt.FailingTestIdentities);
            Assert.Equal(!failed, receipt.Passed);
            Assert.Equal(failed ? 1 : 0, receipt.ExitCode);
            using var document = JsonDocument.Parse(File.ReadAllText(target));
            Assert.Equal("go-test-json", document.RootElement.GetProperty("results")
                .GetProperty("tool").GetProperty("name").GetString());
            AssertSerializedOutcome(receipt, target, success: true);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Attach_NormalizedWriteFails_PreservesVerdictAndEveryExistingField(bool failed)
    {
        WithRoot(root =>
        {
            var baseline = Attach(root, failed);
            var trxPath = Assert.Single(baseline.TestResultPaths!);
            var target = Assert.Single(baseline.NormalizedTestResultPaths!);
            var baselineRead = new TrxTestResultSource().Read(trxPath);
            Assert.Equal(TestResultReadStatus.Readable, baselineRead.Status);
            var baselinePairs = baselineRead.Records.Select(record => (record.Id, record.Status)).ToArray();
            Assert.Equal(4, baselinePairs.Length);
            File.Delete(trxPath);
            File.Delete(target);
            Directory.CreateDirectory(target);
            Assert.False(File.Exists(trxPath));
            Assert.True(Directory.Exists(target));
            AcceptanceCheckResult? receipt = null;

            var exception = Record.Exception(() => { receipt = Attach(root, failed); });

            Assert.Null(exception);
            Assert.NotNull(receipt);
            Assert.Equal(baseline.Passed, receipt.Passed);
            Assert.Equal(baseline.ExitCode, receipt.ExitCode);
            Assert.Equal(baseline.OutputTail, receipt.OutputTail);
            Assert.Equal(baseline.ResultSummary, receipt.ResultSummary);
            Assert.Equal(baseline.Advisory, receipt.Advisory);
            Assert.Equal(baseline.TestResultPaths!.ToArray(), receipt.TestResultPaths!.ToArray());
            Assert.Equal(baseline.ExecutedTestCount, receipt.ExecutedTestCount);
            Assert.Equal(baseline.FailingTestIdentities!.ToArray(), receipt.FailingTestIdentities!.ToArray());
            AssertEveryExistingField(baseline, receipt);
            Assert.True(File.Exists(trxPath));
            var rewritten = new TrxTestResultSource().Read(trxPath);
            Assert.Equal(TestResultReadStatus.Readable, rewritten.Status);
            Assert.Equal(baselinePairs, rewritten.Records.Select(record => (record.Id, record.Status)));
            Assert.Null(receipt.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(receipt.NormalizedTestResultWriteFailure);
            Assert.Equal(target, failure.TargetPath);
            Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
            Assert.False(string.IsNullOrEmpty(failure.Detail));
            Assert.Contains(failure.Detail.Split(':')[0], new[] { nameof(IOException), nameof(UnauthorizedAccessException) });
            Assert.DoesNotContain('\r', failure.Detail);
            Assert.DoesNotContain('\n', failure.Detail);
            Assert.True(Directory.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
            AssertSerializedOutcome(receipt, target, success: false);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serialize_ReceiptWithoutNormalizedData_OmitsBothProperties(bool camelCase)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new AcceptanceCheckResult("go test", true, 0, null),
            Options(camelCase)));

        Assert.DoesNotContain(document.RootElement.EnumerateObject(), property =>
            property.Name.Contains("normalizedTestResult", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSerializedOutcome(AcceptanceCheckResult receipt, string target, bool success)
    {
        foreach (var camelCase in new[] { false, true })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(receipt, Options(camelCase)));
            var root = document.RootElement;
            var paths = camelCase ? "normalizedTestResultPaths" : "NormalizedTestResultPaths";
            var failure = camelCase ? "normalizedTestResultWriteFailure" : "NormalizedTestResultWriteFailure";
            if (success)
            {
                Assert.Equal(target, Assert.Single(root.GetProperty(paths).EnumerateArray()).GetString());
                Assert.False(root.TryGetProperty(failure, out _));
            }
            else
            {
                Assert.False(root.TryGetProperty(paths, out _));
                var detail = root.GetProperty(failure);
                Assert.Equal(JsonValueKind.Object, detail.ValueKind);
                Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed,
                    detail.GetProperty(camelCase ? "reason" : "Reason").GetString());
                Assert.Equal(target, detail.GetProperty(camelCase ? "targetPath" : "TargetPath").GetString());
            }
        }
    }

    private static void AssertEveryExistingField(AcceptanceCheckResult baseline, AcceptanceCheckResult receipt)
    {
        using var before = JsonDocument.Parse(JsonSerializer.Serialize(baseline));
        using var after = JsonDocument.Parse(JsonSerializer.Serialize(receipt));
        foreach (var property in before.RootElement.EnumerateObject())
        {
            if (property.Name is nameof(AcceptanceCheckResult.NormalizedTestResultPaths)
                or nameof(AcceptanceCheckResult.NormalizedTestResultWriteFailure)) continue;
            Assert.Equal(property.Value.GetRawText(), after.RootElement.GetProperty(property.Name).GetRawText());
        }
    }

    private static JsonSerializerOptions Options(bool camelCase) => new()
    {
        PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null
    };

    private static AcceptanceCheckResult Attach(string root, bool failed) =>
        GoTestJsonGateReceipt.Attach(new AcceptanceManifestCheck
            {
                Name = "go test", Type = "command", Command = "go", Arguments = ["test", "-count=1", "./..."]
            }, ["go", "test", "-json", "-count=1", "./..."],
            new(failed ? 1 : 0, failed ? GoTestJsonGateReceiptTests.StreamB : GoTestJsonGateReceiptTests.StreamA),
            root, Path.Combine(root, "attempt"), value => value,
            new("go test", !failed, failed ? 1 : 0, failed ? "raw" : null));

    private static void WithRoot(Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            GoTestJsonGateReceiptTests.CreateGoSources(root);
            assertion(root);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
