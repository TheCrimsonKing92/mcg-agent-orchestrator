using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class AcceptanceOutputCaptureTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public void AcceptanceProcessUsesOwnedStartCompatibleCaptureTransport()
    {
        const string stdoutTarget = @"\\.\pipe\mcg-test-stdout";
        const string stderrTarget = @"\\.\pipe\mcg-test-stderr";
        var startInfo = GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(
            ["dotnet", "test"],
            Path.GetTempPath(),
            stdoutTarget,
            stderrTarget);

        Xunit.Assert.Equal(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", startInfo.FileName);
        var command = OperatingSystem.IsWindows()
            ? startInfo.Arguments
            : startInfo.ArgumentList.Last();
        if (OperatingSystem.IsWindows())
        {
            Xunit.Assert.False(startInfo.RedirectStandardOutput);
            Xunit.Assert.False(startInfo.RedirectStandardError);
            Xunit.Assert.Contains(stdoutTarget, command, StringComparison.Ordinal);
            Xunit.Assert.Contains(stderrTarget, command, StringComparison.Ordinal);
        }
        else
        {
            Xunit.Assert.True(startInfo.RedirectStandardOutput);
            Xunit.Assert.True(startInfo.RedirectStandardError);
            Xunit.Assert.DoesNotContain(" > ", command, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("2>", command, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public async Task CappedShellCapturePreservesHeadWritesOneTerminatorAndReportsLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-capped-capture-{Guid.NewGuid():N}.out");
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));
        try
        {
            var limitNotifications = 0;
            await using var source = new MemoryStream(payload);
            var result = await GoalAcceptanceVerifier.DrainCappedCaptureAsync(
                source,
                path,
                limitBytes: 512,
                utcNow: () => DateTimeOffset.UnixEpoch,
                onLimitReached: () =>
                {
                    limitNotifications++;
                },
                CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            var text = Encoding.UTF8.GetString(bytes);
            Xunit.Assert.True(result.LimitReached);
            Xunit.Assert.Equal(payload.Length, result.WrittenBytes);
            Xunit.Assert.Equal(1, limitNotifications);
            Xunit.Assert.Equal(512, bytes.Length);
            Xunit.Assert.StartsWith(new string('x', 32), text, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, CountOccurrences(text, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));
            Xunit.Assert.Contains("cap_bytes=512", text, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task BelowCapReadPreservesOutputLargerThanPreviewByteForByte()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-below-cap-capture-{Guid.NewGuid():N}.out");
        var payload = Enumerable.Range(0, 96 * 1024)
            .Select(index => (byte)('a' + index % 26))
            .ToArray();
        try
        {
            await using var source = new MemoryStream(payload);
            var result = await GoalAcceptanceVerifier.DrainCappedCaptureAsync(
                source,
                path,
                limitBytes: payload.Length + 1,
                utcNow: () => DateTimeOffset.UnixEpoch,
                onLimitReached: null,
                CancellationToken.None);

            var output = await GoalAcceptanceVerifier.ReadCapturedFileWithRetryAsync(
                path,
                captureLimitReached: false);

            Xunit.Assert.False(result.LimitReached);
            Xunit.Assert.Equal(payload.Length, result.WrittenBytes);
            Xunit.Assert.Equal(Encoding.UTF8.GetString(payload), output);
            Xunit.Assert.DoesNotContain("captured output omitted", output, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task RedirectedProcessKeepsDrainingAfterLimitWhileCaptureStaysBounded()
    {
        const long limitBytes = 512;
        const long producedBytes = 1024 * 1024;
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-draining-capture-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-draining-capture-{Guid.NewGuid():N}.err");
        try
        {
            string[] arguments;
            if (OperatingSystem.IsWindows())
            {
                arguments =
                [
                    "powershell",
                    "-NoProfile",
                    "-Command",
                    "$stream=[Console]::OpenStandardOutput(); $bytes=[byte[]]::new(65536); " +
                    "for($i=0;$i -lt 16;$i++){ $stream.Write($bytes,0,$bytes.Length); $stream.Flush() }; " +
                    "[Console]::In.ReadLine() | Out-Null"
                ];
            }
            else
            {
                arguments = ["/bin/sh", "-c", "dd if=/dev/zero bs=65536 count=16 2>/dev/null; read line"];
            }

            var startInfo = GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(arguments, Path.GetTempPath());
            startInfo.RedirectStandardInput = true;
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start redirected capture producer.");
            var limitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdoutDrain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
                process.StandardOutput.BaseStream,
                stdoutPath,
                limitBytes,
                () => DateTimeOffset.UnixEpoch,
                () => limitReached.TrySetResult(),
                CancellationToken.None);
            var stderrDrain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
                process.StandardError.BaseStream,
                stderrPath,
                limitBytes,
                () => DateTimeOffset.UnixEpoch,
                onLimitReached: null,
                CancellationToken.None);

            await limitReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.False(process.HasExited);

            await process.StandardInput.WriteLineAsync(string.Empty);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var stdoutResult = await stdoutDrain.WaitAsync(TimeSpan.FromSeconds(30));
            await stderrDrain.WaitAsync(TimeSpan.FromSeconds(30));

            var capture = await File.ReadAllTextAsync(stdoutPath);
            Xunit.Assert.Equal(0, process.ExitCode);
            Xunit.Assert.True(stdoutResult.LimitReached);
            Xunit.Assert.Equal(producedBytes, stdoutResult.WrittenBytes);
            Xunit.Assert.Equal(limitBytes, new FileInfo(stdoutPath).Length);
            Xunit.Assert.Contains($"written_bytes={producedBytes}", capture, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, CountOccurrences(capture, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));
        }
        finally
        {
            try { File.Delete(stdoutPath); } catch { }
            try { File.Delete(stderrPath); } catch { }
        }
    }

    [Xunit.Fact]
    public void CaptureLimitEventIsTypedAndCarriesGoalRunPathAndCap()
    {
        using var writer = new StringWriter();

        GoalAcceptanceVerifier.EmitCaptureLimitReached(
            "0123456789abcdef",
            Path.Combine("attempts", "run-42"),
            @"C:\temp\capture.out",
            1024,
            writer);

        var line = writer.ToString().Trim();
        Xunit.Assert.StartsWith("ACCEPTANCE_CAPTURE_LIMIT_REACHED ", line, StringComparison.Ordinal);
        Xunit.Assert.Contains("goal=01234567", line, StringComparison.Ordinal);
        Xunit.Assert.Contains("run=run-42", line, StringComparison.Ordinal);
        Xunit.Assert.Contains("capture.out", line, StringComparison.Ordinal);
        Xunit.Assert.Contains("cap_bytes=1024", line, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string needle) =>
        value.Split(needle, StringSplitOptions.None).Length - 1;
}
