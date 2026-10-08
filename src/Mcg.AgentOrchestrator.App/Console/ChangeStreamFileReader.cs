using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Tails complete JSON lines; a discontinuity invalidates the entire available delta batch.
internal sealed class ChangeStreamFileReader
{
    internal const string Gap = "gap";
    internal const string Rotation = "rotation";
    internal const string UnknownSchema = "unknown-schema";
    private readonly string _path;
    private long _position;
    private long _sequence;
    private DateTime? _creationUtc;
    private string? _firstLine;
    private byte[] _partial = [];

    internal ChangeStreamFileReader(string path)
    {
        _path = path;
        Reanchor();
    }

    // Capture BEFORE reading state: changes committed during that read must still be replayed.
    internal long Reanchor()
    {
        _position = 0;
        _partial = [];
        _creationUtc = null;
        _firstLine = null;
        _sequence = 0;
        try
        {
            using var stream = Open();
            _creationUtc = File.GetCreationTimeUtc(_path);
            using var data = new MemoryStream();
            stream.CopyTo(data);
            var bytes = data.ToArray();
            var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            _position = end;
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _firstLine ??= line.TrimEnd('\r');
                if (ChangeStreamRecord.TryParse(line, out var record)) _sequence = record!.Sequence;
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        if (_sequence == 0) _sequence = ChangeStreamWriter.PeekHighestSequence(_path);
        return _sequence;
    }

    internal IReadOnlyList<ChangeStreamRecord> ReadAvailable(out string? discontinuity)
    {
        discontinuity = null;
        FileStream stream;
        try { stream = Open(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (_creationUtc is not null) { discontinuity = Rotation; Reanchor(); }
            return [];
        }
        using (stream)
        {
            var creation = File.GetCreationTimeUtc(_path);
            string? first;
            using (var header = new StreamReader(stream, Encoding.UTF8, leaveOpen: true)) first = header.ReadLine();
            if ((_creationUtc is not null && creation != _creationUtc) || stream.Length < _position ||
                (_firstLine is not null && first != _firstLine))
            {
                discontinuity = Rotation;
                return [];
            }
            _creationUtc = creation;
            if (stream.Length == _position) return [];
            stream.Position = _position;
            using var data = new MemoryStream();
            stream.CopyTo(data);
            _position = stream.Position;
            var bytes = new byte[_partial.Length + data.Length];
            _partial.CopyTo(bytes, 0);
            data.ToArray().CopyTo(bytes, _partial.Length);
            var records = new List<ChangeStreamRecord>();
            var start = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                var line = Encoding.UTF8.GetString(bytes, start, index - start).TrimEnd('\r');
                start = index + 1;
                _firstLine ??= line;
                if (!ChangeStreamRecord.TryParse(line, out var record) || record!.Schema != ChangeStreamRecord.CurrentSchemaVersion)
                    discontinuity ??= UnknownSchema;
                else
                {
                    // Already-covered deltas can be replayed; the session owns snapshot suppression.
                    if (record.Sequence > _sequence)
                    {
                        if (record.Sequence != _sequence + 1) discontinuity ??= Gap;
                        _sequence = record.Sequence;
                    }
                    records.Add(record);
                }
            }
            _partial = bytes[start..];
            return discontinuity is null ? records : [];
        }
    }

    private FileStream Open() => new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
