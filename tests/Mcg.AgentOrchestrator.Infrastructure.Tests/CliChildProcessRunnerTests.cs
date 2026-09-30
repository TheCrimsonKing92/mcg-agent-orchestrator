using System.Diagnostics;
using Xunit;

public sealed class CliChildProcessRunnerTests
{
    [Fact]
    public async Task HangingChildIsTreeKilledAndNamedWhenInjectedGuardExpires()
    {
        Assert.True(CliChildProcessRunner.DefaultHangGuard >= TimeSpan.FromSeconds(120));
        var marker = Path.Combine(Path.GetTempPath(), $"mcg-cli-child-{Guid.NewGuid():N}.pid");
        var temporaryMarker = marker + ".tmp";
        var markerWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(marker)!, Path.GetFileName(marker));
        watcher.Created += (_, _) => markerWritten.TrySetResult();
        watcher.Renamed += (_, _) => markerWritten.TrySetResult();
        watcher.EnableRaisingEvents = true;

        var start = TreeStart(marker, temporaryMarker);
        Process? child = null;
        Process? descendant = null;
        try
        {
            var run = CliChildProcessRunner.RunAsync(start, TimeSpan.FromSeconds(10),
                process => child = Process.GetProcessById(process.Id));
            await markerWritten.Task.WaitAsync(TestHangGuard.Bound);
            descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            Assert.False(descendant.HasExited);

            var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
                await run.WaitAsync(TestHangGuard.Bound));
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
                if (!descendant.HasExited)
                {
                    descendant.Kill(entireProcessTree: true);
                    await descendant.WaitForExitAsync();
                }
                descendant.Dispose();
            }
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
            File.Delete(marker);
            File.Delete(temporaryMarker);
        }
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
            start.ArgumentList.Add("$p = Start-Process ping.exe -ArgumentList '-t 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                $"Set-Content -LiteralPath '{temporaryMarker.Replace("'", "''")}' -Value $p.Id; " +
                $"Move-Item -LiteralPath '{temporaryMarker.Replace("'", "''")}' -Destination '{marker.Replace("'", "''")}' ; " +
                "Write-Output started; Wait-Process -Id $p.Id");
            return start;
        }

        var shellMarker = marker.Replace("'", "'\"'\"'");
        var shellTemporaryMarker = temporaryMarker.Replace("'", "'\"'\"'");
        return ShellStart($"sleep 3600 & child=$!; printf '%s' \"$child\" > '{shellTemporaryMarker}'; mv '{shellTemporaryMarker}' '{shellMarker}'; echo started; wait \"$child\"");
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
