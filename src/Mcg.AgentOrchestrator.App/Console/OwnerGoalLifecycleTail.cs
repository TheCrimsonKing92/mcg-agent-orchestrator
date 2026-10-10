using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-owned, called serially by the host. Each cursor retains a torn UTF-8 line across refreshes.
internal sealed class OwnerGoalLifecycleTail(string directory)
{
    private const int InitialWindow = 64 * 1024;
    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);

    internal void Reset() => _cursors.Clear();

    internal IReadOnlyList<OwnerConductEvent> ReadNew(IReadOnlyCollection<string> goalIds, CancellationToken token = default,
        bool evictDeparted = true)
    {
        // Drain known goals once more before eviction: completion may remove a board
        // row in the same refresh that writes its final role-finish event. Scoped reads
        // leave every other cursor untouched until the next full refresh.
        var departing = evictDeparted ? _cursors.Keys.Except(goalIds).ToArray() : [];
        var result = new List<OwnerConductEvent>();
        foreach (var id in goalIds.Concat(departing).Distinct())
        {
            token.ThrowIfCancellationRequested();
            // Board metadata supplies ids, but never allow an id to escape the events directory.
            if (id.IndexOfAny(['/', '\\']) >= 0 || id is "." or "..") continue;
            var path = Path.Combine(directory, id + ".jsonl");
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var creation = File.GetCreationTimeUtc(path);
                if (!_cursors.TryGetValue(id, out var cursor) || cursor.Creation != creation || stream.Length < cursor.Position)
                {
                    cursor = new() { Creation = creation, Position = Math.Max(0, stream.Length - InitialWindow) };
                    cursor.DropFirst = cursor.Position > 0;
                    _cursors[id] = cursor;
                }
                stream.Position = cursor.Position;
                var remaining = stream.Length - stream.Position;
                var buffer = new byte[8192];
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var read = stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (read == 0) break;
                    remaining -= read;
                    for (var index = 0; index < read; index++)
                    {
                        if (buffer[index] != (byte)'\n') { cursor.Partial.WriteByte(buffer[index]); continue; }
                        if (!cursor.DropFirst && OwnerGoalLifecycleEvent.TryParse(
                            Encoding.UTF8.GetString(cursor.Partial.ToArray()).TrimEnd('\r'), id, out var item) &&
                            OwnerConsoleStartupActivity.IsOperatorEvent(item!)) result.Add(item!);
                        cursor.DropFirst = false;
                        cursor.Partial.SetLength(0);
                        cursor.Partial.Position = 0;
                    }
                }
                cursor.Position = stream.Position;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _cursors.Remove(id); }
        }
        foreach (var id in departing) _cursors.Remove(id);
        return result;
    }

    private sealed class Cursor
    {
        internal long Position;
        internal DateTime Creation;
        internal bool DropFirst;
        internal readonly MemoryStream Partial = new();
    }
}
