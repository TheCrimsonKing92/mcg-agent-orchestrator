using System.Text;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsOwnConsoleCodePage(ITestOutputHelper output)
{
    [Fact]
    public async Task Utf8OwnConsolePreservesHostCodePagesAndDefaultGitSharesConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var document = await ConsoleHostExperimentHarness.Run(1, "both", ownConsoleCheck: true,
            writeOutput: output.WriteLine);
        var report = document.RootElement;
        Assert.Equal("InheritWindowlessConsole", report.GetProperty("switchAtStartup").GetString());
        Assert.Equal(0, report.GetProperty("consoleWindow").GetInt64());
        Assert.True(report.GetProperty("consoleProcessCount").GetUInt32() > 0, report.GetRawText());
        var check = report.GetProperty("ownConsoleCheck");
        var child = check.GetProperty("child");
        CheckCensus(child, 1);
        Assert.Equal("", child.GetProperty("stderr").GetString());
        var bytes = Convert.FromBase64String(child.GetProperty("stdoutBase64").GetString()!);
        Assert.Equal("non-ASCII caf\u00e9 \u6f22\u5b57 e\u0301", new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n'));
        CheckCensus(check.GetProperty("git"), 0);
        foreach (var axis in new[] { "input", "output" })
        {
            var before = check.GetProperty("before").GetProperty(axis).GetUInt32();
            Assert.NotEqual(0u, before);
            Assert.Equal(before, check.GetProperty("afterOwnConsole").GetProperty(axis).GetUInt32());
            Assert.Equal(before, check.GetProperty("afterGit").GetProperty(axis).GetUInt32());
        }
    }

    private static void CheckCensus(JsonElement child, int expectedConhosts)
    {
        Assert.Equal(0, child.GetProperty("exitCode").GetInt32());
        Assert.Equal(0, child.GetProperty("unclassifiedProcesses").GetInt32());
        var images = child.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(child.GetProperty("totalProcesses").GetUInt32(), (uint)images.Length);
        Assert.Contains(images, image => image.GetProperty("processId").GetInt32() == child.GetProperty("processId").GetInt32());
        Assert.Equal(expectedConhosts, child.GetProperty("conhostCount").GetInt32());
        Assert.Equal(expectedConhosts, images.Count(image => image.GetProperty("isConhost").GetBoolean()));
    }
}
