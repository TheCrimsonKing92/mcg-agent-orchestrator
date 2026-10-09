using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

public sealed class CliChildProcessRunnerTests
{
    [Fact]
    public async Task HangingChildIsTreeKilledAndNamedWhenInjectedGuardExpires()
    {
        Assert.True(CliChildProcessRunner.DefaultHangGuard >= TimeSpan.FromSeconds(120));
        var markerDirectory = Path.Combine(Path.GetTempPath(), $"mcg-cli-child-marker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(markerDirectory);
        var marker = Path.Combine(markerDirectory, "descendant.pid");
        var temporaryMarker = marker + ".tmp";

        var start = TreeStart(marker, temporaryMarker);
        Process? child = null;
        Process? descendant = null;
        ConductorSupervisorProcessIdentity? childIdentity = null;
        ConductorSupervisorProcessIdentity? descendantIdentity = null;
        try
        {
            var markerWritten = MarkerFileProbe.WaitAsync(marker, TestHangGuard.Bound,
                () => child is null ? MarkerFileProbe.ChildState.NotStarted
                    : child.HasExited ? MarkerFileProbe.ChildState.Exited(child.ExitCode)
                    : MarkerFileProbe.ChildState.Running);
            var run = CliChildProcessRunner.RunAsync(start, TimeSpan.FromSeconds(10),
                process =>
                {
                    childIdentity = TestOwnedProcessStop.Identify(process);
                    child = Process.GetProcessById(process.Id);
                    markerWritten.GetAwaiter().GetResult();
                });
            await markerWritten;
            descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            descendantIdentity = TestOwnedProcessStop.Identify(descendant);
            Assert.False(descendant.HasExited);

            await TestHangGuard.CompletesWithinAsync(run, TestHangGuard.HangSafetyBound,
                "CliChildProcessRunner.RunAsync with injected 10 s guard");
            var error = await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.Contains(start.FileName, error.Message, StringComparison.Ordinal);
            Assert.Contains("did not exit", error.Message, StringComparison.Ordinal);
            Assert.Contains("stdout=started", error.Message, StringComparison.Ordinal);
            Assert.Contains("stderr=", error.Message, StringComparison.Ordinal);
            Assert.NotNull(child);
            Assert.True(child.HasExited);
            await descendant.WaitForExitAsync().WaitAsync(TestHangGuard.Bound);
            Assert.True(descendant.HasExited);
        }
        finally
        {
            if (descendant is not null)
            {
                if (!descendant.HasExited && TestOwnedProcessStop.StopTreeIfSame(descendantIdentity))
                {
                    await descendant.WaitForExitAsync();
                }
                descendant.Dispose();
            }
            if (child is not null)
            {
                if (!child.HasExited && TestOwnedProcessStop.StopTreeIfSame(childIdentity))
                {
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
            try { Directory.Delete(markerDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task HangSafetyBoundFailsWithItsOwnMessageWhenTaskNeverCompletes()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bound = TimeSpan.FromMilliseconds(200);
        const string operation = "never-completing control";

        var error = await Assert.ThrowsAsync<TestHangGuardExpiredException>(() =>
            TestHangGuard.CompletesWithinAsync(never.Task, bound, operation));

        Assert.IsNotAssignableFrom<TimeoutException>(error);
        Assert.Contains("hang-safety bound", error.Message, StringComparison.Ordinal);
        Assert.Contains(bound.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains(operation, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not exit", error.Message, StringComparison.Ordinal);
        Assert.False(never.Task.IsCompleted);
    }

    private static ProcessStartInfo TreeStart(string marker, string temporaryMarker)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("Write-Output started; $p = Start-Process ping.exe -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                $"Set-Content -LiteralPath '{temporaryMarker.Replace("'", "''")}' -Value $p.Id; " +
                $"Move-Item -LiteralPath '{temporaryMarker.Replace("'", "''")}' -Destination '{marker.Replace("'", "''")}' ; " +
                "Wait-Process -Id $p.Id");
            return start;
        }

        var shellMarker = marker.Replace("'", "'\"'\"'");
        var shellTemporaryMarker = temporaryMarker.Replace("'", "'\"'\"'");
        return ShellStart($"echo started; sleep 3600 & child=$!; printf '%s' \"$child\" > '{shellTemporaryMarker}'; mv '{shellTemporaryMarker}' '{shellMarker}'; wait \"$child\"");
    }

    [Fact]
    public async Task LargeOutputOnBothStreamsIsReturnedCompleteWithExitCode()
    {
        const string line = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789ABCD";
        const int lines = 1024;
        var expected = string.Concat(Enumerable.Repeat(line + (OperatingSystem.IsWindows() ? "\r\n" : "\n"), lines));
        var command = OperatingSystem.IsWindows()
            ? $"(for /L %i in (1,1,{lines}) do @echo {line}) & (for /L %i in (1,1,{lines}) do @echo {line}) 1>&2 & exit /b 3"
            : $"i=0; while [ $i -lt {lines} ]; do printf '%s\\n' '{line}'; i=$((i+1)); done; i=0; while [ $i -lt {lines} ]; do printf '%s\\n' '{line}' >&2; i=$((i+1)); done; exit 3";
        var result = await CliChildProcessRunner.RunAsync(ShellStart(command));

        Assert.Equal(3, result.ExitCode);
        Assert.True(result.StandardOutput.Length > 65536);
        Assert.True(result.StandardError.Length > 65536);
        Assert.Equal(expected, result.StandardOutput);
        Assert.Equal(expected, result.StandardError);
    }

    private static ProcessStartInfo ShellStart(string command)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
        }
        else
        {
            start.ArgumentList.Add("-c");
        }
        start.ArgumentList.Add(command);
        return start;
    }
}
