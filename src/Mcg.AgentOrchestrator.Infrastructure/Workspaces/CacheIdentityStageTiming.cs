using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Options and observations have invocation lifetime and carry no cache authority.
internal sealed record CacheIdentityDiagnosticContext(Action<CacheIdentityStageReceipt> Sink, TimeProvider? Clock = null);

internal sealed record CacheIdentityStageReceipt(string Fields)
{
    internal string Format(string prefix) => string.Join(" ", Fields.Split(' ').Select(field => prefix + field));

    internal static CacheIdentityStageReceipt Missing(string availability) =>
        CacheIdentityStageTiming.FormatMissing(availability);
}

internal sealed class CacheIdentityStageTiming : IDisposable
{
    internal enum Stage { RestoreCommand, EvaluationCommand, PreprocessingCommand, DirectoryHash, FileHash }
    private static readonly string[] Names =
        ["restore_command", "evaluation_command", "preprocessing_command", "directory_hash", "file_hash"];
    private readonly CacheIdentityDiagnosticContext _context;
    private readonly TimeProvider _clock;
    private readonly long[] _ticks = new long[5];
    private readonly long[] _counts = new long[5];
    private readonly bool[] _unknown = new bool[5];
    private readonly long _start;
    private readonly long _frequency;
    private long _last;
    private int _active;
    private bool _invalid;
    private bool _complete;
    private bool _published;
    private long _treeExecutions, _memoHits, _treeFiles, _treeBytes, _fileFiles, _fileBytes;

    internal CacheIdentityStageTiming(CacheIdentityDiagnosticContext context)
    {
        _context = context;
        _clock = context.Clock ?? TimeProvider.System;
        try
        {
            _frequency = _clock.TimestampFrequency;
            if (_frequency <= 0) _invalid = true;
        }
        catch (Exception) { _invalid = true; }
        _start = Read();
    }

    internal void Complete(bool resolved) => _complete = resolved;
    internal void TreeExecution() => _treeExecutions++;
    internal void MemoHit() => _memoHits++;

    internal IDisposable Measure(Stage stage)
    {
        var index = (int)stage;
        _counts[index]++;
        var start = Read();
        if (_active++ != 0) _invalid = true;
        return new Measurement(this, index, start);
    }

    // The caller has already opened the real input. Observe returned bytes, never file lengths
    // or a second read pass. Tree-contained files contribute work, but no nested file stage.
    internal Stream ObserveHashing(Stream stream, bool tree)
    {
        if (tree) _treeFiles++; else _fileFiles++;
        return new HashingStream(stream, bytes =>
        {
            if (tree) _treeBytes += bytes; else _fileBytes += bytes;
        });
    }

    private long Read()
    {
        try
        {
            var value = _clock.GetTimestamp();
            if (value < _last) _invalid = true;
            _last = value;
            return value;
        }
        catch (Exception) { _invalid = true; return _last; }
    }

    public void Dispose()
    {
        if (_published) return;
        _published = true;
        var end = Read();
        // Publication is outside the operation. Diagnostic failures cannot change identity
        // results or replace an exception from commands, hashing, or resolution.
        try { _context.Sink(Snapshot(end)); }
        catch (Exception) { }
    }

    private CacheIdentityStageReceipt Snapshot(long end)
    {
        var total = (decimal)end - _start;
        var assigned = _ticks.Sum(ticks => (decimal)ticks);
        var invalid = _invalid || _active != 0 || total < 0 || assigned > total;
        var fields = new List<string>
        {
            $"total_ms={(invalid ? "unknown" : Milliseconds(total))}",
            $"residual_ms={(invalid ? "unknown" : Milliseconds(total - assigned))}",
            $"availability={(invalid ? "unavailable" : "measured")}",
            $"accounting_status={(!invalid && _complete ? "complete" : "partial")}",
        };
        for (var index = 0; index < Names.Length; index++)
        {
            var unknown = invalid || _unknown[index];
            fields.Add($"{Names[index]}_ms={(unknown ? "unknown" : Milliseconds(_ticks[index]))}");
            fields.Add($"{Names[index]}_count={Number(_counts[index])}");
            fields.Add($"{Names[index]}_status={(unknown ? "unknown" : _counts[index] == 0 ? "not_run" : "measured")}");
        }
        AddWork(fields, Number(_treeExecutions), Number(_memoHits), Number(_treeFiles), Number(_treeBytes), Number(_fileFiles), Number(_fileBytes));
        return new(string.Join(" ", fields));
    }

    internal static CacheIdentityStageReceipt FormatMissing(string availability)
    {
        var fields = new List<string>
        {
            "total_ms=unknown", "residual_ms=unknown", $"availability={availability}", "accounting_status=partial"
        };
        foreach (var name in Names)
        {
            fields.Add($"{name}_ms=unknown");
            fields.Add($"{name}_count=unknown");
            fields.Add($"{name}_status=unknown");
        }
        AddWork(fields, "unknown", "unknown", "unknown", "unknown", "unknown", "unknown");
        return new(string.Join(" ", fields));
    }

    private static void AddWork(List<string> fields, string executions, string hits, string treeFiles, string treeBytes, string fileFiles, string fileBytes)
    {
        fields.Add($"directory_hash_executions={executions}");
        fields.Add($"directory_hash_memo_hits={hits}");
        fields.Add($"directory_hash_files={treeFiles}");
        fields.Add($"directory_hash_bytes={treeBytes}");
        fields.Add($"file_hash_files={fileFiles}");
        fields.Add($"file_hash_bytes={fileBytes}");
        fields.Add("command_scope=command_boundary_internals_unknown");
    }

    // Convert only after accumulating raw ticks, so repeated sub-millisecond calls survive.
    private string Milliseconds(decimal ticks) => decimal.Truncate(ticks * 1000 / _frequency).ToString(CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class Measurement(CacheIdentityStageTiming owner, int index, long start) : IDisposable
    {
        private bool _closed;
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            var end = owner.Read();
            owner._active--;
            if (end < start) owner._invalid = owner._unknown[index] = true;
            else
            {
                try { owner._ticks[index] = checked(owner._ticks[index] + checked(end - start)); }
                catch (OverflowException) { owner._invalid = owner._unknown[index] = true; }
            }
        }
    }

    private sealed class HashingStream(Stream source, Action<int> read) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytes = source.Read(buffer, offset, count);
            read(bytes);
            return bytes;
        }
        public override int Read(Span<byte> buffer)
        {
            var bytes = source.Read(buffer);
            read(bytes);
            return bytes;
        }
        public override int ReadByte()
        {
            var value = source.ReadByte();
            if (value >= 0) read(1);
            return value;
        }
        public override void Flush() => source.Flush();
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
