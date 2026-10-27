using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalSnapshotArchiveCodec
{
    private static ReadOnlySpan<byte> Marker => "MGB1"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
