using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsCaptureTempRoot : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public async Task CaptureUsesPurposeDirectoryAndDefaultSuccessRemovesFiles()
    {
        var result = await Run(keep: null);
        Assert.Equal(0, result.ExitCode);
        AssertCaptureParent(result);
        Assert.False(File.Exists(result.StdoutPath));
        Assert.False(File.Exists(result.StderrPath));
    }

    [Fact]
    public async Task ExplicitKeepPreservesReportedCapturePaths()
    {
        var result = await Run(keep: true);
        try
        {
            Assert.Equal(0, result.ExitCode);
            AssertCaptureParent(result);
            Assert.True(File.Exists(result.StdoutPath));
            Assert.True(File.Exists(result.StderrPath));
            Assert.Contains("capture-out", File.ReadAllText(result.StdoutPath!));
            Assert.Contains("capture-err", File.ReadAllText(result.StderrPath!));
        }
        finally
        {
            if (result.StdoutPath is not null) File.Delete(result.StdoutPath);
            if (result.StderrPath is not null) File.Delete(result.StderrPath);
        }
    }

    private static Task<GoalAcceptanceVerifier.CommandResult> Run(bool? keep) =>
        GoalAcceptanceVerifier.RunProcessForTestsAsync(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.Write('capture-out'); [Console]::Error.Write('capture-err')"],
            Path.GetTempPath(), TimeSpan.FromSeconds(30), keepCaptureFiles: keep);

    private static void AssertCaptureParent(GoalAcceptanceVerifier.CommandResult result)
    {
        var expected = Path.Combine(Path.GetTempPath(), "mcg-run", "tmp", "acceptance-capture");
        Assert.Equal(expected, Path.GetDirectoryName(result.StdoutPath));
        Assert.Equal(expected, Path.GetDirectoryName(result.StderrPath));
    }
}
