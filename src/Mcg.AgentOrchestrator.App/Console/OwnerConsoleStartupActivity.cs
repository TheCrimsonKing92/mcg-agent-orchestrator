using System.Globalization;
using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStartupActivity
{
    // Raw events include context that folds into one sentence (train members, retry, resolution).
    internal const int MaxRawEvents = OwnerConsoleViewModelBuilder.MaxActivityItems * 10;
    internal static bool IsOperatorEvent(OwnerConductEvent item) => OwnerActivityNarrator.Maps(item);
    internal static IReadOnlyList<OwnerConductEvent> ReadRecent(string path, CancellationToken token = default) =>
        ReadHistory(path, null, TimeProvider.System, token).Recent;

    internal static OwnerConsoleActivityLoad ReadHistory(string path, DateTimeOffset? horizon, TimeProvider clock,
        CancellationToken token = default)
    {
        var recent = new List<OwnerConductEvent>();
        var todayLandings = new HashSet<OwnerConductEvent>();
        var midnight = new DateTimeOffset(clock.GetLocalNow().Date, clock.GetLocalNow().Offset);
        DateTimeOffset? last = null;
        foreach (var file in HistoryFiles(path))
        {
            token.ThrowIfCancellationRequested();
            FileStream stream;
            try { stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            using (stream)
            {
                try
                {
                    foreach (var line in ReverseLines(stream, token))
                    {
                        if (!ConductEventFileSource.TryParse(line, out var item)) continue;
                        last = last is null || item!.Timestamp > last ? item!.Timestamp : last;
                        var enough = horizon is { } oldest ? item!.Timestamp < oldest : recent.Count >= MaxRawEvents;
                        // Count today's landings even when the displayed history is already full.
                        if (enough && item!.Timestamp < midnight) return Snapshot();
                        if (OwnerActivityNarrator.IsLanding(item!) &&
                            TimeZoneInfo.ConvertTime(item!.Timestamp, clock.LocalTimeZone).Date == clock.GetLocalNow().Date)
                            todayLandings.Add(item!);
                        if (!enough && IsOperatorEvent(item!) && recent.Count < MaxRawEvents && !recent.Contains(item!)) recent.Add(item!);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return Snapshot();
        OwnerConsoleActivityLoad Snapshot() => new(recent.OrderBy(value => value.Timestamp).ToArray(), last, todayLandings.Count);
    }

    private static IEnumerable<string> HistoryFiles(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        string[] siblings;
        try { siblings = Directory.GetFiles(directory, stem + "-*" + extension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { siblings = []; }
        yield return full;
        foreach (var sibling in siblings.Select(file => (File: file, Suffix: Path.GetFileNameWithoutExtension(file)[(stem.Length + 1)..]))
            .Select(value => (value.File, Parts: value.Suffix.Split('-')))
            .Where(value => DateTime.TryParseExact(value.Parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .OrderByDescending(value => value.Parts[0], StringComparer.Ordinal)
            .ThenByDescending(value => value.Parts.Length > 1 && int.TryParse(value.Parts[1], out var suffix) ? suffix : 0))
            yield return sibling.File;
    }

    private static IEnumerable<string> ReverseLines(FileStream stream, CancellationToken token)
    {
        // Snapshot length; a writer cannot extend the scan. Traverse bounded blocks backwards.
        var position = stream.Length;
        var buffer = new byte[8192];
        var line = new List<byte>();
        while (position > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, position);
            position -= count;
            stream.Position = position;
            stream.ReadExactly(buffer.AsSpan(0, count));
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    if (line.Count > 0) { line.Reverse(); yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r').TrimStart('\uFEFF'); line.Clear(); }
                }
                else line.Add(buffer[index]);
            }
        }
        if (line.Count > 0) { line.Reverse(); yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r').TrimStart('\uFEFF'); }
    }

    internal static void Append(List<OwnerConductEvent> recent, OwnerConductEvent item)
    {
        if (!IsOperatorEvent(item) || recent.Contains(item)) return;
        recent.Add(item);
        if (recent.Count > MaxRawEvents)
        {
            var oldest = recent.Min(value => value.Timestamp);
            recent.RemoveAt(recent.FindIndex(value => value.Timestamp == oldest));
        }
    }
}
