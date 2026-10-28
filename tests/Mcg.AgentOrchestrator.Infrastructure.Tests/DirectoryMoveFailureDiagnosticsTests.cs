using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: instance-local seams, virtual deadlines, and uniquely owned fixture paths.
public sealed class DirectoryMoveFailureDiagnosticsTests(Xunit.ITestOutputHelper output)
{
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);
    private static readonly string Source = Path.Combine(Path.GetTempPath(), "move-evidence-unit-" + Guid.NewGuid().ToString("N"), "source");
    private static readonly string Destination = Path.Combine(Path.GetDirectoryName(Source)!, "other", "destination");

    [Fact]
    public Task MoveFailure_CapturesBeforeCleanup() => CheckOrdering(new IOException("injected move denial"));

    [Fact]
    public Task AccessDenied_CapturesBeforeCleanup() => CheckOrdering(new UnauthorizedAccessException("injected access denial"));

    private async Task CheckOrdering(Exception primary)
    {
        var root = Path.Combine(Path.GetTempPath(), "move-evidence-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "parent", "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var events = new List<string>();
        var capturedWhilePresent = false;
        var publishedWhilePresent = false;
        string? snapshot = null;
        var helper = new DirectoryMoveFailureDiagnostics(readProbe: _ =>
        {
            capturedWhilePresent = Directory.Exists(source) && Directory.Exists(root);
            events.Add("capture");
            return Task.FromResult("observed");
        });
        var observed = await Record.ExceptionAsync(async () =>
        {
            try
            {
                await helper.MoveAsync(source, destination, text =>
                {
                    publishedWhilePresent = Directory.Exists(source) && Directory.Exists(root);
                    snapshot = text;
                    events.Add("publish");
                    output.WriteLine(text);
                }, (_, _) => { events.Add("move"); ThrowMove(primary); });
            }
            finally
            {
                events.Add("cleanup");
                _ = TempRootJanitor.DeleteTree(root);
            }
        });
        Assert.Same(primary, observed);
        Assert.True(capturedWhilePresent);
        Assert.True(publishedWhilePresent);
        Assert.Equal("move", events[0]);
        Assert.Contains("capture", events);
        Assert.True(events.IndexOf("capture") < events.IndexOf("publish"));
        Assert.Equal("cleanup", events[^1]);
        Assert.False(Directory.Exists(root));
        Assert.Contains("operation=\"Directory.Move\"", snapshot);
        Assert.Contains(source, snapshot!.Replace("\\\\", "\\", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveFailure_RethrowsSameException()
    {
        var inner = new Win32Exception(5, "native inner cause");
        var primary = new IOException("move failure", inner);
        var moves = 0;
        var (observed, snapshot) = await Fail(new DirectoryMoveFailureDiagnostics(_ => Task.FromResult("observed")),
            primary, () => moves++);
        Assert.Same(primary, observed);
        Assert.Same(inner, observed!.InnerException);
        Assert.Contains(nameof(ThrowMove), observed.StackTrace);
        Assert.Equal(1, moves);
        Assert.Contains("primary.type=\"System.IO.IOException\"", snapshot);
        Assert.Contains($"primary.hresult=\"0x{unchecked((uint)primary.HResult):X8}\"", snapshot);
        Assert.Contains("primary.inner.native=\"5 (Win32Exception.NativeErrorCode)\"", snapshot);
    }

    [Fact]
    public async Task PartialMetadata_DistinguishesMissingDeniedUnsupportedAndFailed()
    {
        var fields = new List<string>();
        var helper = new DirectoryMoveFailureDiagnostics(probe =>
        {
            fields.Add(probe.Field);
            return probe.Field switch
            {
                "source.metadata" => throw new DirectoryNotFoundException("missing source"),
                "destination.metadata" => throw new UnauthorizedAccessException("inspection denied"),
                "source.owner" => throw new PlatformNotSupportedException("no owner API"),
                "source.acl" => throw new InvalidOperationException("ACL probe failed"),
                "source.handles" => Task.FromResult("unavailable(native-helper-refused)"),
                _ => Task.FromResult("observed")
            };
        });
        var primary = new IOException("primary move failed");
        var (observed, snapshot) = await Fail(helper, primary);
        Assert.Same(primary, observed);
        Assert.Contains("source.metadata=\"missing(DirectoryNotFoundException", snapshot);
        Assert.Contains("destination.metadata=\"denied(UnauthorizedAccessException", snapshot);
        Assert.Contains("source.owner=\"unsupported(PlatformNotSupportedException", snapshot);
        Assert.Contains("source.acl=\"failed(InvalidOperationException", snapshot);
        Assert.Contains("source.handles=\"unavailable(native-helper-refused)\"", snapshot);
        Assert.Contains("source.parent.metadata", fields);
        Assert.Contains("destination.parent.metadata", fields);
        Assert.Equal(fields.Count, fields.Distinct().Count());
        Assert.Contains("capture.status=\"completed\"", snapshot);
    }

    [Fact]
    public async Task SharedParent_IsInspectedOnce()
    {
        var fields = new List<string>();
        var helper = new DirectoryMoveFailureDiagnostics(probe =>
        {
            fields.Add(probe.Field);
            return Task.FromResult("observed");
        });
        string? snapshot = null;
        var primary = new IOException("move failed");
        var observed = await Record.ExceptionAsync(() => helper.MoveAsync(Source,
            Path.Combine(Path.GetDirectoryName(Source)!, "sibling"), text => snapshot = text,
            (_, _) => ThrowMove(primary)));
        Assert.Same(primary, observed);
        Assert.Contains("source.parent.metadata", fields);
        Assert.DoesNotContain("destination.parent.metadata", fields);
        Assert.Contains("destination.parent=\"same-as-source.parent\"", snapshot);
    }

    [Fact]
    public async Task Deadline_IsSharedAndDiscardsLateResults()
    {
        var clock = new ManualStewardTimeProvider();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fields = new List<string>();
        var publications = new List<string>();
        var helper = new DirectoryMoveFailureDiagnostics(probe =>
        {
            fields.Add(probe.Field);
            if (probe.Field == "source.owner")
            {
                started.SetResult();
                return pending.Task;
            }
            // Advance two probes by 750 ms each, so a fresh per-probe budget would be wrong.
            if (probe.Kind is DirectoryMoveFailureDiagnostics.ProbeKind.ProcessName or
                DirectoryMoveFailureDiagnostics.ProbeKind.Identity)
                clock.Advance(TimeSpan.FromMilliseconds(750));
            return Task.FromResult("observed-before-deadline");
        }, clock);
        var primary = new IOException("move failed");
        var operation = Record.ExceptionAsync(() => helper.MoveAsync(Source, Destination,
            publications.Add, (_, _) => ThrowMove(primary))).AsTask();
        await WaitFor(started.Task, "source.owner probe started");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await WaitFor(operation, "aggregate deadline released move failure");
        Assert.Same(primary, await operation);
        var snapshot = Assert.Single(publications);
        Assert.Contains("source.metadata=\"observed-before-deadline\"", snapshot);
        Assert.Contains("source.owner=\"timed-out(unfinished; aggregate-deadline)\"", snapshot);
        Assert.Contains("destination.metadata=\"timed-out(not-started; aggregate-deadline)\"", snapshot);
        Assert.Contains("capture.status=\"timed-out(partial)\"", snapshot);
        Assert.DoesNotContain("destination.metadata", fields);
        pending.SetResult("late-result-must-not-be-published");
        Assert.Equal(snapshot, Assert.Single(publications));
        Assert.DoesNotContain("late-result", snapshot);
    }

    [Fact]
    public async Task Deadline_WhenContentIsFull_PreservesTimeoutStatuses()
    {
        var huge = string.Concat(Enumerable.Repeat("😀漢\n", 20000));
        var primary = new IOException(huge);
        // Measure the prefix through the public receipt so path lengths and PID width cannot
        // affect where this test fills the 27 KiB content allowance (plus 4 KiB status reserve).
        var (_, baseline) = await Fail(new DirectoryMoveFailureDiagnostics(_ => Task.FromResult(huge),
            new ManualStewardTimeProvider()), primary);
        var prefixEnd = baseline.IndexOf("destination.metadata=", StringComparison.Ordinal);
        Assert.True(prefixEnd >= 0);
        Assert.Contains("truncated(snapshot-cap)", baseline[..prefixEnd]);
        const int contentLimit = 27 * 1024;
        var fillerLength = contentLimit - Encoding.UTF8.GetByteCount(baseline[..prefixEnd]) -
            Encoding.UTF8.GetByteCount("destination.metadata=\"\"\n");
        Assert.InRange(fillerLength, 1, 4000);

        var clock = new ManualStewardTimeProvider();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fields = new List<string>();
        var helper = new DirectoryMoveFailureDiagnostics(probe =>
        {
            fields.Add(probe.Field);
            if (probe.Field == "destination.owner")
            {
                started.SetResult();
                return pending.Task;
            }
            return Task.FromResult(probe.Field == "destination.metadata"
                ? new string('x', fillerLength) : huge);
        }, clock);
        var operation = Fail(helper, primary);
        await WaitFor(started.Task, "destination.owner probe started after filling content");
        clock.Advance(DirectoryMoveFailureDiagnostics.CaptureBudget);
        await WaitFor(operation, "full snapshot released at aggregate deadline");
        var (observed, snapshot) = await operation;
        Assert.Same(primary, observed);
        var statusStart = snapshot.IndexOf("destination.owner=", StringComparison.Ordinal);
        Assert.True(statusStart >= 0);
        Assert.Equal(contentLimit, Encoding.UTF8.GetByteCount(snapshot[..statusStart]));
        Assert.Contains("destination.owner=\"timed-out(unfinished; aggregate-deadline)\"", snapshot);
        Assert.Contains("destination.acl=\"timed-out(not-started; aggregate-deadline)\"", snapshot);
        Assert.Contains("destination.parent.handles=\"timed-out(not-started; aggregate-deadline)\"", snapshot);
        Assert.Contains("capture.status=\"timed-out(partial)\"", snapshot);
        Assert.Contains("output.truncated=true", snapshot);
        Assert.True(Encoding.UTF8.GetByteCount(snapshot) <= DirectoryMoveFailureDiagnostics.MaximumOutputBytes);
        Assert.DoesNotContain("destination.acl", fields);
        pending.SetResult("late-result-must-not-be-published");
    }

    [Fact]
    public async Task Output_IsCappedInUtf8AndKeepsOmissionStatuses()
    {
        var huge = string.Concat(Enumerable.Repeat("😀漢\n", 20000));
        var helper = new DirectoryMoveFailureDiagnostics(_ => Task.FromResult(huge));
        var (_, snapshot) = await Fail(helper, new IOException(huge));
        Assert.True(Encoding.UTF8.GetByteCount(snapshot) <= DirectoryMoveFailureDiagnostics.MaximumOutputBytes);
        Assert.Contains("[truncated(field-cap)]", snapshot);
        Assert.Contains("truncated(snapshot-cap)", snapshot);
        Assert.Contains("output.truncated=true", snapshot);
        Assert.Contains("destination.parent.handles=", snapshot);
        Assert.Contains("capture.status=\"completed\"", snapshot);
        Assert.DoesNotContain("\uFFFD", snapshot);
    }

    [Fact]
    public async Task PublicationFailure_ReportsSafeFallbackAndPreservesPrimary()
    {
        var fallback = new List<string>();
        string? snapshot = null;
        var helper = new DirectoryMoveFailureDiagnostics(_ => Task.FromResult("observed"), fallback: fallback.Add);
        var primary = new UnauthorizedAccessException("primary move denial");
        var observed = await Record.ExceptionAsync(() => helper.MoveAsync(Source, Destination, text =>
        {
            snapshot = text;
            throw new IOException(new string('x', 50000));
        }, (_, _) => ThrowMove(primary)));
        Assert.Same(primary, observed);
        var fallbackText = Assert.Single(fallback);
        Assert.Contains("publication=failed", fallbackText);
        Assert.True(Encoding.UTF8.GetByteCount(snapshot!) + Encoding.UTF8.GetByteCount(fallbackText) <=
            DirectoryMoveFailureDiagnostics.MaximumOutputBytes);
    }

    [Fact]
    public async Task BothWritersFail_StillRethrowsPrimary()
    {
        var calls = 0;
        var helper = new DirectoryMoveFailureDiagnostics(_ => Task.FromResult("observed"), fallback: _ =>
        {
            calls++;
            throw new InvalidOperationException("fallback broken");
        });
        var primary = new IOException("move failed");
        var observed = await Record.ExceptionAsync(() => helper.MoveAsync(Source, Destination,
            _ => throw new InvalidOperationException("output broken"), (_, _) => ThrowMove(primary)));
        Assert.Same(primary, observed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HandleObservations_DoNotAttributeDenial()
    {
        // Exercise the real read/format path with only the native handle boundary replaced.
        var paths = new List<string>();
        var helper = new DirectoryMoveFailureDiagnostics(timeProvider: new ManualStewardTimeProvider(),
            readHandles: path =>
            {
                paths.Add(path);
                return new FileHandleHolderObservation([new(123, "observed-holder")], null);
            });
        var operation = Fail(helper, new IOException("move failed"));
        await WaitFor(operation, "default diagnostic probes and snapshot publication");
        var (_, snapshot) = await operation;
        Assert.Equal(4, paths.Count);
        Assert.Contains("source.handles=\"observed-open-handles=123:observed-holder\"", snapshot);
        Assert.Contains("no denying process or cause is established", snapshot);
        Assert.Contains("source.metadata=\"missing(", snapshot);
        Assert.Contains("process.pid=\"" + Environment.ProcessId + "\"", snapshot);
        Assert.Contains("process.name=\"observed(", snapshot);
    }

    [Fact]
    public async Task NativeCode_UsesDocumentedEncodingOrExplicitUnavailability()
    {
        var helper = new DirectoryMoveFailureDiagnostics(_ => Task.FromResult("observed"));
        var (_, encoded) = await Fail(helper, new IOException("denial", unchecked((int)0x80070005)));
        Assert.Contains("primary.native=\"5 (HRESULT_FROM_WIN32 facility=7)\"", encoded);
        var (_, unknown) = await Fail(helper, new IOException("custom", unchecked((int)0x81234567)));
        Assert.Contains("primary.native=\"unavailable(no-exposed-native-code)\"", unknown);
    }

    [Fact]
    public async Task Success_DoesNotProbe()
    {
        var probes = 0;
        var publications = 0;
        var clock = new ManualStewardTimeProvider();
        var moves = new List<(string, string)>();
        var helper = new DirectoryMoveFailureDiagnostics(_ =>
        {
            probes++;
            throw new InvalidOperationException("success must not probe");
        }, clock, fallback: _ => publications++);
        await helper.MoveAsync(Source, Destination, _ => publications++, (source, destination) => moves.Add((source, destination)));
        Assert.Equal((Source, Destination), Assert.Single(moves));
        Assert.Equal(0, probes);
        Assert.Equal(0, publications);
        Assert.False(clock.TimerCreated.Task.IsCompleted);
    }

    private static async Task<(Exception? Error, string Snapshot)> Fail(DirectoryMoveFailureDiagnostics helper,
        Exception primary, Action? onMove = null)
    {
        string? snapshot = null;
        var observed = await Record.ExceptionAsync(() => helper.MoveAsync(Source, Destination, text => snapshot = text,
            (_, _) => { onMove?.Invoke(); ThrowMove(primary); }));
        Assert.NotNull(snapshot);
        return (observed, snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMove(Exception primary) => throw primary;

    private static async Task WaitFor(Task task, string eventName)
    {
        try { await task.WaitAsync(HangBound); }
        catch (TimeoutException error) { throw new TimeoutException("Did not observe " + eventName, error); }
    }
}
