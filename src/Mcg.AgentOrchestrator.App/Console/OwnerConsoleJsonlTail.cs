using System.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns one file's byte cursor and independent per-consumer parse caches; holds no open handle between reads.
internal sealed class OwnerConsoleJsonlTail(string path)
{
    private sealed record Line(long Start, string Text);
    private readonly object _sync = new();
    private readonly List<Line> _lines = [];
    private readonly Dictionary<string, object> _consumers = new(StringComparer.Ordinal);
    private long _position;
    private DateTime? _creationTime;
    private byte[] _probe = [];
    private long _contentBytesRead;
    private long _parseCalls;

    internal long ContentBytesRead { get { lock (_sync) return _contentBytesRead; } }
    internal long ParseCalls { get { lock (_sync) return _parseCalls; } }

    internal IReadOnlyList<T> Select<T>(string consumer, Func<string, T> parse, long windowBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(windowBytes);
        lock (_sync)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var creationTime = File.GetCreationTimeUtc(stream.SafeFileHandle);
            if (_creationTime != creationTime || length < _position || !MatchesProbe(stream))
            {
                _position = 0;
                _lines.Clear();
                _consumers.Clear();
                _probe = [];
            }
            _creationTime = creationTime;
            stream.Position = _position;
            // Bound this read to the length at open even while a writer continues appending.
            var bytes = new byte[checked((int)(length - _position))];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) break;
                read += count;
            }
            _contentBytesRead += read;
            var start = 0;
            var readStart = _position;
            for (var index = 0; index < read; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                _lines.Add(new(readStart + start, Decode(bytes.AsSpan(start, index - start), readStart + start)));
                start = index + 1;
            }
            _position = readStart + start;
            _probe = new byte[(int)Math.Min(256, _position)];
            stream.Position = _position - _probe.Length;
            stream.ReadExactly(_probe);

            if (!_consumers.TryGetValue(consumer, out var cached))
            {
                cached = new List<T>();
                _consumers.Add(consumer, cached);
            }
            if (cached is not List<T> values)
                throw new InvalidOperationException("A tail consumer must keep the same result type.");
            while (values.Count < _lines.Count)
            {
                _parseCalls++;
                values.Add(parse(_lines[values.Count].Text));
            }
            var offset = Math.Max(0, length - windowBytes);
            var result = new List<T>();
            for (var index = 0; index < _lines.Count; index++)
                if (offset == 0 || _lines[index].Start > offset) result.Add(values[index]);
            var fragment = bytes.AsSpan(start, read - start);
            var onlyBom = _position == 0 && fragment.SequenceEqual(Encoding.UTF8.Preamble);
            if (start < read && !onlyBom && (offset == 0 || _position > offset))
            {
                _parseCalls++;
                result.Add(parse(Decode(fragment, _position)));
            }
            return result;
        }
    }

    private bool MatchesProbe(FileStream stream)
    {
        stream.Position = _position - _probe.Length;
        Span<byte> actual = stackalloc byte[_probe.Length];
        stream.ReadExactly(actual);
        return actual.SequenceEqual(_probe);
    }

    private static string Decode(ReadOnlySpan<byte> bytes, long start)
    {
        if (start == 0 && bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        return Encoding.UTF8.GetString(bytes);
    }
}
