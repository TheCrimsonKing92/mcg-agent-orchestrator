using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// History is passing, non-reused lane executions, oldest first, independent of scheduling history.
internal static class GateLaneBaselineStore
{
    internal const string FileName = "gate-lane-test-baselines.json";
    internal const int MaximumRuns = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal sealed record Run(DateTimeOffset RecordedAt, double WallMs, IReadOnlyDictionary<string, double> Tests);
    private sealed record Store(int Version, Dictionary<string, List<Run>> Lanes);

    internal static string ResolveStorePath(string worktreePath) =>
        Path.Combine(HostLedgerStateDirectory.Resolve(worktreePath), FileName);

    internal static IReadOnlyList<Run> ReadHistory(string worktreePath, string lane)
    {
        try { return Read(ResolveStorePath(worktreePath)).Lanes.GetValueOrDefault(lane)?.TakeLast(MaximumRuns).ToArray() ?? []; }
        catch (Exception) { return []; }
    }

    internal static string? TryAppend(string worktreePath, string lane, Run run, bool passed)
    {
        if (!passed) return null;
        string? temporary = null;
        var acquired = false;
        Mutex? mutex = null;
        try
        {
            if (!double.IsFinite(run.WallMs) || run.WallMs <= 0) return "baseline-invalid-wall";
            var path = Path.GetFullPath(ResolveStorePath(worktreePath));
            var lockIdentity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
            mutex = new Mutex(false, "mcg-lane-baseline-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockIdentity))));
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return "baseline-lock-timeout";
            var store = Read(path);
            var history = store.Lanes.GetValueOrDefault(lane) ?? [];
            history.Add(run with { Tests = LaneTestDurationSnapshot.Bound(run.Tests) });
            store.Lanes[lane] = history.TakeLast(MaximumRuns).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception error) { return "baseline-write:" + error.GetType().Name; }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { }
            try { if (acquired) mutex!.ReleaseMutex(); } catch (Exception) { }
            mutex?.Dispose();
        }
    }

    private static Store Read(string path)
    {
        try
        {
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path), JsonOptions);
            if (store is null || store.Version != 1 || store.Lanes is null) return new(1, new(StringComparer.Ordinal));
            if (store.Lanes.Values.Any(history => history is null || history.Any(run => run is null || run.Tests is null)))
                return new(1, new(StringComparer.Ordinal));
            foreach (var lane in store.Lanes.Keys.ToArray())
                store.Lanes[lane] = store.Lanes[lane].Where(run => double.IsFinite(run.WallMs) && run.WallMs > 0)
                    .TakeLast(MaximumRuns).Select(run => run with { Tests = LaneTestDurationSnapshot.Bound(run.Tests) }).ToList();
            return store;
        }
        // Only absent or corrupt data starts fresh. A transient read failure must
        // reach TryAppend's best-effort boundary before it can replace any history.
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException)
        { return new(1, new(StringComparer.Ordinal)); }
    }
}
