using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TestChildProcessCaptureTests
{
    // Parallel-safe: every child, input pipe, event and controlled stream is owned
    // by one test; no process enumeration or global environment mutation.
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
        using var ready = new CountdownEvent(2);
        var info = Shell("[Console]::Out.Write('stdout-phase'); [Console]::Out.Flush(); " +
            "[Console]::Error.Write('stderr-phase'); [Console]::Error.Flush(); [Console]::In.ReadLine() | Out-Null");
        var stdoutReady = 0;
        var stderrReady = 0;
        var terminated = false;
        var error = Record.Exception(() => TestChildProcessCapture.Run(info,
            start: startInfo => owned = Process.Start(startInfo),
            wait: (_, _) => { Await(ready.WaitHandle, "both real child markers captured"); return false; },
            stopTree: child =>
            {
                stopped = child;
                child.Kill(entireProcessTree: true);
                terminated = child.WaitForExit((int)TestHangGuard.Bound.TotalMilliseconds);
            },
            controls: new(Progress: (stdout, bytes, _) =>
            {
                if (stdout && bytes >= 12 && Interlocked.Exchange(ref stdoutReady, 1) == 0)
                    ready.Signal();
                if (!stdout && bytes >= 12 && Interlocked.Exchange(ref stderrReady, 1) == 0)
                    ready.Signal();
            })));

        Assert.NotNull(error);
        Assert.Same(owned, stopped);
        Assert.True(terminated, "owned child exit after stop");
        Assert.IsType<Xunit.Sdk.FailException>(error);
        Assert.Contains(info.FileName, error.Message, StringComparison.Ordinal);
        Assert.Contains("-Command", error.Message, StringComparison.Ordinal);
        Assert.Contains("stdout/stderr", error.Message, StringComparison.Ordinal);
        Assert.Contains("stdout=", error.Message, StringComparison.Ordinal);
        Assert.Contains("stderr=", error.Message, StringComparison.Ordinal);
        Assert.Contains("bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("stdout=12 bytes; stderr=12 bytes; retention limit=4194304 bytes.", error.Message);
        Assert.Equal("stdout-phase", SnapshotText(error.Message, "stdout"));
        Assert.Equal("stderr-phase", SnapshotText(error.Message, "stderr"));
        Assert.Contains("stdout snapshot [nonempty, incomplete, untruncated]:", error.Message);
        Assert.Contains("stderr snapshot [nonempty, incomplete, untruncated]:", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetentionOverflowStopsTheOwnedChildAndReportsTheStreamAndByteCount(bool stdout)
    {
        Process? owned = null;
        Process? stopped = null;
        var target = stdout ? "Out" : "Error";
        var info = Shell($"[Console]::{target}.Write('X' * 4096); [Console]::{target}.Flush(); " +
            "[Console]::In.ReadLine() | Out-Null");
        var stops = 0;
        var error = Record.Exception(() => TestChildProcessCapture.Run(info,
            start: startInfo => owned = Process.Start(startInfo),
            wait: (task, bound) => task.Wait(bound),
            stopTree: child => { stopped = child; Interlocked.Increment(ref stops); child.Kill(entireProcessTree: true); },
            retentionBytes: 1024));

        Assert.NotNull(error);
        Assert.Same(owned, stopped);
        Assert.Equal(1, stops);
        Assert.IsType<Xunit.Sdk.FailException>(error);
        Assert.Contains(info.FileName, error.Message, StringComparison.Ordinal);
        Assert.Contains("-Command", error.Message, StringComparison.Ordinal);
        Assert.Contains(stdout ? "stdout" : "stderr", error.Message, StringComparison.Ordinal);
        Assert.Contains($"while capturing {(stdout ? "stdout" : "stderr")};", error.Message);
        var count = System.Text.RegularExpressions.Regex.Match(error.Message,
            (stdout ? "stdout" : "stderr") + "=(\\d+) bytes");
        Assert.True(count.Success, error.Message);
        Assert.True(long.Parse(count.Groups[1].Value) > 1024, error.Message);
        Assert.Contains("retention limit=1024 bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(new string('X', (int)long.Parse(count.Groups[1].Value)),
            SnapshotText(error.Message, stdout ? "stdout" : "stderr"));
        Assert.Contains($"{(stdout ? "stdout" : "stderr")} snapshot [nonempty, incomplete, untruncated]:", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptySnapshot_DistinguishesEofFromAnOpenStream(bool stdoutEof)
    {
        using var stdout = new ControlledStream();
        using var stderr = new ControlledStream();
        using var eof = new ManualResetEventSlim();
        if (stdoutEof) stdout.End();
        var error = CaptureFailure(stdout, stderr, controls: new(
            Progress: (output, _, complete) => { if (output && complete) eof.Set(); }),
            ready: () =>
            {
                Await(stdout.Entered.WaitHandle, "stdout read entered");
                Await(stderr.Entered.WaitHandle, "stderr read entered");
                if (stdoutEof) Await(eof.WaitHandle, "stdout EOF published");
            });

        Assert.Contains($"stdout snapshot [empty, {(stdoutEof ? "complete" : "incomplete")}, untruncated]:", error.Message);
        Assert.Contains("stderr snapshot [empty, incomplete, untruncated]:", error.Message);
        Assert.Equal("", SnapshotText(error.Message, "stdout"));
        Assert.Equal("", SnapshotText(error.Message, "stderr"));
        Assert.Contains("stdout=0 bytes; stderr=0 bytes", error.Message);
    }

    [Fact]
    public void ConcurrentDrains_PreserveLatestBoundedTails()
    {
        var stdoutText = "old-out:" + new string('O', 24000) + ":last-out";
        var stderrText = "old-err:" + new string('E', 24000) + ":last-err";
        using var stdout = new ControlledStream(Encoding.UTF8.GetBytes(stdoutText));
        using var stderr = new ControlledStream(Encoding.UTF8.GetBytes(stderrText));
        using var entered = new Barrier(2);
        using var ready = new CountdownEvent(2);
        var error = CaptureFailure(stdout, stderr, controls: new(
            OpenStream: (_, output) =>
            {
                if (!entered.SignalAndWait(TestHangGuard.Bound))
                    throw new IOException("both drain threads did not enter");
                return output ? stdout : stderr;
            },
            Progress: (output, bytes, complete) =>
            {
                if (!complete && bytes == Encoding.UTF8.GetByteCount(output ? stdoutText : stderrText)) ready.Signal();
            }), ready: () => Await(ready.WaitHandle, "both concurrent tails published"));

        Assert.Equal(stdoutText[^4096..], SnapshotText(error.Message, "stdout"));
        Assert.Equal(stderrText[^4096..], SnapshotText(error.Message, "stderr"));
        Assert.Contains("stdout snapshot [nonempty, incomplete, truncated]:", error.Message);
        Assert.Contains("stderr snapshot [nonempty, incomplete, truncated]:", error.Message);
        Assert.Contains($"stdout={stdoutText.Length} bytes; stderr={stderrText.Length} bytes", error.Message);
    }

    [Fact]
    public void FailureSnapshot_IsFrozenBeforeCleanupPublishesMoreData()
    {
        using var stdout = new ControlledStream(Encoding.UTF8.GetBytes("before-stop"));
        using var stderr = new ControlledStream();
        using var before = new ManualResetEventSlim();
        using var after = new CountdownEvent(2);
        var cleanupObserved = false;
        var error = CaptureFailure(stdout, stderr,
            controls: new(Progress: (output, bytes, complete) =>
            {
                if (output && bytes == 11 && !complete) before.Set();
                if (complete) after.Signal();
            }),
            ready: () => Await(before.WaitHandle, "pre-stop bytes published"),
            cleanup: () =>
            {
                stdout.Add(Encoding.UTF8.GetBytes("after-stop"));
                stdout.End();
                stderr.Add(Encoding.UTF8.GetBytes("cleanup-stderr"));
                stderr.End();
                Await(after.WaitHandle, "cleanup bytes and both EOFs observed");
                cleanupObserved = true;
            });

        Assert.True(cleanupObserved);
        Assert.Equal("before-stop", SnapshotText(error.Message, "stdout"));
        Assert.Equal("", SnapshotText(error.Message, "stderr"));
        Assert.Contains("stdout=11 bytes; stderr=0 bytes", error.Message);
        Assert.Contains("stdout snapshot [nonempty, incomplete, untruncated]:", error.Message);
        Assert.Contains("stderr snapshot [empty, incomplete, untruncated]:", error.Message);
    }

    [Fact]
    public void FailedRead_ReportsUnavailableWithoutReplacingTheTimeoutAssertion()
    {
        using var stdout = new ControlledStream();
        using var stderr = new ControlledStream();
        using var failed = new ManualResetEventSlim();
        stdout.FailRead();
        var error = CaptureFailure(stdout, stderr,
            controls: new(Progress: (output, _, _) => { if (output) failed.Set(); }),
            ready: () => Await(failed.WaitHandle, "stdout read failure published"));

        Assert.Contains("stdout snapshot [empty, incomplete, untruncated, unavailable]:", error.Message);
        Assert.Equal("", SnapshotText(error.Message, "stdout"));
        Assert.Contains("while capturing stdout/stderr or exit", error.Message);
        Assert.DoesNotContain("private read failure", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void OrdinaryExit_PreservesFullResultsBeyondTheDiagnosticWindow(int exitCode)
    {
        var result = TestChildProcessCapture.Run(Shell(
            $"[Console]::Out.Write('S' * 18000); [Console]::Error.Write('E' * 19000); exit {exitCode}"));
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(new string('S', 18000), result.Stdout);
        Assert.Equal(new string('E', 19000), result.Stderr);
    }

    [Fact]
    public void SplitUtf8_ReadsDecodeAcrossChunksAndReplaceTheIncompleteSuffix()
    {
        using var stdout = new ControlledStream([0x41, 0xF0], [0x9F], [0x98, 0x80, 0xE2, 0x82]);
        using var stderr = new ControlledStream();
        using var ready = new ManualResetEventSlim();
        var error = CaptureFailure(stdout, stderr,
            controls: new(Progress: (output, bytes, _) => { if (output && bytes == 7) ready.Set(); }),
            ready: () => Await(ready.WaitHandle, "all split UTF-8 bytes published"));
        Assert.Equal("A\U0001F600\uFFFD", SnapshotText(error.Message, "stdout"));
        Assert.Contains("stdout snapshot [nonempty, incomplete, untruncated]:", error.Message);
    }

    [Fact]
    public void WrappedByteWindow_DecodesAScalarAcrossThePhysicalRingBoundary()
    {
        using var stdout = new ControlledStream(Encoding.UTF8.GetBytes(new string('A', 16382)),
            Encoding.UTF8.GetBytes("\U0001F600"));
        using var stderr = new ControlledStream();
        using var ready = new ManualResetEventSlim();
        var error = CaptureFailure(stdout, stderr,
            controls: new(Progress: (output, bytes, _) => { if (output && bytes == 16386) ready.Set(); }),
            ready: () => Await(ready.WaitHandle, "UTF-8 scalar published across ring wrap"));
        Assert.Equal(new string('A', 4094) + "\U0001F600", SnapshotText(error.Message, "stdout"));
        Assert.Contains("stdout snapshot [nonempty, incomplete, truncated]:", error.Message);
    }

    [Fact]
    public void WrappedUtf8Tail_PreservesLatestScalarsWithoutSplittingSurrogates()
    {
        // 4096 four-byte scalars plus two bytes forces the byte tail to begin
        // with the last two continuation bytes of the first scalar.
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\U0001F600", 4096)));
        var diagnostics = new TestChildProcessCaptureDiagnostics(Encoding.UTF8, Encoding.UTF8);
        diagnostics.Publish(true, bytes);
        diagnostics.Publish(true, [0xE2, 0x82]);
        var snapshot = diagnostics.ReadFrozen();
        var text = SnapshotText(snapshot.Stdout + "\n" + snapshot.Stderr, "stdout");
        // Character trimming also omits the partial first scalar. Its fallback
        // is pinned independently below, without hiding it behind that window.
        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 2047)) + "\uFFFD", text);
        Assert.Contains("stdout snapshot [nonempty, incomplete, truncated]:", snapshot.Stdout);
    }

    [Fact]
    public void Utf8Snapshot_ReplacesMalformedPrefixAndSuffixEvenWithStrictEncoding()
    {
        var diagnostics = new TestChildProcessCaptureDiagnostics(new UTF8Encoding(false, true), Encoding.UTF8);
        // A tail starting at either continuation byte uses explicit U+FFFD
        // replacement, never strict-decoder exceptions or dropped bytes.
        diagnostics.Publish(true, [0x98, 0x80, 0x41, 0xE2, 0x82]);
        var snapshot = diagnostics.ReadFrozen();
        Assert.Equal("\uFFFD\uFFFDA\uFFFD", SnapshotText(snapshot.Stdout + "\n" + snapshot.Stderr, "stdout"));
        Assert.DoesNotContain("unavailable", snapshot.Stdout);
    }

    [Fact]
    public void Overflow_FreezesTheLatestTailBeforeFurtherDrainProgress()
    {
        var payload = Encoding.UTF8.GetBytes("old:" + new string('X', 8180) + ":tailend");
        using var stdout = new ControlledStream(payload);
        using var stderr = new ControlledStream();
        var stops = 0;
        var error = Record.Exception(() => TestChildProcessCapture.Run(Shell("[Console]::In.ReadLine() | Out-Null"),
            controls: new(OpenStream: (_, output) => output ? stdout : stderr),
            retentionBytes: 1024,
            stopTree: child =>
            {
                Interlocked.Increment(ref stops);
                stdout.Add(Encoding.UTF8.GetBytes("after-overflow"));
                stdout.End();
                stderr.End();
                child.Kill(entireProcessTree: true);
            }));
        Assert.IsType<Xunit.Sdk.FailException>(error);
        Assert.Equal(1, stops);
        Assert.Contains("while capturing stdout; stdout=8192 bytes", error!.Message);
        Assert.Contains("retention limit=1024 bytes", error.Message);
        Assert.Equal(Encoding.UTF8.GetString(payload)[^4096..], SnapshotText(error.Message, "stdout"));
        Assert.Contains("stdout snapshot [nonempty, incomplete, truncated]:", error.Message);
    }

    [Fact]
    public void RepeatedFailureAndDispose_StopOnlyTheOriginalOwnedProcessOnce()
    {
        Process? owned = null;
        var stops = 0;
        var exitedDuringStop = false;
        var capture = TestChildProcessCapture.Start(Shell("[Console]::In.ReadLine() | Out-Null"),
            start: info => owned = Process.Start(info),
            wait: (_, _) => false,
            stopTree: child =>
            {
                Assert.Same(owned, child);
                stops++;
                child.StandardInput.WriteLine("release");
                child.StandardInput.Flush();
                exitedDuringStop = child.WaitForExit((int)TestHangGuard.Bound.TotalMilliseconds);
                if (!exitedDuringStop) child.Kill(entireProcessTree: true);
                // The existing stop policy tolerates an exit race.
                throw new InvalidOperationException("owned child already exited");
            });
        try
        {
            Assert.False(capture.TryWaitForExit(TestHangGuard.Bound));
            Assert.False(capture.TryWaitForExit(TestHangGuard.Bound));
            Assert.IsType<Xunit.Sdk.FailException>(Record.Exception(() => capture.Complete()));
        }
        finally
        {
            capture.Dispose();
            capture.Dispose();
        }
        Assert.True(exitedDuringStop, "owned child exited during failure cleanup");
        Assert.Equal(1, stops);
    }

    private static Exception CaptureFailure(ControlledStream stdout, ControlledStream stderr,
        TestChildProcessCapture.Controls controls, Action ready, Action? cleanup = null)
    {
        var stops = 0;
        var terminated = false;
        Process? owned = null;
        var error = Record.Exception(() => TestChildProcessCapture.Run(Shell("[Console]::In.ReadLine() | Out-Null"),
            start: info => owned = Process.Start(info),
            controls: controls with { OpenStream = controls.OpenStream ?? ((_, output) => output ? stdout : stderr) },
            wait: (_, _) => { ready(); return false; },
            stopTree: child =>
            {
                Assert.Same(owned, child);
                Interlocked.Increment(ref stops);
                try { cleanup?.Invoke(); }
                finally
                {
                    stdout.End();
                    stderr.End();
                    child.Kill(entireProcessTree: true);
                    terminated = child.WaitForExit((int)TestHangGuard.Bound.TotalMilliseconds);
                }
            }));
        Assert.Equal(1, stops);
        Assert.True(terminated, "owned child exit after stop");
        Assert.IsType<Xunit.Sdk.FailException>(error);
        return error!;
    }

    private static string SnapshotText(string message, string stream)
    {
        var start = message.IndexOf(stream + " snapshot [", StringComparison.Ordinal);
        Assert.True(start >= 0, message);
        start = message.IndexOf("]:\n", start, StringComparison.Ordinal) + 3;
        var end = stream == "stdout" ? message.IndexOf("\nstderr snapshot [", start, StringComparison.Ordinal) : message.Length;
        Assert.True(end >= start, message);
        return message[start..end];
    }

    private static void Await(WaitHandle signal, string name) =>
        Assert.True(signal.WaitOne(TestHangGuard.Bound), $"Hang guard: {name}");

    private sealed class ControlledStream(params byte[][] chunks) : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = CreateQueue(chunks);
        private byte[]? _current;
        private int _offset;
        private bool _fail;
        internal ManualResetEventSlim Entered { get; } = new();
        internal void Add(byte[] bytes) => _chunks.Add(bytes);
        internal void End() { if (!_chunks.IsAddingCompleted) _chunks.CompleteAdding(); }
        internal void FailRead() { _fail = true; End(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Entered.Set();
            if (_fail) throw new IOException("private read failure");
            if (_current == null || _offset == _current.Length)
            {
                if (!_chunks.TryTake(out _current, TestHangGuard.Bound))
                {
                    if (_chunks.IsCompleted) return 0;
                    throw new IOException("Hang guard: controlled read was not released");
                }
                _offset = 0;
            }
            var read = Math.Min(count, _current.Length - _offset);
            Array.Copy(_current, _offset, buffer, offset, read);
            _offset += read;
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) { End(); _chunks.Dispose(); Entered.Dispose(); } base.Dispose(disposing); }
        private static BlockingCollection<byte[]> CreateQueue(byte[][] chunks)
        {
            var queue = new BlockingCollection<byte[]>();
            foreach (var chunk in chunks) queue.Add(chunk);
            return queue;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static ProcessStartInfo Shell(string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var inherited = new Dictionary<string, string?>();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "COMSPEC", "HOME", "USERPROFILE", "DOTNET_ROOT" })
            inherited[name] = Environment.GetEnvironmentVariable(name);
        info.Environment.Clear();
        foreach (var (name, value) in inherited)
            if (value != null) info.Environment[name] = value;
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        return info;
    }
}
