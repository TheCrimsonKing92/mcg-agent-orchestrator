using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;

// Parallel-safe: each case owns its temporary sources, reports and census index; no processes are started.
public sealed class ReceiptTestFailureReaderTests
{
    private static readonly AcceptanceTrxFailure ExpectedFailure = new(
        "example.com/schemadrift/cmd/schemadrift.TestStdoutWithJSONOutput", "Failed", null, null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_GoReceipt_MatchesTrxAndSurvivesWebRoundTrip(bool failed)
    {
        WithRoot(root =>
        {
            var receipt = Attach(root, failed);
            Assert.True(ReceiptTestFailureReader.TryReadNormalized(receipt, out var normalized));
            var failures = ReceiptTestFailureReader.Read(receipt);
            var trxFailures = ReceiptTestFailureReader.ReadFromTrx(receipt);
            var direct = receipt.TestResultPaths!
                .SelectMany(path => AcceptanceTrxFailureReader.Read(path).Failures).ToArray();
            Assert.Equal(direct, trxFailures);
            Assert.Equal(trxFailures, failures);
            Assert.Equal(failures, normalized);
            if (failed)
                Assert.Equal(ExpectedFailure, Assert.Single(failures));
            else
                Assert.Empty(failures);

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var roundTripped = JsonSerializer.Deserialize<AcceptanceCheckResult>(
                JsonSerializer.Serialize(receipt, options), options)!;
            Assert.NotNull(roundTripped);
            Assert.Equal(receipt.NormalizedTestResultPaths, roundTripped.NormalizedTestResultPaths);
            Assert.Equal(failures, ReceiptTestFailureReader.Read(roundTripped));
        });
    }

    [Fact]
    public void Read_RemovedTrx_UsesNormalizedFailures()
    {
        WithRoot(root =>
        {
            var receipt = Attach(root, failed: true);
            var trx = Assert.Single(receipt.TestResultPaths!);
            File.Move(trx, trx + ".gone");

            Assert.Equal(ExpectedFailure, Assert.Single(ReceiptTestFailureReader.Read(receipt)));
            Assert.Empty(ReceiptTestFailureReader.ReadFromTrx(receipt));
            Assert.Empty(ReceiptTestFailureReader.Read(receipt with { NormalizedTestResultPaths = null }));
        });
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Unparseable")]
    [InlineData("Invalid")]
    [InlineData("NullPeer")]
    [InlineData("DifferentPeer")]
    [InlineData("DuplicatePeer")]
    [InlineData("SkippedRecord")]
    [InlineData("MultipleTrx")]
    public void Read_IneligibleNormalizedResult_FallsBackToChangedTrx(string scenario)
    {
        WithRoot(root =>
        {
            var receipt = Attach(root, failed: true);
            var peer = Assert.Single(receipt.NormalizedTestResultPaths!);
            var trx = Assert.Single(receipt.TestResultPaths!);
            switch (scenario)
            {
                case "Missing":
                    File.Move(peer, peer + ".gone");
                    break;
                case "Unparseable":
                    File.WriteAllText(peer, "{");
                    break;
                case "Invalid":
                    File.WriteAllText(peer, File.ReadAllText(peer).Replace("mcg-test-result/1", "mcg-test-result/0"));
                    break;
                case "NullPeer":
                    receipt = receipt with { NormalizedTestResultPaths = null };
                    break;
                case "DifferentPeer":
                    var other = Path.Combine(root, "different.ctrf.json");
                    NormalizedTestResultWriter.Write(other, "go", []);
                    receipt = receipt with { NormalizedTestResultPaths = [other] };
                    break;
                case "DuplicatePeer":
                    receipt = receipt with { NormalizedTestResultPaths = [peer, peer] };
                    break;
                case "SkippedRecord":
                    var records = new TrxTestResultSource().Read(trx);
                    Assert.Equal(TestResultReadStatus.Readable, records.Status);
                    NormalizedTestResultWriter.Write(peer, "go", [.. records.Records,
                        new TestResult("extra-skipped", "extra-skipped", null, [], TestResultStatus.Skipped,
                            null, null, null, [], null, null, "go")]);
                    Assert.Equal(TestResultReadStatus.Readable, new NormalizedTestResultSource().Read(peer).Status);
                    break;
                case "MultipleTrx":
                    var copy = Path.Combine(root, "copy.trx");
                    File.Copy(trx, copy);
                    receipt = receipt with { TestResultPaths = [trx, copy] };
                    break;
                default:
                    throw new ArgumentException("Unknown fallback scenario.", nameof(scenario));
            }

            Assert.False(ReceiptTestFailureReader.TryReadNormalized(receipt, out var normalized));
            Assert.Empty(normalized);
            var failures = ReceiptTestFailureReader.Read(receipt);
            Assert.Equal(ReceiptTestFailureReader.ReadFromTrx(receipt), failures);
            if (scenario == "MultipleTrx")
                Assert.Equal(new[] { ExpectedFailure, ExpectedFailure }, failures);
            else
                Assert.Equal(ExpectedFailure, Assert.Single(failures));
        });
    }

    [Fact]
    public void Gate_GoReceipt_MatchesTrxAndKeepsMessageAfterTrxRemoval()
    {
        WithRoot(root =>
        {
            var receipt = Attach(root, failed: true);
            Assert.False(string.IsNullOrEmpty(receipt.OutputTail));
            var (normalizedReading, normalizedDisposition) = ReadGate(root, "normalized", receipt);
            var (trxReading, trxDisposition) = ReadGate(root, "trx", receipt with { NormalizedTestResultPaths = null });
            var normalized = Assert.Single(normalizedReading.FailingTests);
            var trx = Assert.Single(trxReading.FailingTests);
            Assert.Equal(trx.CheckName, normalized.CheckName);
            Assert.Equal(trx.TestIdentity, normalized.TestIdentity);
            Assert.Equal(ExpectedFailure.TestName, normalized.TestIdentity);
            Assert.Equal(trx.ExceptionSignature, normalized.ExceptionSignature);
            Assert.Equal(trx.FailureMessage, normalized.FailureMessage);
            Assert.Equal(trx.MessageFingerprint, normalized.MessageFingerprint);
            Assert.Equal(trxDisposition.GetType(), normalizedDisposition.GetType());
            Assert.Equal(Assert.IsType<ApparatusRedDisposition.Genuine>(trxDisposition).Reason,
                Assert.IsType<ApparatusRedDisposition.Genuine>(normalizedDisposition).Reason);

            var trxPath = Assert.Single(receipt.TestResultPaths!);
            File.Move(trxPath, trxPath + ".gone");
            var (withoutTrx, _) = ReadGate(root, "normalized-gone", receipt);
            var (fallback, _) = ReadGate(root, "trx-gone", receipt with { NormalizedTestResultPaths = null });
            Assert.Null(Assert.Single(withoutTrx.FailingTests).FailureMessage);
            Assert.Equal(receipt.OutputTail, Assert.Single(fallback.FailingTests).FailureMessage);
        });
    }

    [Fact]
    public void Read_DotnetApparatusRedReceipt_NormalizedFailuresMatchTrx()
    {
        WithRoot(root =>
        {
            var trxPath = Path.Combine(root, "dotnet.trx");
            File.WriteAllText(trxPath, NormalizedGateResultStepTests.Trx);
            var receipt = NormalizedGateResultStep.Apply(
                new AcceptanceCheckResult("dotnet test", false, 1, "raw dotnet failure",
                    TestResultPaths: [trxPath], FailingTestIdentities: ["Sample.Tests.Fails"],
                    ExecutedTestCount: 2), trxPath, NormalizedGateResultStep.TrxSourceLabel);

            Assert.Equal(NormalizedTestResultWriter.PathFor(trxPath),
                Assert.Single(receipt.NormalizedTestResultPaths!));
            Assert.Null(receipt.NormalizedTestResultWriteFailure);
            Assert.True(ReceiptTestFailureReader.TryReadNormalized(receipt, out var normalizedFailures));
            var trxFailures = ReceiptTestFailureReader.ReadFromTrx(receipt);
            Assert.Equal(new AcceptanceTrxFailure("Sample.Tests.Fails", "Failed", "Expected true.",
                "at Sample.Tests.Fails()"), Assert.Single(trxFailures));
            Assert.Equal(trxFailures, normalizedFailures);
            Assert.Equal(trxFailures, ReceiptTestFailureReader.Read(receipt));

            var (normalizedReading, normalizedDisposition) = ReadGate(root, "dotnet-normalized", receipt);
            var (trxReading, trxDisposition) = ReadGate(root, "dotnet-trx",
                receipt with { NormalizedTestResultPaths = null });
            var normalized = Assert.Single(normalizedReading.FailingTests);
            var trx = Assert.Single(trxReading.FailingTests);
            Assert.Equal("dotnet test", normalized.CheckName);
            Assert.Equal("Sample.Tests.Fails", normalized.TestIdentity);
            Assert.Equal("Expected true.", normalized.FailureMessage);
            Assert.Equal(trx.CheckName, normalized.CheckName);
            Assert.Equal(trx.TestIdentity, normalized.TestIdentity);
            Assert.Equal(trx.ExceptionSignature, normalized.ExceptionSignature);
            Assert.Equal(trx.FailureMessage, normalized.FailureMessage);
            Assert.Equal(trx.MessageFingerprint, normalized.MessageFingerprint);
            Assert.Equal(Assert.IsType<ApparatusRedDisposition.Genuine>(trxDisposition).Reason,
                Assert.IsType<ApparatusRedDisposition.Genuine>(normalizedDisposition).Reason);

            File.Move(trxPath, trxPath + ".gone");
            Assert.Empty(ReceiptTestFailureReader.ReadFromTrx(receipt));
            Assert.Equal(trxFailures, ReceiptTestFailureReader.Read(receipt));
        });
    }

    private static (ApparatusRedGateReading Reading, ApparatusRedDisposition Disposition) ReadGate(
        string root, string indexName, AcceptanceCheckResult receipt)
    {
        var index = new AcceptanceFailingTestIndex(Path.Combine(root, indexName + ".json"));
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z", null);
        var gate = new ApparatusRedGate(index, _ => root, () => now);
        var (_, goal) = ConductorDriverTests.SimpleGoal();
        var reading = Assert.IsType<ApparatusRedGateReading>(gate.RecordGateCompletion(goal,
            new AcceptanceVerificationSummary(false, [receipt], FailedChecks: [receipt.Name],
                BranchHeadSha: "candidate-a", MainHeadSha: "main-a"),
            new Lazy<IReadOnlyList<string>>(() => [])));
        return (reading, gate.Classify(goal, reading));
    }

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
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
