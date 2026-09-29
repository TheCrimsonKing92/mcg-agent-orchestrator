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
    public async Task ConsumedSubBufferOutputIsPublishedWhileSourceRemainsOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-live-capture-{Guid.NewGuid():N}.out");
        var payload = Encoding.UTF8.GetBytes("acceptance output is visible before EOF");
        await using var source = new HeldOpenAfterPayloadStream(payload);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayRequested = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var drain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
            source,
            path,
            limitBytes: 1024,
            utcNow: () => DateTimeOffset.UnixEpoch,
            onLimitReached: null,
            CancellationToken.None,
            publicationDelaySource: (interval, token) =>
            {
                token.Register(() => published.TrySetResult());
                delayRequested.TrySetResult(interval);
                return releasePublication.Task;
            });
        try
        {
            await TestHangGuard.WaitAsync(source.SecondReadEntered, "capture source second read");
            Xunit.Assert.Equal(TimeSpan.FromSeconds(1),
                await TestHangGuard.WaitAsync(delayRequested.Task, "capture publication delay request"));
            Xunit.Assert.Equal(0, GetVisibleLength(path));
            releasePublication.TrySetResult();
            await TestHangGuard.WaitAsync(published.Task, "capture publication flush after injected delay");
            Xunit.Assert.False(source.EofReleased);
            Xunit.Assert.Equal(payload, ReadVisiblePayload(path));
        }
        finally
        {
            source.ReleaseEof();
            await TestHangGuard.WaitAsync(drain, "capture drain after EOF release");
            try { File.Delete(path); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task TerminalOnlyFlushControlWithholdsLiveBytesButPreservesFinalOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-terminal-flush-control-{Guid.NewGuid():N}.out");
        var payload = Encoding.UTF8.GetBytes("terminal flush still preserves exact output");
        await using var source = new HeldOpenAfterPayloadStream(payload);
        var delayRequests = 0;
        var drain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
            source,
            path,
            limitBytes: 1024,
            utcNow: () => DateTimeOffset.UnixEpoch,
            onLimitReached: null,
            CancellationToken.None,
            publicationInterval: Timeout.InfiniteTimeSpan,
            publicationDelaySource: (interval, token) =>
            {
                Interlocked.Increment(ref delayRequests);
                return Task.CompletedTask;
            });
        try
        {
            await TestHangGuard.WaitAsync(source.SecondReadEntered, "terminal-only capture source second read");
            Xunit.Assert.Equal(0, Volatile.Read(ref delayRequests));
            Xunit.Assert.Equal(0, GetVisibleLength(path));

            source.ReleaseEof();
            var result = await TestHangGuard.WaitAsync(drain, "capture drain after EOF release");
            Xunit.Assert.Equal(payload.Length, result.WrittenBytes);
            Xunit.Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            source.ReleaseEof();
            await TestHangGuard.WaitAsync(drain, "capture drain after EOF release");
            try { File.Delete(path); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task ConcurrentStdoutAndStderrPublishIndependentlyWhileBothSourcesRemainOpen()
    {
        var stdoutPath = Path.Combine(Path.GetTempPath(), $"mcg-live-stdout-{Guid.NewGuid():N}.out");
        var stderrPath = Path.Combine(Path.GetTempPath(), $"mcg-live-stderr-{Guid.NewGuid():N}.err");
        var stdoutPayload = Encoding.UTF8.GetBytes("stdout-live-payload");
        var stderrPayload = Encoding.UTF8.GetBytes("stderr-independent-payload");
        await using var stdoutSource = new HeldOpenAfterPayloadStream(stdoutPayload);
        await using var stderrSource = new HeldOpenAfterPayloadStream(stderrPayload);
        var stdoutVisible = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrVisible = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stdoutWatcher = WatchVisiblePayload(stdoutPath, stdoutPayload.Length, stdoutVisible);
        using var stderrWatcher = WatchVisiblePayload(stderrPath, stderrPayload.Length, stderrVisible);
        var stdoutDrain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
            stdoutSource, stdoutPath, 1024, () => DateTimeOffset.UnixEpoch, null, CancellationToken.None,
            publicationInterval: TimeSpan.FromMilliseconds(50));
        var stderrDrain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
            stderrSource, stderrPath, 1024, () => DateTimeOffset.UnixEpoch, null, CancellationToken.None,
            publicationInterval: TimeSpan.FromMilliseconds(50));
        try
        {
            await Task.WhenAll(stdoutSource.SecondReadEntered, stderrSource.SecondReadEntered)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var published = await Task.WhenAll(stdoutVisible.Task, stderrVisible.Task)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Xunit.Assert.False(stdoutSource.EofReleased);
            Xunit.Assert.False(stderrSource.EofReleased);
            Xunit.Assert.Equal(stdoutPayload, published[0]);
            Xunit.Assert.Equal(stderrPayload, published[1]);
            Xunit.Assert.NotEqual(published[0], published[1]);
        }
        finally
        {
            stdoutSource.ReleaseEof();
            stderrSource.ReleaseEof();
            await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(TimeSpan.FromSeconds(5));
            try { File.Delete(stdoutPath); } catch { }
            try { File.Delete(stderrPath); } catch { }
        }
    }

    [Xunit.Fact]
    public async Task CancellationWhileWaitingForMoreBytesPublishesConsumedBytesAndCompletesNormally()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-cancelled-live-capture-{Guid.NewGuid():N}.out");
        var payload = Encoding.UTF8.GetBytes("consumed bytes survive drain cancellation");
        await using var source = new HeldOpenAfterPayloadStream(payload);
        using var cancellation = new CancellationTokenSource();
        var drain = GoalAcceptanceVerifier.DrainCappedCaptureAsync(
            source,
            path,
            limitBytes: 1024,
            utcNow: () => DateTimeOffset.UnixEpoch,
            onLimitReached: null,
            cancellation.Token);
        try
        {
            await source.SecondReadEntered.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var result = await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Xunit.Assert.Equal(payload.Length, result.WrittenBytes);
            Xunit.Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            source.ReleaseEof();
            try { await drain.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
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

    private static long GetVisibleLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return -1; }
    }

    private static byte[] ReadVisiblePayload(string path)
    {
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)reader.Length)];
        reader.ReadExactly(bytes);
        return bytes;
    }

    private static IDisposable WatchVisiblePayload(
        string path,
        int expectedLength,
        TaskCompletionSource<byte[]> completion) =>
        new VisiblePayloadProbe(path, expectedLength, completion);

    private static void TryReadVisiblePayload(
        string path,
        int expectedLength,
        TaskCompletionSource<byte[]> completion)
    {
        if (completion.Task.IsCompleted)
            return;

        try
        {
            using var reader = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (reader.Length != expectedLength)
                return;

            var bytes = new byte[expectedLength];
            reader.ReadExactly(bytes);
            completion.TrySetResult(bytes);
        }
        catch (IOException)
        {
            // A create/change notification can precede the shared handle becoming readable.
        }
    }

    private sealed class HeldOpenAfterPayloadStream(byte[] payload) : Stream
    {
        private readonly TaskCompletionSource _secondReadEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseEof =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        internal Task SecondReadEntered => _secondReadEntered.Task;
        internal bool EofReleased => _releaseEof.Task.IsCompleted;

        internal void ReleaseEof() => _releaseEof.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                payload.CopyTo(buffer);
                return payload.Length;
            }

            _secondReadEntered.TrySetResult();
            await _releaseEof.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class VisiblePayloadProbe : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;

        internal VisiblePayloadProbe(
            string path,
            int expectedLength,
            TaskCompletionSource<byte[]> completion)
        {
            void Probe() => TryReadVisiblePayload(path, expectedLength, completion);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Created += (_, _) => Probe();
            _watcher.Changed += (_, _) => Probe();
            _watcher.EnableRaisingEvents = true;
            _timer = new Timer(_ => Probe(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        }

        public void Dispose()
        {
            _timer.Dispose();
            _watcher.Dispose();
        }
    }
}
