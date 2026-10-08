using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Sequence authority is the file under a path-scoped cross-process gate, never an instance counter.
internal sealed class ChangeStreamWriter(
    string path,
    long maxBytes = ConductEventLogWriter.DefaultMaxBytes,
    int rotatedGenerationCount = ConductEventLogWriter.DefaultRotatedGenerationCount)
{
    internal const string FileName = "change-stream.log";
    private readonly string _mutexName = ConductEventLogWriter.RequiredEventMutexName(path) + ".ChangeStream";

    internal ChangeStreamRecord? Append(string eventKind, string? goalId, string detail, DateTimeOffset timestamp)
    {
        if (!ChangeStreamRecord.TryClassify(eventKind, goalId, out var kind)) return null;
        using var gate = new Mutex(false, _mutexName);
        var ownsGate = false;
        try
        {
            try { ownsGate = gate.WaitOne(); }
            catch (AbandonedMutexException) { ownsGate = true; }
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var sequence = checked(PeekHighestSequence(path) + 1);
            RotateIfNeeded(timestamp);
            var record = new ChangeStreamRecord(ChangeStreamRecord.CurrentSchemaVersion, sequence,
                timestamp, kind!, goalId!, eventKind, detail);
            // Separate an interrupted tail from the next complete record, making corruption observable.
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (stream.Length > 0)
                {
                    stream.Position = stream.Length - 1;
                    if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
                }
                stream.Position = stream.Length;
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions) + Environment.NewLine);
                stream.Write(bytes);
            }
            PruneRotatedGenerations();
            return record;
        }
        finally { if (ownsGate) gate.ReleaseMutex(); }
    }

    // Only ConductEventLogWriter calls this, after its conduct append succeeds under its gate.
    internal void AppendConductLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var kind = root.GetProperty("eventKind").GetString() ?? string.Empty;
            var goal = root.TryGetProperty("goalId", out var id) ? id.GetString() : null;
            if (!ChangeStreamRecord.TryClassify(kind, goal, out _)) return;
            Append(kind, goal, root.GetProperty("detail").GetString() ?? string.Empty,
                root.GetProperty("timestamp").GetDateTimeOffset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // The conduct line is already committed. Do not retry or duplicate it on typed-write failure.
        }
    }

    internal static long PeekHighestSequence(string file)
    {
        var current = ReadLastSequence(file);
        if (current > 0) return current;
        var directory = Path.GetDirectoryName(file) ?? ".";
        if (!Directory.Exists(directory)) return 0;
        return Directory.EnumerateFiles(directory,
                $"{Path.GetFileNameWithoutExtension(file)}-*{Path.GetExtension(file)}")
            .Select(ReadLastSequence).DefaultIfEmpty(0).Max();
    }

    private static long ReadLastSequence(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Normal appends read a bounded tail; expand only for a long or corrupt final record.
            var count = (int)Math.Min(stream.Length, 4096);
            while (count > 0)
            {
                var offset = stream.Length - count;
                stream.Position = offset;
                var bytes = new byte[count];
                stream.ReadExactly(bytes);
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                for (var index = lines.Length - 2; index >= (offset == 0 ? 0 : 1); index--)
                    if (ChangeStreamRecord.TryParse(lines[index].TrimEnd('\r'), out var record)) return record!.Sequence;
                if (offset == 0) return 0;
                count = (int)Math.Min(stream.Length, (long)count * 2);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return 0;
    }

    private void RotateIfNeeded(DateTimeOffset timestamp)
    {
        if (maxBytes <= 0 || !File.Exists(path) || new FileInfo(path).Length < maxBytes) return;
        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var stamp = timestamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var rotated = Path.Combine(directory, $"{stem}-{stamp}.log");
        for (var index = 1; File.Exists(rotated); index++)
            rotated = Path.Combine(directory, $"{stem}-{stamp}-{index}.log");
        File.Move(path, rotated);
    }

    private void PruneRotatedGenerations()
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        foreach (var file in Directory.EnumerateFiles(directory, $"{Path.GetFileNameWithoutExtension(path)}-*.log")
                     .Select(file => new FileInfo(file)).OrderByDescending(file => file.LastWriteTimeUtc)
                     .ThenByDescending(file => file.Name, StringComparer.Ordinal).Skip(Math.Max(0, rotatedGenerationCount)))
        {
            try { file.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
