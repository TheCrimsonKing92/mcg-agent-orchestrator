using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsSdkOwnConsole(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentSdkCommandsPreserveHostCodePagesAndDefaultGitSharesConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await ConsoleHostExperimentHarness.Run(1, "both", sdkOwnConsoleCheck: true,
            writeOutput: output.WriteLine);
        var report = document.RootElement;
        Assert.Equal("InheritWindowlessConsole", report.GetProperty("switchAtStartup").GetString());
        Assert.Equal(0, report.GetProperty("consoleWindow").GetInt64());
        Assert.True(report.GetProperty("consoleProcessCount").GetUInt32() > 0, report.GetRawText());
        var check = report.GetProperty("sdkOwnConsoleCheck");
        var children = check.GetProperty("children").EnumerateArray().ToArray();
        Assert.Equal(2, children.Length);
        Assert.NotEqual(children[0].GetProperty("processId").GetInt32(), children[1].GetProperty("processId").GetInt32());
        foreach (var child in children)
        {
            Assert.True(child.GetProperty("exitCode").GetInt32() == 0, child.GetRawText());
            Assert.Equal(1, child.GetProperty("conhostCount").GetInt32());
            Assert.Single(child.GetProperty("images").EnumerateArray().Where(image => image.GetProperty("isConhost").GetBoolean()));
        }
        CheckGitCensus(check.GetProperty("git"));
        foreach (var axis in new[] { "input", "output" })
        {
            var before = check.GetProperty("before").GetProperty(axis).GetUInt32();
            Assert.NotEqual(0u, before);
            Assert.Equal(before, check.GetProperty("afterSdk").GetProperty(axis).GetUInt32());
            Assert.Equal(before, check.GetProperty("afterGit").GetProperty(axis).GetUInt32());
        }
    }

    private static void CheckGitCensus(JsonElement child)
    {
        Assert.Equal(0, child.GetProperty("exitCode").GetInt32());
        Assert.Equal(0, child.GetProperty("unclassifiedProcesses").GetInt32());
        var images = child.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(child.GetProperty("totalProcesses").GetUInt32(), (uint)images.Length);
        Assert.Contains(images, image => image.GetProperty("processId").GetInt32() == child.GetProperty("processId").GetInt32());
        Assert.Equal(0, child.GetProperty("conhostCount").GetInt32());
        Assert.DoesNotContain(images, image => image.GetProperty("isConhost").GetBoolean());
    }
}
