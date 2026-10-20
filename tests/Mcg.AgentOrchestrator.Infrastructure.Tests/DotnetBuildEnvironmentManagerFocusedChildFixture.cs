using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

// One fixture root and its contained launch. Published identities are evidence, never kill authority.
internal sealed class DotnetBuildEnvironmentManagerFocusedChildFixture : IAsyncDisposable
{
    internal const string IdentityFailure = "The focused child fixture did not publish a readable descendant identity.";
    private OwnedProcessGroup.SuspendedRedirectedProcessStart? launch;
    private SafeFileHandle? jobObservation;
    private object? heldIdentity;
    private bool disposed;
    private bool cleanupAttempted;
    private Stopwatch? processCleanupBudget;

    internal DotnetBuildEnvironmentManagerFocusedChildFixture(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal string IdentityPath => Path.Combine(Root, "descendant.json");
    internal string ReleasePath => Path.Combine(Root, "never-release");
    internal string Phase { get; set; } = "setup";
    internal Process Harness => launch!.Process;
    internal TailCapture? Stdout { get; private set; }
    internal TailCapture? Stderr { get; private set; }
    internal bool CustodyResolved { get; private set; }
    internal bool ForcedTermination { get; private set; }

    internal void Start(ProcessStartInfo startInfo)
    {
        Phase = "launch";
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        launch = OwnedProcessGroup.StartSuspendedContainedRedirected(startInfo);
        // Process is attached by PID rather than started by this managed object. Retain its
        // handle while the owned launch is still suspended so ExitCode remains readable after
        // exit (also for Diagnostics), without reopening a potentially reused PID.
        _ = Harness.SafeHandle;
        var identity = launch.ReadLifecycleIdentity(Harness.Id);
        heldIdentity = new
        {
            processId = Harness.Id, startedAt = identity.StartedAt, executable = identity.ExecutablePath,
            observation = identity.Status.ToString(), identity.NativeError, identity.Operation
        };
        if (!launch.Group.TryDuplicateAccountingHandle(out jobObservation))
            throw new InvalidOperationException("Cannot retain fixture job exit observation.");
        // The launch retains the native process handle and the non-breakaway job before resume.
        Stdout = new TailCapture(launch.StandardOutput);
        Stderr = new TailCapture(launch.StandardError);
        launch.Resume();
    }

    internal static string ObserveIdentity(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 8192) return "malformed";
            using var document = JsonDocument.Parse(stream);
            var value = document.RootElement;
            return value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out var id) && id > 0 &&
                value.TryGetProperty("parentProcessId", out var parent) && parent.TryGetInt32(out var parentId) && parentId > 0 &&
                value.TryGetProperty("startedAt", out var started) && started.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(started.GetString(), out _) &&
                value.TryGetProperty("executable", out var image) && image.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(image.GetString()) &&
                value.TryGetProperty("identityStatus", out var status) && status.ValueKind == JsonValueKind.String &&
                status.GetString() == "confirmed" ? "valid" : "malformed";
        }
        catch (FileNotFoundException) { return "absent"; }
        catch (DirectoryNotFoundException) { return "absent"; }
        catch (JsonException) { return "malformed"; }
        catch (InvalidOperationException) { return "malformed"; }
        catch (IOException) { return "unreadable"; }
        catch (UnauthorizedAccessException) { return "unreadable"; }
    }

    internal JsonDocument RequireIdentity(bool waitForPublication = true)
    {
        Phase = "identity-readiness";
        var available = waitForPublication
            ? SpinWait.SpinUntil(() => ObserveIdentity(IdentityPath) == "valid", TimeSpan.FromSeconds(15))
            : ObserveIdentity(IdentityPath) == "valid";
        Xunit.Assert.True(available, IdentityFailure);
        return JsonDocument.Parse(File.ReadAllText(IdentityPath));
    }

    internal string Diagnostics()
    {
        string state;
        try { state = launch is null ? "not-started" : Harness.HasExited ? $"exited:{Harness.ExitCode}" : "alive"; }
        catch (Exception error) { state = "observation-failed:" + error.GetType().Name; }
        return JsonSerializer.Serialize(new
        {
            phase = Phase, root = Root, harnessIdentity = heldIdentity, harnessState = state,
            identityPath = IdentityPath, identityState = ObserveIdentity(IdentityPath),
            stdout = Stdout?.Snapshot() ?? new StreamSnapshot("not-started", "empty", "", null),
            stderr = Stderr?.Snapshot() ?? new StreamSnapshot("not-started", "empty", "", null)
        });
    }

    // Capture before cleanup changes the observable failure state. Cleanup cannot replace the primary assertion.
    internal async Task RunAsync(Func<Task> body, Func<bool, bool>? observeCleanup = null)
    {
        Exception? primary = null;
        string? diagnostics = null;
        try { await body(); }
        catch (Exception error) { primary = error; diagnostics = Diagnostics(); }
        var cleanup = await CleanupAsync(observeCleanup);
        if (primary is not null)
            throw new Xunit.Sdk.XunitException(primary.Message + Environment.NewLine + diagnostics +
                Environment.NewLine + "cleanup=" + (cleanup ?? "resolved"));
        Xunit.Assert.True(cleanup is null, "cleanup=" + cleanup + Environment.NewLine + Diagnostics());
    }

    internal async Task DrainAsync()
    {
        await Task.WhenAll(Stdout?.Completion ?? Task.CompletedTask, Stderr?.Completion ?? Task.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal async Task<string?> CleanupAsync(Func<bool, bool>? observeCleanup = null)
    {
        cleanupAttempted = true;
        var errors = new List<string>();
        // Retrying an observation or deletion must not grant another process-cleanup budget.
        var budget = processCleanupBudget ??= Stopwatch.StartNew();
        TimeSpan Remaining() => TimeSpan.FromMilliseconds(Math.Max(0, 10000 - budget.Elapsed.TotalMilliseconds));
        try { File.WriteAllText(ReleasePath, "release"); }
        catch (Exception error) { errors.Add("release-failed:" + error.GetType().Name); }

        var exited = launch is null;
        if (launch is not null)
        {
            try
            {
                if (jobObservation is null || jobObservation.IsInvalid)
                    errors.Add("job-observation-unavailable");
                else
                {
                    exited = OwnedProcessGroup.WaitForJobExit(jobObservation,
                        TimeSpan.FromMilliseconds(Math.Min(2000, Remaining().TotalMilliseconds)));
                    if (!exited)
                    {
                        ForcedTermination = true;
                        launch.Group.Kill();
                        exited = OwnedProcessGroup.WaitForJobExit(jobObservation, Remaining());
                    }
                }
            }
            catch (Exception error) { errors.Add("process-cleanup-failed:" + error.GetType().Name); }
        }
        CustodyResolved = observeCleanup?.Invoke(exited) ?? exited;
        if (!CustodyResolved) errors.Add("owned-tree-exit-unconfirmed");

        try { await DrainAsync(); }
        catch (Exception error) { errors.Add("stream-drain-failed:" + error.GetType().Name); }
        if (Stdout?.Snapshot().Error is { } stdoutError) errors.Add("stdout-capture-failed:" + stdoutError);
        if (Stderr?.Snapshot().Error is { } stderrError) errors.Add("stderr-capture-failed:" + stderrError);
        // Keep release intact whenever observation is unresolved, including the injected negative control.
        if (CustodyResolved && errors.Count == 0)
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception error) { errors.Add("root-delete-failed:" + error.GetType().Name); }
        }
        return errors.Count == 0 ? null : string.Join(",", errors);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        string? cleanup;
        // RunAsync already reports cleanup alongside its primary failure. Do not mask it during disposal.
        try { cleanup = !cleanupAttempted && Directory.Exists(Root) ? await CleanupAsync() : null; }
        finally
        {
            launch?.Dispose();
            jobObservation?.Dispose();
        }
        Xunit.Assert.True(cleanup is null, "outer-cleanup=" + cleanup);
    }

    internal sealed record StreamSnapshot(string State, string Content, string Tail, string? Error);

    internal sealed class TailCapture
    {
        private readonly object sync = new();
        private string tail = "";
        private string state = "open";
        private string? error;

        internal TailCapture(TextReader reader) => Completion = CaptureAsync(reader);
        internal Task Completion { get; }
        internal StreamSnapshot Snapshot()
        {
            lock (sync) return new(state, tail.Length == 0 ? "empty" : "nonempty", tail, error);
        }

        private async Task CaptureAsync(TextReader reader)
        {
            try
            {
                var buffer = new char[512];
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                {
                    lock (sync)
                    {
                        tail += new string(buffer, 0, count);
                        if (tail.Length > 4096) tail = tail[^4096..];
                    }
                }
                lock (sync) state = "completed";
            }
            catch (Exception failure)
            {
                lock (sync) { state = "capture-failed"; error = failure.GetType().Name; }
            }
        }
    }
}
