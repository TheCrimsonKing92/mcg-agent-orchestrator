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
    [InlineData("dashboard")]
    [InlineData("transcript")]
    public void PureQueriesSelectQueryOnlyComposition(string command)
    {
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify([command]));
    }

    [Theory]
    [InlineData("serve-dashboard")]
    [InlineData("hosted-dashboard")]
    [InlineData("simple-hosted-dashboard")]
    [InlineData("open-dashboard")]
    [InlineData("prototype-ui")]
    public void DashboardCommandsSelectOptionalHostComposition(string command)
    {
        Assert.Equal(CliCommandCapability.DashboardHost, CliCommandCapabilities.Classify([command]));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("hosted")]
    [InlineData("read-only")]
    public void DashboardModeSelectsOptionalHostComposition(string mode)
    {
        var args = new[] { "dashboard", "--mode", mode };

        Assert.Equal(CliCommandCapability.DashboardHost, CliCommandCapabilities.Classify(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
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

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start query process.");
            using var processTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(processTimeout.Token);
            var standardError = await process.StandardError.ReadToEndAsync(processTimeout.Token);

            acceptCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connectionAttempt);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Now listening on", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            acceptCancellation.Cancel();
            listener.Stop();
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task DashboardHelpWorksWithoutOptionalDashboardComponent()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "headless-dashboard-help-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
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
            startInfo.ArgumentList.Add("dashboard");
            startInfo.ArgumentList.Add("--help");
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workspace;

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start help process.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Usage: dashboard", standardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("component is not installed", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task DashboardModeReportsMissingOptionalDashboardComponent()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "headless-dashboard-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
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
            startInfo.ArgumentList.Add("dashboard");
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add("local");
            startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workspace;

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dashboard process.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            Assert.Equal(OptionalDashboardHostLauncher.MissingComponentExitCode, process.ExitCode);
            Assert.Empty(standardOutput);
            Assert.Contains("Dashboard component is not installed", standardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
