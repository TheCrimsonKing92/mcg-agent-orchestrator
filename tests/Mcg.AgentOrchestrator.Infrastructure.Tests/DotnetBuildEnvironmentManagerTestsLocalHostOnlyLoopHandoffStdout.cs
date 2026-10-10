using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorLoopHandoffTestConditions;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffStdout : ConductorBatchLoopTests
{
    public DotnetBuildEnvironmentManagerTestsLocalHostOnlyLoopHandoffStdout(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Trait("Category", "LocalHostOnly")]
    [Xunit.Fact(
        DisplayName = "ConductorLoopHandoff_windows_launcher_inherits_redirected_stdout_handle",
        Skip = "requires a breakaway-permitted job; the acceptance gate runs this test",
        SkipType = typeof(ConductorLoopHandoffTestConditions),
        SkipUnless = nameof(IsWindowsBreakawayPermitted))]
    public void ConductorLoopHandoffWindowsLauncherInheritsRedirectedStdoutHandle()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-stdout-handoff");
        ConductorSupervisorProcessIdentity? successorIdentity = null;
        int? processId = null;
        var suppressionScopeActive = false;
        try
        {
            var stdoutPath = Path.Combine(root, "successor.out.log");
            var stderrPath = Path.Combine(root, "successor.err.log");
            var stdoutMarker = "handoff-stdout-marker-" + Guid.NewGuid().ToString("N");
            var stderrMarker = "handoff-stderr-marker-" + Guid.NewGuid().ToString("N");
            var scriptPath = Path.Combine(root, "write-marker.cmd");
            File.WriteAllText(scriptPath, $"@echo {stdoutMarker}{Environment.NewLine}@echo {stderrMarker} 1>&2{Environment.NewLine}");
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            var result = ConductorLoopHandoff.LaunchDetachedWindows(
                new ConductLoopLaunchRequest("batch1", [], stdoutPath, stderrPath, root, 0),
                [
                    cmdPath,
                    "/d",
                    "/c",
                    scriptPath
                ],
                acquireConsoleSuppression: () =>
                {
                    suppressionScopeActive = true;
                    return new ProcessTreeGuiSuppression.ConsoleSpawnScope(
                        childConsolePolicyApplied: true,
                        onDispose: () => suppressionScopeActive = false);
                },
                beforeCreateProcess: () => Assert.True(
                    suppressionScopeActive,
                    "Hidden-console suppression was disposed before CreateProcessW."));

            Assert.True(result.ProcessId > 0);
            Assert.Contains("breakawaySucceeded=true", result.LaunchDetail, StringComparison.Ordinal);
            Assert.Matches("residualJobMembership=(true|false)", result.LaunchDetail);
            Assert.False(suppressionScopeActive);
            successorIdentity = result.SuccessorIdentity;
            processId = result.ProcessId;
            var conductEventsPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
            Assert.True(File.Exists(conductEventsPath), "Windows handoff did not journal its pre-spawn diagnostic.");
            var conductEvents = File.ReadAllText(conductEventsPath);
            Assert.Contains("\"eventKind\":\"loop-handoff-spawn\"", conductEvents, StringComparison.Ordinal);
            Assert.Contains("spawnPath=windows-createprocess", conductEvents, StringComparison.Ordinal);
            Assert.Matches("incumbentConsole=(present|absent)", conductEvents);
            Assert.Matches("incumbentConsoleAttached=(true|false)", conductEvents);
            Assert.Contains("suppression=child-owned-hidden-console", conductEvents, StringComparison.Ordinal);
            Assert.True(WaitUntil(
                () => File.Exists(stdoutPath) && ReadAllTextShared(stdoutPath).Contains(stdoutMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stdout did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
            Assert.True(WaitUntil(
                () => File.Exists(stderrPath) && ReadAllTextShared(stderrPath).Contains(stderrMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)),
                $"stderr did not contain marker. child={DescribeProcess(processId.Value)} stdout={TryReadAllTextShared(stdoutPath)} stderr={TryReadAllTextShared(stderrPath)}");
        }
        finally
        {
            TestOwnedProcessStop.StopTreeIfSame(successorIdentity);

            TryDeleteDirectory(root);
        }
    }
}
