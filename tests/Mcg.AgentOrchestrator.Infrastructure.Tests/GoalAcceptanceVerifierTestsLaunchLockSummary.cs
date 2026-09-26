using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierTestsLaunchLockSummary : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task GateLanePreservesTestHostSummaryBesideItsTrx()
    {
        const string summary =
            "LAUNCH_LOCK_SUMMARY pid=4242 launches=2 wait_ms_total=1.000 wait_p50_ms=0.500 " +
            "wait_p95_ms=1.000 wait_max_ms=1.000 hold_ms_total=3.000 hold_p50_ms=1.000 " +
            "hold_p95_ms=2.000 hold_max_ms=2.000 create_ms_total=1.000 " +
            "by_entry=AcquireConsoleForChildSpawn:0,AcquireSuppressedChildSpawn:2," +
            "AcquireErrorModeForChildSpawn:0,Start:0";
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Launch lock", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--filter", "FullyQualifiedName~LaunchLockSummaryFormatterTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var attemptDirectory = Path.Combine(root, "TestResults");
        var attemptPrefix = Path.Combine(attemptDirectory, "launch-lock-attempt");
        var previousPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                attemptPrefix);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed: 1",
                        Stderr: "LAUNCH_LOCK_SUMMARY pid=7 launches=0" + Environment.NewLine +
                            "test host diagnostic" + Environment.NewLine + summary + Environment.NewLine));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, stableSlotIndex: 0);

            Xunit.Assert.True(result.Passed, result.OutputTail);
            var lane = Xunit.Assert.Single(result.Checks!);
            var trxPath = Xunit.Assert.Single(lane.TestResultPaths!);
            Xunit.Assert.True(File.Exists(trxPath));
            var summaryPath = Path.ChangeExtension(trxPath, ".launch-lock-summary.txt");
            Xunit.Assert.Equal(attemptDirectory, Path.GetDirectoryName(summaryPath));
            Xunit.Assert.Equal(summary + Environment.NewLine, File.ReadAllText(summaryPath));
            Xunit.Assert.Single(Directory.GetFiles(attemptDirectory, "*.launch-lock-summary.txt"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousPrefix);
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }
}
