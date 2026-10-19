using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Progress belongs to one pass under the sweep lease; it expires from that pass's original start.
internal sealed class StorageRetentionResumeCursor
{
    internal const string FileName = "storage-retention-cursor.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly CursorState _state;
    private bool _persistenceDisabled;

    private StorageRetentionResumeCursor(string path, CursorState state)
    {
        _path = path;
        _state = state;
    }

    internal static string PathFor(string orchestratorDirectory) => Path.Combine(orchestratorDirectory, FileName);

    internal static StorageRetentionResumeCursor Open(string orchestratorDirectory, DateTimeOffset now, TimeSpan window)
    {
        var path = PathFor(orchestratorDirectory);
        try
        {
            var state = JsonSerializer.Deserialize<CursorState>(File.ReadAllText(path), JsonOptions);
            if (state is not null && state.PolicyVersion == EvidenceRetentionPolicy.Version &&
                state.StartedAtUtc <= now && now - state.StartedAtUtc <= window &&
                state.Families is not null && state.Families.Values.All(family => family is not null))
                return new(path, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable progress costs a full pass, never a failed sweep.
        }
        return new(path, new(now, EvidenceRetentionPolicy.Version, new(StringComparer.Ordinal)));
    }

    internal string? LastCompleted(string family) =>
        _state.Families.TryGetValue(family, out var progress) ? progress.LastCompleted : null;

    internal bool ShouldSkip(string family, string name) =>
        _state.Families.TryGetValue(family, out var progress) &&
        (progress.Complete || (progress.LastCompleted is not null &&
            string.CompareOrdinal(name, progress.LastCompleted) <= 0));

    internal void Advance(string family, string completedName)
    {
        _state.Families[family] = new(completedName, false);
        Save();
    }

    internal void MarkFamilyComplete(string family)
    {
        _state.Families[family] = new(LastCompleted(family), true);
        Save();
    }

    internal void Complete()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _persistenceDisabled = true;
        }
    }

    private void Save()
    {
        if (_persistenceDisabled) return;
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _persistenceDisabled = true;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _persistenceDisabled = true;
            }
        }
    }

    private sealed record CursorState(DateTimeOffset StartedAtUtc, int PolicyVersion,
        Dictionary<string, FamilyState> Families);
    private sealed record FamilyState(string? LastCompleted, bool Complete);
}
