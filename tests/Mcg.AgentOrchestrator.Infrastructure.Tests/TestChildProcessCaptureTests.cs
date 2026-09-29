using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestChildProcessCaptureTests
{
    [Fact]
    public void StderrLargerThanPipeBufferIsDrainedBeforeStdoutAndExit()
    {
        const int length = 1024 * 1024 + 4096;
        var result = TestChildProcessCapture.Run(Shell(
            $"[Console]::Error.Write('E' * {length}); [Console]::Out.Write('done'); exit 7"));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("done", result.Stdout);
        Assert.Equal(new string('E', length), result.Stderr);
    }

    [Fact]
    public void ExpiredHangBoundStopsTheOwnedChildAndNamesItsCapturedBytes()
    {
        Process? owned = null;
        Process? stopped = null;
        var info = Shell("[Console]::Out.Write('ready'); Start-Sleep -Seconds 120");
        var error = Record.Exception(() => TestChildProcessCapture.Run(info,
            start: startInfo => owned = Process.Start(startInfo),
            wait: (_, _) => false,
            stopTree: child =>
            {
                stopped = child;
                child.Kill(entireProcessTree: true);
            }));

        Assert.NotNull(error);
        Assert.Same(owned, stopped);
        Assert.Contains(info.FileName, error.Message, StringComparison.Ordinal);
        Assert.Contains("-Command", error.Message, StringComparison.Ordinal);
        Assert.Contains("stdout/stderr", error.Message, StringComparison.Ordinal);
        Assert.Contains("stdout=", error.Message, StringComparison.Ordinal);
        Assert.Contains("stderr=", error.Message, StringComparison.Ordinal);
        Assert.Contains("bytes", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetentionOverflowStopsTheOwnedChildAndReportsTheStreamAndByteCount(bool stdout)
    {
        Process? owned = null;
        Process? stopped = null;
        var target = stdout ? "Out" : "Error";
        var info = Shell($"[Console]::{target}.Write('X' * 4096)");
        var error = Record.Exception(() => TestChildProcessCapture.Run(info,
            start: startInfo => owned = Process.Start(startInfo),
            wait: (task, bound) => task.Wait(bound),
            stopTree: child => stopped = child,
            retentionBytes: 1024));

        Assert.NotNull(error);
        Assert.Same(owned, stopped);
        Assert.Contains(info.FileName, error.Message, StringComparison.Ordinal);
        Assert.Contains("-Command", error.Message, StringComparison.Ordinal);
        Assert.Contains(stdout ? "stdout" : "stderr", error.Message, StringComparison.Ordinal);
        var count = System.Text.RegularExpressions.Regex.Match(error.Message,
            (stdout ? "stdout" : "stderr") + "=(\\d+) bytes");
        Assert.True(count.Success, error.Message);
        Assert.True(long.Parse(count.Groups[1].Value) > 1024, error.Message);
        Assert.Contains("retention limit=1024 bytes", error.Message, StringComparison.Ordinal);
    }

    private static ProcessStartInfo Shell(string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        return info;
    }
}
