using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorDiagnosticDumpCaptureTests
{
    [Xunit.Fact]
    public async Task MissingToolReturnsNoDumpWithoutStartingProcess()
    {
        var started = false;
        var capture = new DotnetDumpConductorDiagnosticDumpCapture(
            resolveTool: () => null,
            runProcess: (_, _, _, _) =>
            {
                started = true;
                return Task.FromResult(0);
            });

        var result = await capture.CaptureAsync(4242, Path.GetTempPath(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Captured);
        Assert.Null(result.DumpPath);
        Assert.Equal("tool-missing", result.Reason);
        Assert.False(started);
    }
}
