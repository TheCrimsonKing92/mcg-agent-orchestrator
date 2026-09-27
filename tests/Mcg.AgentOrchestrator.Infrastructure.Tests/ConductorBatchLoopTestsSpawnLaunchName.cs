using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorBatchLoopTestsSpawnLaunchName
{
    [Xunit.Theory]
    [Xunit.InlineData("spawnPath=windows-createprocess incumbentConsole=absent suppression=existing-console-preserved")]
    [Xunit.InlineData("spawnPath=windows-createprocess error=Win32Exception nativeError=5 message=denied")]
    public void WindowsSpawnEventDetailNamesTheLaunch(string consoleDetail)
    {
        var request = new ConductLoopLaunchRequest("spec-refinement-abcd1234", [], "out", "err", "work", 0);

        var detail = ConductorLoopHandoff.FormatWindowsSpawnEventDetail(request, consoleDetail);

        Assert.Equal($"{consoleDetail} launch=spec-refinement-abcd1234", detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData("", "unnamed")]
    [Xunit.InlineData(" \t", "unnamed")]
    [Xunit.InlineData("a b\r\nc", "a_b_c")]
    public void WindowsSpawnEventDetailKeepsLaunchTokenOnOneLine(string name, string expected)
    {
        var request = new ConductLoopLaunchRequest(name, [], "out", "err", "work", 0);

        Assert.Equal($"console=present launch={expected}",
            ConductorLoopHandoff.FormatWindowsSpawnEventDetail(request, "console=present"));
    }
}
