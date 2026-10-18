using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Replaces only the existing focusedEvidence.mode token, preserving all surrounding bytes.
internal static class FocusedEvidenceModeRewrite
{
    internal static byte[]? Apply(byte[] original, string mode)
    {
        if (mode is not ("off" or "shadow" or "remote-first"))
            throw new ArgumentException("mode-unsupported", nameof(mode));
        var preamble = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var reader = new Utf8JsonReader(original.AsSpan(preamble));
        var blockSeen = false;
        var inBlock = false;
        int? start = null;
        var end = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1) inBlock = false;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.CurrentDepth == 1 && reader.ValueTextEquals("focusedEvidence"))
            {
                if (blockSeen || !reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    throw new InvalidOperationException("executors-file-invalid");
                blockSeen = inBlock = true;
            }
            else if (inBlock && reader.CurrentDepth == 2 && reader.ValueTextEquals("mode"))
            {
                if (start is not null || !reader.Read() || reader.TokenType != JsonTokenType.String ||
                    reader.GetString() is not ("off" or "shadow" or "remote-first"))
                    throw new InvalidOperationException("executors-file-invalid");
                start = preamble + checked((int)reader.TokenStartIndex);
                end = preamble + checked((int)reader.BytesConsumed);
            }
        }
        if (start is null) return null;
        var replacement = Encoding.UTF8.GetBytes($"\"{mode}\"");
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, start.Value));
        output.Write(replacement);
        output.Write(original.AsSpan(end));
        return output.ToArray();
    }
}
