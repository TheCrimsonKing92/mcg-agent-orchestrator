using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Test-local, read-only evidence collection. Every invocation owns its deadline and frozen output.
internal sealed class DirectoryMoveFailureDiagnostics(
    Func<DirectoryMoveFailureDiagnostics.Probe, Task<string>>? readProbe = null,
    TimeProvider? timeProvider = null,
    Action<string>? fallback = null,
    Func<string, FileHandleHolderObservation>? readHandles = null)
{
    internal static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(2);
    internal const int MaximumOutputBytes = 32 * 1024;
    private const int FallbackReserveBytes = 1024;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Action<string> fallbackOutput = fallback ?? Console.Error.WriteLine;

    internal enum ProbeKind { ProcessName, Identity, Metadata, Owner, Acl, Handles }
    internal sealed record Probe(string Field, ProbeKind Kind, string? Path = null);

    internal async Task MoveAsync(string source, string destination, Action<string> publish,
        Action<string, string>? move = null)
    {
        try
        {
            (move ?? Directory.Move)(source, destination);
        }
        catch (Exception primary)
        {
            try
            {
                await CaptureAndPublishAsync(source, destination, primary, publish).ConfigureAwait(false);
            }
            catch (Exception diagnostic)
            {
                SafeFallback("move-failure-snapshot capture=failed " + Error(diagnostic));
            }
            throw;
        }
    }

    private async Task CaptureAndPublishAsync(string source, string destination, Exception primary,
        Action<string> publish)
    {
        using var stopTimer = new CancellationTokenSource();
        // One timer for the entire capture, including all paths and optional probes.
        var deadline = Task.Delay(CaptureBudget, clock, stopTimer.Token);
        try
        {
            var report = new BoundedReport(MaximumOutputBytes - FallbackReserveBytes);
            report.Add("snapshot", "move-failure-snapshot");
            report.Add("operation", "Directory.Move");
            AddException(report, "primary", primary);
            if (primary.InnerException is { } inner)
                AddException(report, "primary.inner", inner);
            report.Add("process.pid", Environment.ProcessId.ToString());
            report.Add("capture.budget", "aggregate=2000ms; unfinished probes may continue; late results are discarded");
            report.Add("handles.interpretation", "Open handles are observations only; no denying process or cause is established.");

            var probes = new List<Probe>
            {
                new("process.name", ProbeKind.ProcessName),
                new("process.identity", ProbeKind.Identity)
            };
            var fullSource = AddPath(report, "source", source);
            var fullDestination = AddPath(report, "destination", destination);
            var paths = new List<(string Label, string Path)>();
            if (fullSource is not null) paths.Add(("source", fullSource));
            if (fullDestination is not null) paths.Add(("destination", fullDestination));
            var parents = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var (label, path) in paths.ToArray())
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is not null && parents.Add(parent))
                {
                    AddPath(report, label + ".parent", parent);
                    paths.Add((label + ".parent", parent));
                }
                else
                    report.Add(label + ".parent", parent is null ? "unavailable(no-parent)" : "same-as-source.parent");
            }
            foreach (var (label, path) in paths)
            {
                probes.Add(new(label + ".metadata", ProbeKind.Metadata, path));
                probes.Add(new(label + ".owner", ProbeKind.Owner, path));
                probes.Add(new(label + ".acl", ProbeKind.Acl, path));
                probes.Add(new(label + ".handles", ProbeKind.Handles, path));
            }

            var expired = false;
            foreach (var probe in probes)
            {
                if (deadline.IsCompleted)
                {
                    expired = true;
                    report.Add(probe.Field, "timed-out(not-started; aggregate-deadline)");
                    continue;
                }
                try
                {
                    // Native filesystem APIs need not honor cancellation. Never run them inline.
                    var pending = readProbe is null ? Task.Run(() => Read(probe)) : readProbe(probe);
                    if (await Task.WhenAny(pending, deadline).ConfigureAwait(false) != pending || deadline.IsCompleted)
                    {
                        expired = true;
                        report.Add(probe.Field, "timed-out(unfinished; aggregate-deadline)");
                        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    else
                        report.Add(probe.Field, await pending.ConfigureAwait(false));
                }
                catch (Exception diagnostic)
                {
                    report.Add(probe.Field, Error(diagnostic));
                }
            }
            report.Add("capture.status", expired ? "timed-out(partial)" : "completed");
            var snapshot = report.Finish();
            try { publish(snapshot); }
            catch (Exception writerFailure)
            {
                // Reserve a separate bounded fallback even if the primary writer partially wrote.
                SafeFallback("move-failure-snapshot publication=failed " + Error(writerFailure));
            }
        }
        finally { stopTimer.Cancel(); }
    }

    private string Read(Probe probe)
    {
        switch (probe.Kind)
        {
            case ProbeKind.ProcessName:
                using (var process = Process.GetCurrentProcess())
                    return "observed(" + process.ProcessName + ")";
            case ProbeKind.Identity:
                if (!OperatingSystem.IsWindows()) return "unsupported(effective-security-identity on this platform)";
                using (var identity = WindowsIdentity.GetCurrent())
                    return $"observed(name={identity.Name}; sid={identity.User?.Value ?? "unavailable"})";
            case ProbeKind.Metadata:
                // Exists() would conflate missing and denied inspection.
                return "exists; attributes=" + File.GetAttributes(probe.Path!);
            case ProbeKind.Owner:
                if (!OperatingSystem.IsWindows()) return "unsupported(Windows owner API)";
                var owner = new DirectoryInfo(probe.Path!).GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier));
                return "observed(owner-sid=" + (owner?.Value ?? "unavailable") + ")";
            case ProbeKind.Acl:
                if (!OperatingSystem.IsWindows()) return "unsupported(Windows ACL API)";
                return "observed(access-sddl=" + new DirectoryInfo(probe.Path!)
                    .GetAccessControl(AccessControlSections.Access)
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access) + ")";
            case ProbeKind.Handles:
                var observation = (readHandles ?? FileHandleHolders.Read)(probe.Path!);
                return observation.Failure is { } failure
                    ? "unavailable(" + failure + ")"
                    : "observed-open-handles=" + observation.Format();
            default:
                throw new InvalidOperationException("Unknown move diagnostic probe kind.");
        }
    }

    private static string? AddPath(BoundedReport report, string label, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            report.Add(label + ".full-path", full);
            report.Add(label + ".length", full.Length.ToString());
            return full;
        }
        catch (Exception error)
        {
            report.Add(label + ".full-path", Error(error));
            report.Add(label + ".length", "unavailable(full-path-resolution-failed)");
            return null;
        }
    }

    private static void AddException(BoundedReport report, string field, Exception error)
    {
        report.Add(field + ".type", error.GetType().FullName ?? error.GetType().Name);
        report.Add(field + ".message", error.Message);
        report.Add(field + ".hresult", $"0x{unchecked((uint)error.HResult):X8}");
        report.Add(field + ".native", NativeCode(error));
    }

    private static string NativeCode(Exception error) => error is Win32Exception native
        ? $"{native.NativeErrorCode} (Win32Exception.NativeErrorCode)"
        : (unchecked((uint)error.HResult) & 0xFFFF0000u) == 0x80070000u
            ? $"{error.HResult & 0xFFFF} (HRESULT_FROM_WIN32 facility=7)"
            : "unavailable(no-exposed-native-code)";

    private static string Error(Exception error) =>
        (error is FileNotFoundException or DirectoryNotFoundException ? "missing" :
            error is UnauthorizedAccessException ? "denied" :
            error is PlatformNotSupportedException ? "unsupported" : "failed") +
        $"({error.GetType().Name}; hresult=0x{unchecked((uint)error.HResult):X8}; native={NativeCode(error)}; " +
        BoundedReport.Quote(error.Message, 512) + ")";

    private void SafeFallback(string message)
    {
        try { fallbackOutput(BoundedReport.Quote(message, FallbackReserveBytes - 1)); }
        catch { /* Diagnostic publication cannot replace the move exception. */ }
    }

    private sealed class BoundedReport(int maximumBytes)
    {
        private readonly StringBuilder text = new();
        private int bytes;
        private bool truncated;

        internal void Add(string field, string value)
        {
            var quoted = Quote(value, 4096);
            truncated |= quoted.Contains("[truncated(field-cap)]", StringComparison.Ordinal);
            var line = field + "=" + quoted + "\n";
            // Fixed field count and short internal names need <4 KiB for all omission statuses.
            if (bytes + Encoding.UTF8.GetByteCount(line) > maximumBytes - 4096)
            {
                truncated = true;
                line = field + "=truncated(snapshot-cap)\n";
            }
            text.Append(line);
            bytes += Encoding.UTF8.GetByteCount(line);
        }

        internal string Finish() => text + "output.truncated=" + (truncated ? "true" : "false") + "\n";

        internal static string Quote(string value, int maximumBytes)
        {
            const string marker = "[truncated(field-cap)]";
            var result = new StringBuilder("\"");
            var used = 2;
            foreach (var rune in value.EnumerateRunes())
            {
                var piece = rune.Value switch
                {
                    '\\' => "\\\\", '"' => "\\\"", '\r' => "\\r", '\n' => "\\n", '\t' => "\\t",
                    < 32 => $"\\u{rune.Value:X4}", _ => rune.ToString()
                };
                var size = Encoding.UTF8.GetByteCount(piece);
                if (used + size > maximumBytes - marker.Length)
                {
                    result.Append(marker);
                    break;
                }
                result.Append(piece);
                used += size;
            }
            return result.Append('"').ToString();
        }
    }
}
