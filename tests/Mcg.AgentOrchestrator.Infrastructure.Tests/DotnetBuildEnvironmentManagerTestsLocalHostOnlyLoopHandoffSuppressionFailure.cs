using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorLoopHandoffTestConditions;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffSuppressionFailure : ConductorBatchLoopTests
{
    public DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffSuppressionFailure(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Trait("Category", "LocalHostOnly")]
    [Xunit.Fact(
        Skip = "requires a breakaway-permitted job; the acceptance gate runs this test",
        SkipType = typeof(ConductorLoopHandoffTestConditions),
        SkipUnless = nameof(IsWindowsBreakawayPermitted))]
    public void ConductorLoopHandoffSuppressionFailureStillStartsSuccessor()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-suppression-failure");
        ConductorSupervisorProcessIdentity? successorIdentity = null;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var marker = "handoff-fail-open-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {marker}{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [cmdPath, "/d", "/c", scriptPath],
                acquireConsoleSuppression: () => throw new System.ComponentModel.Win32Exception(5, "synthetic suppression failure"));

            Assert.True(result.ProcessId > 0);
            Assert.Contains("breakawaySucceeded=true", result.LaunchDetail, StringComparison.Ordinal);
            Assert.Matches("residualJobMembership=(true|false)", result.LaunchDetail);
            successorIdentity = result.SuccessorIdentity;
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(marker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"successor did not start after suppression failure. stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");

            var conductEvents = File.ReadAllText(Path.Combine(root, ConductEventLogWriter.CurrentFileName));
            Assert.Contains("\"eventKind\":\"loop-handoff-console-suppression-failed\"", conductEvents, StringComparison.Ordinal);
            Assert.Contains("error=Win32Exception", conductEvents, StringComparison.Ordinal);
            Assert.Contains("nativeError=5", conductEvents, StringComparison.Ordinal);
            Assert.Contains("suppression=acquisition-failed", conductEvents, StringComparison.Ordinal);
        }
        finally
        {
            TestOwnedProcessStop.StopTreeIfSame(successorIdentity);

            TryDeleteDirectory(root);
        }
    }
}
