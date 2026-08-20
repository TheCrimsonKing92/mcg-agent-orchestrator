namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class RollingLogWriteStream : Stream
{
    internal const long DefaultMaxBytes = 12L * 1024 * 1024;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream _current;
    private int _partNumber;

    public RollingLogWriteStream(string path, FileMode mode, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.GetFullPath(path);
        _maxBytes = Math.Max(1, maxBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        _partNumber = FindNextPartNumber(_path);
        _current = Open(mode);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _current.Length;
    public override long Position { get => _current.Position; set => throw new NotSupportedException(); }

    public override void Flush() => _current.Flush();

    public override async Task FlushAsync(CancellationToken cancellationToken) =>
        await _current.FlushAsync(cancellationToken).ConfigureAwait(false);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _gate.Wait();
        try
        {
            RotateIfNeeded(count);
            _current.Write(buffer, offset, count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RotateIfNeeded(buffer.Length);
            await _current.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (_current.Length == 0 || _current.Length + incomingBytes <= _maxBytes)
        {
            return;
        }

        _current.Flush(flushToDisk: false);
        _current.Dispose();
        try
        {
            File.Move(_path, $"{_path}.part-{_partNumber++:0000}");
            _current = Open(FileMode.Create);
        }
        catch (IOException)
        {
            // A concurrent reader can temporarily deny rename on Windows. Reopen without truncating;
            // the next write retries rotation after that reader releases the file.
            _current = Open(FileMode.Append);
        }
        catch (UnauthorizedAccessException)
        {
            _current = Open(FileMode.Append);
        }
    }

    private FileStream Open(FileMode mode) =>
        new(_path, mode, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

    private static int FindNextPartNumber(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var prefix = Path.GetFileName(path) + ".part-";
        var highest = Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => int.TryParse(name![prefix.Length..], out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return highest + 1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current.Dispose();
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
