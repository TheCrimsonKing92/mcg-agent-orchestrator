using System.Diagnostics;

internal sealed class TestChildProcessCapture : IDisposable
{
    private const int DefaultRetentionBytes = 4 * 1024 * 1024;
    private readonly Process _process;
    private readonly ProcessStartInfo _startInfo;
    private readonly Func<Task, TimeSpan, bool> _wait;
    private readonly Action<Process> _stopTree;
    private readonly int _retentionBytes;
    private readonly TestChildProcessCaptureDiagnostics _diagnostics;
    private readonly Controls? _controls;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Task _exit;
    private readonly Task<StreamCapture> _stdout;
    private readonly Task<StreamCapture> _stderr;
    private readonly TaskCompletionSource<string> _overflow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopped;
    private bool _disposed;

    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    // Instance-scoped controls observe committed drain state; callbacks never run
    // under the diagnostic lock and cannot delay overflow publication/cleanup.
    internal sealed record Controls(
        Func<Stream, bool, Stream>? OpenStream = null,
        Action<bool, long, bool>? Progress = null);

    private sealed record StreamCapture(byte[] Bytes, long ObservedBytes);

    private TestChildProcessCapture(
        ProcessStartInfo startInfo,
        Process process,
        Func<Task, TimeSpan, bool> wait,
        Action<Process> stopTree,
        int retentionBytes,
        Controls? controls)
    {
        _startInfo = startInfo;
        _process = process;
        _wait = wait;
        _stopTree = stopTree;
        _retentionBytes = retentionBytes;
        _controls = controls;
        _diagnostics = new(process.StandardOutput.CurrentEncoding, process.StandardError.CurrentEncoding);
        _stdout = Task.Factory.StartNew(
            () => Drain(process.StandardOutput.BaseStream, true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _stderr = Task.Factory.StartNew(
            () => Drain(process.StandardError.BaseStream, false),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _exit = Task.Factory.StartNew(
            process.WaitForExit,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal static Result Run(
        ProcessStartInfo startInfo,
        TimeSpan? hangBound = null,
        Func<ProcessStartInfo, Process?>? start = null,
        Func<Task, TimeSpan, bool>? wait = null,
        Action<Process>? stopTree = null,
        int retentionBytes = DefaultRetentionBytes,
        Controls? controls = null)
    {
        var bound = hangBound ?? TestHangGuard.Bound;
        if (bound < TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(hangBound), "The hang bound must be at least 30 seconds.");

        using var capture = Start(startInfo, start, wait, stopTree, retentionBytes, controls);
        return capture.Complete(bound);
    }

    internal static TestChildProcessCapture Start(
        ProcessStartInfo startInfo,
        Func<ProcessStartInfo, Process?>? start = null,
        Func<Task, TimeSpan, bool>? wait = null,
        Action<Process>? stopTree = null,
        int retentionBytes = DefaultRetentionBytes,
        Controls? controls = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("Both child streams must be redirected.", nameof(startInfo));
        if (retentionBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(retentionBytes));

        var process = (start ?? Process.Start)(startInfo)
            ?? throw new InvalidOperationException($"Failed to start child {Describe(startInfo)}.");
        try
        {
            return new TestChildProcessCapture(startInfo, process,
                wait ?? ((task, bound) => task.Wait(bound)),
                stopTree ?? (child => child.Kill(entireProcessTree: true)), retentionBytes, controls);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }

    internal bool TryWaitForExit(TimeSpan bound)
    {
        if (!_wait(_exit, Remaining(bound)))
        {
            StopOwnedTree();
            return false;
        }
        _exit.GetAwaiter().GetResult();
        return true;
    }

    internal Result Complete(TimeSpan? hangBound = null)
    {
        var bound = hangBound ?? TestHangGuard.Bound;
        var all = Task.WhenAll(_exit, _stdout, _stderr);
        var first = Task.WhenAny(all, _overflow.Task);
        if (!_wait(first, Remaining(bound)))
            Fail("stdout/stderr or exit", bound);
        first.GetAwaiter().GetResult();
        if (_overflow.Task.IsCompletedSuccessfully)
            Fail(_overflow.Task.Result, bound);
        all.GetAwaiter().GetResult();

        var stdout = _stdout.Result;
        var stderr = _stderr.Result;
        if (stdout.ObservedBytes > _retentionBytes)
            Fail("stdout", bound);
        if (stderr.ObservedBytes > _retentionBytes)
            Fail("stderr", bound);

        return new Result(_process.ExitCode,
            Decode(stdout.Bytes, _process.StandardOutput.CurrentEncoding),
            Decode(stderr.Bytes, _process.StandardError.CurrentEncoding));
    }

    private StreamCapture Drain(Stream stream, bool isStdout)
    {
        using var retained = new MemoryStream();
        var buffer = new byte[8192];
        long observed = 0;
        int read;
        try
        {
            stream = _controls?.OpenStream?.Invoke(stream, isStdout) ?? stream;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                observed += read;
                _diagnostics.Publish(isStdout, buffer.AsSpan(0, read));
                var keep = (int)Math.Min(read, Math.Max(0, _retentionBytes - retained.Length));
                retained.Write(buffer, 0, keep);
                if (observed > _retentionBytes)
                {
                    _diagnostics.Freeze();
                    _overflow.TrySetResult(isStdout ? "stdout" : "stderr");
                    StopOwnedTree();
                }
                _controls?.Progress?.Invoke(isStdout, observed, false);
            }
            _diagnostics.End(isStdout);
            _controls?.Progress?.Invoke(isStdout, observed, true);
        }
        catch (Exception error)
        {
            _diagnostics.End(isStdout, unavailable: true);
            _controls?.Progress?.Invoke(isStdout, observed, false);
            if (Volatile.Read(ref _stopped) == 0 || error is not (IOException or ObjectDisposedException))
                throw;
        }
        return new StreamCapture(retained.ToArray(), observed);
    }

    private void Fail(string stream, TimeSpan bound)
    {
        var snapshot = _diagnostics.ReadFrozen();
        StopOwnedTree();
        Assert.Fail($"Child {Describe(_startInfo)} exceeded {bound.TotalSeconds:g} s while capturing {stream}; " +
            $"stdout={snapshot.StdoutBytes} bytes; stderr={snapshot.StderrBytes} bytes; " +
            $"retention limit={_retentionBytes} bytes.\n{snapshot.Stdout}\n{snapshot.Stderr}");
    }

    private void StopOwnedTree()
    {
        _diagnostics.Freeze();
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        try { _stopTree(_process); }
        catch (InvalidOperationException) { } // The child may have exited just before the stop.
    }

    private TimeSpan Remaining(TimeSpan bound) =>
        bound - _elapsed.Elapsed > TimeSpan.Zero ? bound - _elapsed.Elapsed : TimeSpan.Zero;

    private static string Decode(byte[] bytes, System.Text.Encoding encoding)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Describe(ProcessStartInfo startInfo) =>
        $"'{startInfo.FileName}' " +
        (startInfo.ArgumentList.Count > 0 ? string.Join(" ", startInfo.ArgumentList) : startInfo.Arguments);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_process.HasExited)
            StopOwnedTree();
        try { Task.WhenAll(_exit, _stdout, _stderr).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { } // Preserve the failure that caused disposal.
        _process.Dispose();
    }
}
