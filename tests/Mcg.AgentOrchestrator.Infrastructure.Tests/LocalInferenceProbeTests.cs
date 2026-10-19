using Mcg.AgentOrchestrator.App.Providers;
using Xunit;

// Parallel-safe: only URI classification is exercised; no sockets or clock reads.
public sealed class LocalInferenceProbeTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/v1/models")]
    [InlineData("http://127.12.34.56:8080/v1/models")]
    [InlineData("http://localhost:8080/v1/models")]
    [InlineData("http://LOCALHOST:8080/v1/models")]
    [InlineData("http://[::1]:8080/v1/models")]
    public void ConnectTimeoutFor_LoopbackHost_Returns500Milliseconds(string modelsUrl)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), LocalInferenceProbe.ConnectTimeoutFor(new Uri(modelsUrl)));
    }

    [Theory]
    [InlineData("http://inference.example:8080/v1/models")]
    [InlineData("http://192.0.2.1:8080/v1/models")]
    [InlineData("http://[2001:db8::1]:8080/v1/models")]
    public void ConnectTimeoutFor_NonLoopbackHost_ReturnsNoConnectCap(string modelsUrl)
    {
        Assert.Null(LocalInferenceProbe.ConnectTimeoutFor(new Uri(modelsUrl)));
    }
}
