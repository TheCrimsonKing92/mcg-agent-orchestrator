using System.ComponentModel;

// Parallel-safe: diagnostic queries are injected; no process or shared filesystem state is used.
public sealed class MtpTestRunnerScriptTestsBridgeBootstrapCapture
{
    private static readonly DateTime StartedAt = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    public void ImageUnavailable_RecordsErrorAndPreservesStartTime(string? image)
    {
        var diagnostics = WorkerBuildBridgeBootstrapCapture.ReadProcessDiagnostics(() => StartedAt, () => image);

        Assert.Null(diagnostics.Image);
        Assert.Contains("image metadata is unavailable", diagnostics.ImageError, StringComparison.Ordinal);
        Assert.Equal(StartedAt, diagnostics.StartTimeUtc);
        Assert.Null(diagnostics.StartTimeError);
    }

    [Xunit.Fact]
    public void ImageQueryDenied_RecordsFailureAndPreservesStartTime()
    {
        var failure = new Win32Exception(5);
        var diagnostics = WorkerBuildBridgeBootstrapCapture.ReadProcessDiagnostics(() => StartedAt, () => throw failure);

        Assert.Null(diagnostics.Image);
        Assert.Equal(failure.ToString(), diagnostics.ImageError);
        Assert.Equal(StartedAt, diagnostics.StartTimeUtc);
        Assert.Null(diagnostics.StartTimeError);
    }

    [Xunit.Fact]
    public void StartTimeQueryUnavailable_RecordsFailureAndStillReadsImage()
    {
        var failure = new InvalidOperationException("Process has exited.");
        var diagnostics = WorkerBuildBridgeBootstrapCapture.ReadProcessDiagnostics(() => throw failure, () => "observed-image.exe");

        Assert.Null(diagnostics.StartTimeUtc);
        Assert.Equal(failure.ToString(), diagnostics.StartTimeError);
        Assert.Equal("observed-image.exe", diagnostics.Image);
        Assert.Null(diagnostics.ImageError);
    }

    [Xunit.Fact]
    public void AvailableMetadata_PreservesObservedValuesWithoutErrors()
    {
        var diagnostics = WorkerBuildBridgeBootstrapCapture.ReadProcessDiagnostics(() => StartedAt, () => "observed-image.exe");

        Assert.Equal(StartedAt, diagnostics.StartTimeUtc);
        Assert.Equal("observed-image.exe", diagnostics.Image);
        Assert.Null(diagnostics.StartTimeError);
        Assert.Null(diagnostics.ImageError);
    }
}
