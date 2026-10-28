using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalSnapshotArchiveCodec
{
    private static ReadOnlySpan<byte> Marker => "MGB1"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Projection consumers can skip task/output payloads without making a decoded JSON string.
    // Match Decode's complete-frame, no-trailing-data and strict UTF-8 validation.
    internal static Stream? OpenDecodedStream(object? value) => value switch
    {
        null or DBNull => null,
        string json => new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false),
        byte[] bytes when bytes.AsSpan().StartsWith(Marker) => new ArchiveReadStream(bytes),
        _ => throw new InvalidDataException("Unsupported goal_snapshot_archive snapshot encoding.")
    };

    private sealed class ArchiveReadStream(byte[] bytes) : Stream
    {
        private BrotliDecoder decoder = new();
        private readonly Decoder utf8 = StrictUtf8.GetDecoder();
        private int offset = Marker.Length;
        private bool done;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (done || buffer.IsEmpty) return 0;
            var status = decoder.Decompress(bytes.AsSpan(offset), buffer, out var consumed, out var written);
            offset += consumed;
            done = status == OperationStatus.Done && offset == bytes.Length;
            if (!done && (status != OperationStatus.DestinationTooSmall || (consumed == 0 && written == 0)))
                throw new InvalidDataException("Invalid or incomplete goal_snapshot_archive Brotli payload.");
            try
            {
                var chars = ArrayPool<char>.Shared.Rent(written + 2);
                try { utf8.Convert(buffer[..written], chars, done, out _, out _, out _); }
                finally { ArrayPool<char>.Shared.Return(chars); }
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Invalid goal_snapshot_archive UTF-8 payload.", ex);
            }
            return written;
        }

        protected override void Dispose(bool disposing)
        {
            decoder.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static byte[] Encode(string json)
    {
        using var output = new MemoryStream();
        output.Write(Marker);
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    public static string? Decode(object? value)
    {
        if (value is null or DBNull)
            return null;
        if (value is string json)
            return json;
        if (value is not byte[] bytes || !bytes.AsSpan().StartsWith(Marker))
            throw new InvalidDataException("Unsupported goal_snapshot_archive snapshot encoding.");

        using var decoder = new BrotliDecoder();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var offset = Marker.Length;
        while (true)
        {
            var status = decoder.Decompress(bytes.AsSpan(offset), buffer, out var consumed, out var written);
            offset += consumed;
            output.Write(buffer, 0, written);
            if (status == OperationStatus.Done && offset == bytes.Length)
                break;
            if (status != OperationStatus.DestinationTooSmall || (consumed == 0 && written == 0))
                throw new InvalidDataException("Invalid or incomplete goal_snapshot_archive Brotli payload.");
        }

        try
        {
            return StrictUtf8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid goal_snapshot_archive UTF-8 payload.", ex);
        }
    }
}
