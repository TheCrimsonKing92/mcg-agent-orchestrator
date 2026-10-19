using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Advisory priority state belongs beside occupancy, but never uses its .json suffix.
// The short file lock serializes create/take/delete across conductor processes.
internal sealed class RemoteExecutorSlotArbiter(string root, string attempt,
    RemoteExecutorClaimClass claimClass, TimeProvider clock) : IDisposable
{
    internal sealed record Demand(int OwnerPid, DateTimeOffset OwnerStartUtc,
        string Class, DateTimeOffset WrittenAt);
    internal sealed record Preemption(string RequestingAttempt, string Lane, DateTimeOffset WrittenAt);
    internal sealed record Target(string Executor, string ShadowAttempt, Preemption Request, bool Owned);
    private readonly HashSet<string> _demands = new(StringComparer.Ordinal);
    private readonly HashSet<(string Executor, string Shadow)> _hung = [];
    private readonly List<Target> _requests = [];
    private readonly object _gate = new();

    // Keep aligned with RemoteExecutorOccupancy.Folder; that method's source is pinned.
    internal static string Folder(string root, string executor) =>
        Path.Combine(root, ".orchestrator", "remote-executor-occupancy", executor);
    private static bool Safe(string value) => !string.IsNullOrWhiteSpace(value) &&
        value is not ("." or "..") && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private static string ShadowAttempt(string marker) => Regex.Replace(marker, @"\.slot-\d+$", "");
    private static string RequestPath(string root, string executor, string shadow, bool taken = false) =>
        Path.Combine(Folder(root, executor), shadow + (taken ? ".preempt-taken" : ".preempt"));

    internal void WriteDemand(IEnumerable<string> executors)
    {
        lock (_gate)
        foreach (var executor in executors)
        {
            if (claimClass is not (RemoteExecutorClaimClass.Gate or RemoteExecutorClaimClass.FocusedEvidence) ||
                !Safe(executor) || !Safe(attempt) ||
                RemoteExecutorOccupancy.DefaultStartTime(Environment.ProcessId) is not { } start) continue;
            var path = Path.Combine(Folder(root, executor), $"{attempt}.{claimClass}.demand");
            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Folder(root, executor));
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Demand(Environment.ProcessId,
                    start, claimClass.ToString(), clock.GetUtcNow())));
                File.Move(temporary, path, overwrite: true);
                _demands.Add(executor);
            }
            catch (Exception ex) when (FileFault(ex)) { }
            finally { Delete(temporary); }
        }
    }

    internal static IReadOnlyList<string> LiveDemandClasses(string root, string executor,
        DateTimeOffset now, Func<int, DateTimeOffset?>? startTimeOf = null)
    {
        var result = new List<string>();
        try
        {
            if (!Safe(executor) || !Directory.Exists(Folder(root, executor))) return result;
            foreach (var path in Directory.EnumerateFiles(Folder(root, executor), "*.demand"))
            {
                try
                {
                    var value = JsonSerializer.Deserialize<Demand>(File.ReadAllText(path));
                    if (value is not null && now - value.WrittenAt <= TimeSpan.FromSeconds(120) &&
                        (startTimeOf ?? RemoteExecutorOccupancy.DefaultStartTime)(value.OwnerPid) == value.OwnerStartUtc)
                        result.Add(value.Class);
                }
                catch (Exception ex) when (FileFault(ex)) { }
            }
        }
        catch (Exception ex) when (FileFault(ex)) { }
        return result;
    }

    internal string? YieldReason(string executor, RemoteFocusedEvidenceSettings settings)
    {
        if (claimClass is not (RemoteExecutorClaimClass.Shadow or RemoteExecutorClaimClass.FocusedEvidence)) return null;
        var demands = LiveDemandClasses(root, executor, clock.GetUtcNow());
        if (demands.Contains("Gate") || HasPreemption(executor)) return "yield-to-gate";
        if (claimClass != RemoteExecutorClaimClass.Shadow) return null;
        if (demands.Contains("FocusedEvidence")) return "yield-to-focused-evidence";
        return settings.ShadowPreemption == "idle-only" && RemoteExecutorOccupancy.LiveMarkers(root, executor)
            .Any(marker => marker.Class != "Shadow") ? "idle-only" : null;
    }

    private static T? WithLock<T>(string root, string executor, Func<T?> action) where T : class
    {
        try
        {
            if (!Safe(executor)) return null;
            Directory.CreateDirectory(Folder(root, executor));
            using var lease = new FileStream(Path.Combine(Folder(root, executor), "arbiter.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return action();
        }
        catch (Exception ex) when (FileFault(ex)) { return null; }
    }

    // Caller holds the executor file lock. Both names count as outstanding Gate demand.
    private List<(string Shadow, Preemption Request)> ReadRequests(string executor)
    {
        var result = new List<(string, Preemption)>();
        var shadows = RemoteExecutorOccupancy.LiveMarkers(root, executor)
            .Where(marker => marker.Class == "Shadow").Select(marker => ShadowAttempt(marker.Attempt)).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(Folder(root, executor), "*.preempt*"))
        {
            if (!path.EndsWith(".preempt", StringComparison.Ordinal) && !path.EndsWith(".preempt-taken", StringComparison.Ordinal)) continue;
            var shadow = Path.GetFileName(path)[..Path.GetFileName(path).LastIndexOf('.')];
            try
            {
                var request = JsonSerializer.Deserialize<Preemption>(File.ReadAllText(path));
                if (request is not null && shadows.Contains(shadow) && clock.GetUtcNow() - request.WrittenAt <= TimeSpan.FromSeconds(120))
                    result.Add((shadow, request));
                else Delete(path);
            }
            catch (Exception ex) when (FileFault(ex)) { Delete(path); }
        }
        return result;
    }

    private bool HasPreemption(string executor)
    {
        var requests = WithLock(root, executor, () => ReadRequests(executor));
        if (requests is not null) return requests.Count > 0;
        // Another process can be creating/taking a request under the short lock.
        // Preserve its reservation until a later read can validate and sweep it.
        try
        {
            return Safe(executor) && Directory.Exists(Folder(root, executor)) &&
                Directory.EnumerateFiles(Folder(root, executor), "*.preempt*").Any();
        }
        catch (Exception ex) when (FileFault(ex)) { return false; }
    }

    internal Target? RequestPreemption(IEnumerable<string> executors, string lane)
    {
        if (claimClass != RemoteExecutorClaimClass.Gate) throw new InvalidOperationException("Only Gate claims may preempt.");
        lock (_gate)
        {
            Target? existing = null;
            foreach (var executor in executors)
            {
                var target = WithLock(root, executor, () =>
                {
                    var outstanding = ReadRequests(executor);
                    var markers = RemoteExecutorOccupancy.LiveMarkers(root, executor)
                        .Where(marker => marker.Class == "Shadow")
                        .OrderBy(marker => File.GetLastWriteTimeUtc(Path.Combine(Folder(root, executor), marker.Attempt + ".json")));
                    foreach (var marker in markers)
                    {
                        var shadow = ShadowAttempt(marker.Attempt);
                        if (_hung.Contains((executor, shadow))) continue;
                        if (outstanding.FirstOrDefault(value => value.Shadow == shadow) is var found && found.Request is not null)
                        {
                            existing ??= new(executor, shadow, found.Request, false);
                            continue;
                        }
                        if (!Safe(shadow)) continue;
                        var request = new Preemption(attempt, lane, clock.GetUtcNow());
                        using (var stream = new FileStream(RequestPath(root, executor, shadow), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            JsonSerializer.Serialize(stream, request);
                        return new Target(executor, shadow, request, true);
                    }
                    return null;
                });
                if (target is null) continue;
                _requests.Add(target);
                return target;
            }
            return existing;
        }
    }

    internal bool TryTakePreemption(string executor)
    {
        if (!Safe(executor) || !Safe(attempt) || !File.Exists(RequestPath(root, executor, attempt))) return false;
        return WithLock(root, executor, () =>
        {
            if (!ReadRequests(executor).Any(value => value.Shadow == attempt)) return null;
            var source = RequestPath(root, executor, attempt);
            if (!File.Exists(source)) return null;
            File.Move(source, RequestPath(root, executor, attempt, taken: true));
            return "taken";
        }) is not null;
    }

    internal void CompletePreemption(string executor) => WithLock(root, executor, () =>
    { if (Safe(attempt)) Delete(RequestPath(root, executor, attempt, taken: true)); return "done"; });

    private void RemoveRequest(Target target)
    {
        if (!target.Owned) return;
        WithLock(root, target.Executor, () =>
        {
            var path = RequestPath(root, target.Executor, target.ShadowAttempt);
            // A taken request belongs to its shadow until the queue-release wait ends.
            if (File.Exists(path) && JsonSerializer.Deserialize<Preemption>(File.ReadAllText(path)) == target.Request) Delete(path);
            return "done";
        });
    }

    internal async Task<RemoteLaneExecutorEntry?> WaitForGateAsync(Target target, TimeSpan pollInterval,
        Func<string, RemoteLaneExecutorEntry?> claim, Action<bool, long> record, CancellationToken token)
    {
        var started = clock.GetUtcNow();
        var released = false;
        try
        {
            while (clock.GetUtcNow() - started < TimeSpan.FromSeconds(45))
            {
                token.ThrowIfCancellationRequested();
                if (claim(target.Executor) is { } entry) { released = true; return entry; }
                await Task.Delay(pollInterval, token).ConfigureAwait(false);
            }
            lock (_gate) _hung.Add((target.Executor, target.ShadowAttempt));
            return null;
        }
        finally
        {
            RemoveRequest(target);
            record(released, Math.Max(0, (long)(clock.GetUtcNow() - started).TotalMilliseconds));
        }
    }

    internal void Claimed(string executor)
    {
        lock (_gate)
        {
            if (_demands.Remove(executor)) Delete(Path.Combine(Folder(root, executor), $"{attempt}.{claimClass}.demand"));
            foreach (var request in _requests.Where(request => request.Executor == executor)) RemoveRequest(request);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var executor in _demands) Delete(Path.Combine(Folder(root, executor), $"{attempt}.{claimClass}.demand"));
            foreach (var request in _requests) RemoveRequest(request);
            _demands.Clear(); _requests.Clear();
        }
    }

    private static bool FileFault(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException;
    private static void Delete(string path) { try { File.Delete(path); } catch (Exception ex) when (FileFault(ex)) { } }
}
