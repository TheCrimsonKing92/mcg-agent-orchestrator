using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class QueryOnlyCompositionTests
{
    [Theory]
    [InlineData("tasks")]
    [InlineData("task")]
    [InlineData("status")]
    [InlineData("next")]
    [InlineData("goals")]
    public void PureQueriesSelectQueryOnlyComposition(string command)
    {
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify([command]));
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("serve-dashboard")]
    [InlineData("hosted-dashboard")]
    [InlineData("simple-hosted-dashboard")]
    [InlineData("open-dashboard")]
    [InlineData("prototype-ui")]
    [InlineData("transcript")]
    public void RemovedDashboardCommandsSelectUnknownCommandComposition(string command)
    {
        Assert.Equal(CliCommandCapabilities.Classify(["unknown-command"]), CliCommandCapabilities.Classify([command]));
        Assert.DoesNotContain("DashboardHost", Enum.GetNames<CliCommandCapability>());
    }

    [Fact]
    public void ExecutionCommandStillSelectsExecutionComposition()
    {
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(["conduct"]));
    }

    [Fact]
    public async Task QueryExecutionDoesNotProbeProviderEndpointsOrStartWebHost()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "query-only-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var acceptCancellation = new CancellationTokenSource();
        var connectionAttempt = listener.AcceptTcpClientAsync(acceptCancellation.Token).AsTask();
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workspace
            };
            startInfo.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
            startInfo.ArgumentList.Add("goals");
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workspace;
            startInfo.Environment["OLLAMA_BASE_URL"] = $"http://127.0.0.1:{endpoint.Port}";
            startInfo.Environment["LLAMA_CPP_BASE_URL"] = $"http://127.0.0.1:{endpoint.Port}";

            var result = await CliChildProcessRunner.RunAsync(startInfo);
            var standardError = result.StandardError;

            acceptCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connectionAttempt);
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("Now listening on", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            acceptCancellation.Cancel();
            listener.Stop();
            Directory.Delete(workspace, recursive: true);
        }
    }

}
