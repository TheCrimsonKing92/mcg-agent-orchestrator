using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductEventsLogTail
{
    private sealed class Cursor
    {
        internal long Offset;
        internal byte[] Prefix = [];
        internal readonly List<(DateTimeOffset At, string Detail)> Rows = [];
    }

    private readonly Dictionary<string, Cursor> _cursors = new(StringComparer.Ordinal);

    // Content-processing bytes only; identity-prefix validation is excluded.
    internal long LastReadBytes { get; private set; }

    internal IReadOnlyList<(DateTimeOffset At, string Detail)> Rows(IReadOnlyList<string> logPaths)
    {
        LastReadBytes = 0;
        var listed = logPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var path in _cursors.Keys.Where(path => !listed.Contains(path)).ToArray()) _cursors.Remove(path);
        var result = new List<(DateTimeOffset At, string Detail)>();
        foreach (var path in logPaths)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!_cursors.TryGetValue(path, out var cursor)) _cursors[path] = cursor = new();
            var prefix = new byte[Math.Min(256L, stream.Length)];
            var prefixLength = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
            prefix = prefix[..prefixLength];
            if (stream.Length < cursor.Offset || prefix.Length < cursor.Prefix.Length ||
                !prefix.AsSpan(0, Math.Min(prefix.Length, cursor.Prefix.Length)).SequenceEqual(cursor.Prefix))
            {
                cursor = new();
                _cursors[path] = cursor;
            }
            if (cursor.Prefix.Length == 0) cursor.Prefix = prefix;
            stream.Position = cursor.Offset;
            using var content = new MemoryStream();
            stream.CopyTo(content);
            var bytes = content.ToArray();
            LastReadBytes += bytes.LongLength;
            var committedLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            // Parse before committing: an exception that HEAD does not suppress must be retried.
            var committed = ParseBytes(bytes.AsSpan(0, committedLength), cursor.Offset == 0);
            var provisional = ParseBytes(bytes.AsSpan(committedLength), cursor.Offset + committedLength == 0);
            cursor.Rows.AddRange(committed);
            cursor.Offset += committedLength;
            result.AddRange(cursor.Rows);
            result.AddRange(provisional);
        }
        return result;
    }

    private static List<(DateTimeOffset At, string Detail)> ParseBytes(ReadOnlySpan<byte> bytes, bool beginning)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (beginning && text.StartsWith('\uFEFF')) text = text[1..];
        using var reader = new StringReader(text);
        var rows = new List<(DateTimeOffset At, string Detail)>();
        while (reader.ReadLine() is { } line)
            if (ParseLine(line) is { } row) rows.Add(row);
        return rows;
    }

    internal static (DateTimeOffset At, string Detail)? ParseLine(string line)
    {
        if (!line.Contains("GOAL goal=", StringComparison.Ordinal) &&
            !line.Contains("ACCEPTANCE_COHORT_INFLIGHT ", StringComparison.Ordinal) &&
            !line.Contains("LOOP_RELAUNCH_DRAIN ", StringComparison.Ordinal) &&
            !line.Contains("ACCEPTANCE_COHORT_ADOPTION_REFUSED ", StringComparison.Ordinal) &&
            !ConductorRelaunchLogFold.Heads.Any(head => line.Contains(head, StringComparison.Ordinal))) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var detail = row.GetProperty("detail").GetString() ?? "";
            var at = row.GetProperty("timestamp").GetDateTimeOffset();
            return (at, detail);
        }
        catch (JsonException) { } // A partially appended line is not evidence.
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        catch (FormatException) { }
        return null;
    }
}
