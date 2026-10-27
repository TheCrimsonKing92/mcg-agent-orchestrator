using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

// Serial: the verifier uses build slots, the attempt-prefix environment variable and lock attribution hooks.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class DotnetNormalizedTrxReceiptTests : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Theory]
    [InlineData("mtp", false)]
    [InlineData("vstest", false)]
    [InlineData("vstest", true)]
    public async Task Run_DotnetReceipt_WritesNormalizedPeerAfterAttemptCopy(string runner, bool retry)
    {
        await WithReceiptAsync(runner, retry, "readable", failed: false, (receipt, path, baseline) =>
        {
            AssertOutcome(baseline, receipt);
            Assert.True(receipt.Passed);
            Assert.Equal(retry, receipt.LockRemediationApplied);
            var target = Assert.Single(receipt.NormalizedTestResultPaths!);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), target);
            Assert.True(File.Exists(target));
            Assert.Null(receipt.NormalizedTestResultWriteFailure);
            using var document = JsonDocument.Parse(File.ReadAllText(target));
            Assert.Equal("trx", document.RootElement.GetProperty("results")
                .GetProperty("tool").GetProperty("name").GetString());
            var read = new NormalizedTestResultSource().Read(target);
            Assert.Equal(TestResultReadStatus.Readable, read.Status);
            Assert.Single(read.Records);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_UnreadableTrx_RecordsFailureWithoutChangingOutcome(bool failed)
    {
        await WithReceiptAsync("vstest", false, "unreadable", failed, (receipt, path, baseline) =>
        {
            AssertOutcome(baseline, receipt);
            Assert.Null(receipt.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(receipt.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.SourceNotReadable, failure.Reason);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), failure.TargetPath);
            Assert.Equal(new TrxTestResultSource().Read(path).Detail, failure.Detail);
        });
    }

    [Theory]
    [InlineData("mtp", false)]
    [InlineData("mtp", true)]
    [InlineData("vstest", false)]
    [InlineData("vstest", true)]
    public async Task Run_ObstructedNormalizedTarget_PreservesOriginalOutcome(string runner, bool failed)
    {
        await WithReceiptAsync(runner, false, "obstructed", failed, (receipt, path, baseline) =>
        {
            AssertOutcome(baseline, receipt);
            Assert.Equal(!failed, receipt.Passed);
            Assert.Equal(failed ? 2 : 1, receipt.ExecutedTestCount);
            Assert.Equal(failed ? new[] { "Sample.Tests.Fails" } : [], receipt.FailingTestIdentities);
            Assert.Null(receipt.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(receipt.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), failure.TargetPath);
            Assert.True(Directory.Exists(failure.TargetPath));
        });
    }

    private async Task WithReceiptAsync(string runner, bool retry, string sourceMode, bool failed,
        Action<AcceptanceCheckResult, string, AcceptanceCheckResult> assertion)
    {
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "checks": [
                { "name": "normalized TRX", "type": "dotnet-test", "runner": "{{runner}}", "project": "tests/Sample.Tests/Sample.Tests.csproj", "arguments": [] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var attemptDirectory = Path.Combine(root, "attempt");
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var previousAttribution = LockAttribution.AttributeForTests;
        var calls = 0;
        string? sourcePath = null;
        AcceptanceCheckResult? baseline = null;
        try
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(attemptDirectory, "gate"));
            if (retry)
                LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
                    path, [new BuildLockHolder(null, "foreign-csc", null, false)], "test");
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                var isTest = runner == "mtp" ? IsMtpExecutableCall(args, "Sample.Tests") : IsVstestExecution(args);
                if (!isTest)
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                calls++;
                if (retry && calls == 1)
                    return Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("Build artifacts unavailable"));
                if (runner == "mtp") WriteMtpTrx(args, 1, ["Sample.Tests.Passes"]);
                else WriteVstestTrx(args, "Sample.Tests.Passes");
                var directoryIndex = Array.IndexOf(args, "--results-directory");
                var fileIndex = Array.IndexOf(args, runner == "mtp" ? "--report-trx-filename" : "--logger");
                sourcePath = Path.Combine(args[directoryIndex + 1], runner == "mtp"
                    ? args[fileIndex + 1] : args[fileIndex + 1]["trx;LogFileName=".Length..]);
                Assert.True(File.Exists(sourcePath));
                if (sourceMode == "unreadable") File.WriteAllText(sourcePath, "not XML");
                else if (failed) File.WriteAllText(sourcePath, NormalizedGateResultStepTests.Trx);
                if (sourceMode == "obstructed")
                {
                    var target = NormalizedTestResultWriter.PathFor(Path.Combine(attemptDirectory, Path.GetFileName(sourcePath)));
                    Directory.CreateDirectory(target);
                    Assert.True(Directory.Exists(target));
                }
                var command = new GoalAcceptanceVerifier.CommandResult(failed ? 1 : 0,
                    failed ? "Failed: 1" : "Passed: 1");
                // Capture the native outcome before the production normalization step can run.
                var evidence = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence([sourcePath]);
                var passed = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(command, evidence,
                    requireTrxEvidence: false).Passed;
                baseline = new AcceptanceCheckResult("normalized TRX", passed, command.ExitCode, null,
                    ExecutedTestCount: evidence.ExecutedTestCount,
                    FailingTestIdentities: passed ? [] : GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities([sourcePath]));
                return Task.FromResult(command);
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Assert.Equal(retry ? 2 : 1, calls);
            var receipt = Assert.Single(result.Checks!);
            var durableTrx = Assert.Single(receipt.TestResultPaths!);
            Assert.Equal(attemptDirectory, Path.GetDirectoryName(durableTrx));
            Assert.NotEqual(sourcePath, durableTrx);
            Assert.True(File.Exists(durableTrx));
            Assert.Equal(File.ReadAllText(sourcePath!), File.ReadAllText(durableTrx));
            Assert.False(File.Exists(NormalizedTestResultWriter.PathFor(sourcePath!)));
            assertion(receipt, durableTrx, Assert.IsType<AcceptanceCheckResult>(baseline));
        }
        finally
        {
            LockAttribution.AttributeForTests = previousAttribution;
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void AssertOutcome(AcceptanceCheckResult baseline, AcceptanceCheckResult receipt)
    {
        Assert.Equal(baseline.Passed, receipt.Passed);
        Assert.Equal(baseline.ExitCode, receipt.ExitCode);
        Assert.Equal(baseline.ExecutedTestCount, receipt.ExecutedTestCount);
        Assert.Equal(baseline.FailingTestIdentities, receipt.FailingTestIdentities);
    }
}
