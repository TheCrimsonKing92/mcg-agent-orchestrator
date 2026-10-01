using System.Diagnostics;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection("EnvMutation")]
public sealed class AdvanceLoopTestsBlockingWorkerRelease
{
    [Fact]
    public void BlockingCommandsShareOneReleaseFileBuilderUnderTheTestRoot()
    {
        var source = File.ReadAllText(Path.Combine(SharedTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "AdvanceLoopTests.cs"));
        Assert.DoesNotContain("Start-Sleep -Seconds 30", source);
        Assert.Empty(Regex.Matches(source, @"Start-Sleep\s+-Seconds"));
        Assert.Single(Regex.Matches(source, "Test-Path -LiteralPath"));
        Assert.Contains("Path.Combine(root, WorkerReleaseFileName)", source);
        Assert.Equal(6, Regex.Matches(source, @"ReleaseBlockingWorkers\(root\);").Count);

        var root = Path.Combine(Path.GetTempPath(), "worker's root");
        var releasePath = Path.Combine(root, AdvanceLoopTests.WorkerReleaseFileName);
        var commands = new[]
        {
            AdvanceLoopTests.BlockingCodexProfileCommand(root),
            AdvanceLoopTests.BlockingClaudeProfileCommand(root),
            AdvanceLoopTests.BlockingXhighCodexProfileCommand(root),
            AdvanceLoopTests.BlockingWorkerCommand(root, "Write-Output manual")
        };
        Assert.Equal(releasePath, AdvanceLoopTests.WorkerReleasePath(root));
        Assert.All(commands, command =>
        {
            Assert.Contains("Test-Path -LiteralPath '" + releasePath.Replace("'", "''") + "'", command);
            Assert.Contains("$poll -lt 1500", command);
            Assert.Contains("Start-Sleep -Milliseconds 200", command);
            Assert.DoesNotContain("Start-Sleep -Seconds", command);
        });
    }

    [Fact]
    public async Task BlockingWorkerExitsWhenItsReleaseFileIsCreated()
    {
        var parent = InfrastructureTestSupport.CreateTempDirectory();
        var root = Path.Combine(parent, "worker's root");
        Directory.CreateDirectory(root);
        var releasePath = AdvanceLoopTests.WorkerReleasePath(root);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(WorkerShell.Executable)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in WorkerShell.BaseArguments())
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.ArgumentList.Add("Write-Output waiting; " +
            AdvanceLoopTests.BlockingWorkerCommand(root, "Write-Output released"));
        var started = false;
        Task<string?>? ready = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            Assert.True(process.Start());
            started = true;
            process.StandardInput.Close();
            stderr = process.StandardError.ReadToEndAsync();
            ready = process.StandardOutput.ReadLineAsync();
            string? signal;
            try
            {
                signal = await ready.WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Worker did not signal readiness before waiting for release file '{releasePath}'.");
                throw;
            }
            stdout = process.StandardOutput.ReadToEndAsync();
            Assert.Equal("waiting", signal);
            Assert.False(process.HasExited);
            Assert.False(File.Exists(releasePath));
            AdvanceLoopTests.ReleaseBlockingWorkers(root);
            await AwaitExit(process, releasePath);

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("released", await stdout);
            Assert.Equal(string.Empty, await stderr);
        }
        finally
        {
            try
            {
                AdvanceLoopTests.ReleaseBlockingWorkers(root);
            }
            finally
            {
                try
                {
                    if (started)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        await AwaitExit(process, releasePath);
                        if (ready is not null) _ = await ready;
                        if (stdout is not null) _ = await stdout;
                        if (stderr is not null) _ = await stderr;
                    }
                }
                finally
                {
                    TempRootJanitor.DeleteTree(parent);
                }
            }
        }
    }

    private static async Task AwaitExit(Process process, string releasePath)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Blocking worker did not exit after release file '{releasePath}' was created.");
        }
    }
}
