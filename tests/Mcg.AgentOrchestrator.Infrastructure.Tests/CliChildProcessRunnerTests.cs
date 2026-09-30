using System.Diagnostics;
using Xunit;

public sealed class CliChildProcessRunnerTests
{
    [Fact]
    public async Task HangingChildIsTreeKilledAndNamedWhenInjectedGuardExpires()
    {
        Assert.True(CliChildProcessRunner.DefaultHangGuard >= TimeSpan.FromSeconds(120));
        // Block in the child itself so a descendant cannot retain a redirected pipe after the kill.
        var start = ShellStart(OperatingSystem.IsWindows()
            ? "echo started& for /L %i in (1,0,2) do @set x=1"
            : "echo started; exec sleep 3600");
        Process? child = null;
        try
        {
            var run = CliChildProcessRunner.RunAsync(start, TimeSpan.FromSeconds(2),
                process => child = Process.GetProcessById(process.Id));
            var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
                await run.WaitAsync(TestHangGuard.Bound));
            Assert.Contains(start.FileName, error.Message, StringComparison.Ordinal);
            Assert.Contains("did not exit", error.Message, StringComparison.Ordinal);
            Assert.Contains("stdout=started", error.Message, StringComparison.Ordinal);
            Assert.Contains("stderr=", error.Message, StringComparison.Ordinal);
            Assert.NotNull(child);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
        }
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
