using System.Text;

// Capture-local state only: this helper never owns or stops a process.
internal sealed class TestChildProcessCaptureDiagnostics(Encoding stdoutEncoding, Encoding stderrEncoding)
{
    internal const int TailBytes = 16 * 1024;
    internal const int TextCharacters = 4096;
    private readonly object _gate = new();
    private readonly Tail _stdout = new(stdoutEncoding);
    private readonly Tail _stderr = new(stderrEncoding);
    private bool _frozen;

    internal void Publish(bool stdout, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (!_frozen)
                (stdout ? _stdout : _stderr).Append(bytes);
        }
    }

    internal void End(bool stdout, bool unavailable = false)
    {
        lock (_gate)
        {
            if (_frozen)
                return;
            var tail = stdout ? _stdout : _stderr;
            tail.Complete = !unavailable;
            tail.Unavailable = unavailable;
        }
    }

    // Freeze the existing rings in place, so cleanup cannot overwrite the snapshot or
    // double the byte-storage budget. No I/O, callbacks or decoding under this lock.
    internal void Freeze()
    {
        lock (_gate)
            _frozen = true;
    }

    internal Snapshot ReadFrozen()
    {
        Freeze();
        return new Snapshot(_stdout.Observed, _stderr.Observed,
            _stdout.Render("stdout"), _stderr.Render("stderr"));
    }

    internal sealed record Snapshot(long StdoutBytes, long StderrBytes, string Stdout, string Stderr);

    private sealed class Tail(Encoding encoding)
    {
        private readonly byte[] _bytes = new byte[TailBytes];
        private int _next;
        private int _count;
        internal long Observed { get; private set; }
        internal bool Complete { get; set; }
        internal bool Unavailable { get; set; }

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            Observed += bytes.Length;
            // Each drain read is bounded, but also support a larger direct publication.
            if (bytes.Length >= TailBytes)
            {
                bytes[^TailBytes..].CopyTo(_bytes);
                _next = 0;
                _count = TailBytes;
                return;
            }
            var first = Math.Min(bytes.Length, TailBytes - _next);
            bytes[..first].CopyTo(_bytes.AsSpan(_next));
            bytes[first..].CopyTo(_bytes);
            _next = (_next + bytes.Length) % TailBytes;
            _count = Math.Min(TailBytes, _count + bytes.Length);
        }

        internal string Render(string name)
        {
            var text = "";
            var truncated = Observed > _count;
            var unavailable = Unavailable;
            try
            {
                var replacementEncoding = (Encoding)encoding.Clone();
                replacementEncoding.DecoderFallback = new DecoderReplacementFallback("\uFFFD");
                var decoder = replacementEncoding.GetDecoder();
                var builder = new StringBuilder(TextCharacters + 1024);
                var characters = new char[1024];
                var start = (_next - _count + TailBytes) % TailBytes;
                var first = Math.Min(_count, TailBytes - start);
                Decode(_bytes.AsSpan(start, first), flush: first == _count);
                if (first != _count)
                    Decode(_bytes.AsSpan(0, _count - first), flush: true);
                // A UTF-16 window must not start with half of a valid surrogate pair.
                if (builder.Length > 0 && char.IsLowSurrogate(builder[0]) && truncated)
                    builder.Remove(0, 1);
                text = builder.ToString();

                void Decode(ReadOnlySpan<byte> bytes, bool flush)
                {
                    bool completed;
                    do
                    {
                        decoder.Convert(bytes, characters, flush, out var used, out var written, out completed);
                        builder.Append(characters, 0, written);
                        if (builder.Length > TextCharacters)
                        {
                            builder.Remove(0, builder.Length - TextCharacters);
                            truncated = true;
                        }
                        bytes = bytes[used..];
                    } while (!completed);
                }
            }
            catch (Exception)
            {
                // Diagnostics must never replace the initiating assertion. Exception
                // messages can contain child data; report availability without them.
                unavailable = true;
            }
            var state = $"{(Observed == 0 ? "empty" : "nonempty")}, " +
                $"{(Complete ? "complete" : "incomplete")}, {(truncated ? "truncated" : "untruncated")}";
            return $"{name} snapshot [{state}{(unavailable ? ", unavailable" : "")}]:\n{text}";
        }
    }
}
