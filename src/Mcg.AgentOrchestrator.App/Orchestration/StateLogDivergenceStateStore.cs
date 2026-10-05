using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Advisory memory only: this file never owns goal state or lifecycle events.
internal sealed class StateLogDivergenceState
{
    public Dictionary<string, StateLogDivergenceStateEntry> Goals { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset? LastStartedUtc { get; set; }
    public string? LastSkipSummary { get; set; }
}

internal sealed record StateLogDivergenceStateEntry(
    int Lost, int Repeated, int StoredOnly, long FirstCursor, string Kinds, bool Terminal)
{
    internal (int Lost, int Repeated, int StoredOnly, long FirstCursor, string Kinds) Signature =>
        (Lost, Repeated, StoredOnly, FirstCursor, Kinds);
}

internal static class StateLogDivergenceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static StateLogDivergenceState Load(string path)
    {
        try
        {
            return UnderLock(path, () =>
            {
                if (!File.Exists(path)) return new StateLogDivergenceState();
                var state = JsonSerializer.Deserialize<StateLogDivergenceState>(File.ReadAllText(path), JsonOptions);
                if (state?.Goals is null || state.Goals.Values.Any(entry => entry is null || entry.Kinds is null))
                    return new StateLogDivergenceState();
                return state;
            });
        }
        catch { return new StateLogDivergenceState(); }
    }

    internal static void Save(string path, StateLogDivergenceState state) => UnderLock(path, () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return true;
    });

    // Mutex ownership is thread-bound: each operation is synchronous, with no await under the lock.
    private static T UnderLock<T>(string path, Func<T> operation)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(path).ToUpperInvariant())));
        using var mutex = new Mutex(false, "McgStateLogDivergence-" + key);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try { return operation(); }
        finally { mutex.ReleaseMutex(); }
    }
}
