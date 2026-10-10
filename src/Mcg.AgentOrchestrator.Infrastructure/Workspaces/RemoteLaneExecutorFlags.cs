using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Boolean experiment view of the existing focused-evidence mode; rewrites only its token.</summary>
internal static class RemoteLaneExecutorFlags
{
    internal const string FocusedEvidenceShadow = "focusedEvidenceShadow";
    internal static IReadOnlyList<string> Names { get; } = [FocusedEvidenceShadow];
    internal static bool IsAllowed(string? propertyName) =>
        string.Equals(propertyName, FocusedEvidenceShadow, StringComparison.Ordinal);

    internal static bool? Read(string path, string propertyName)
    {
        if (!IsAllowed(propertyName)) throw new ArgumentException("property-not-allowlisted", nameof(propertyName));
        var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(path);
        return configuration.DisabledReason is null && configuration.FocusedEvidence.FaultReason is null
            ? configuration.FocusedEvidence.Mode == "shadow" : null;
    }

    // Null means the mode is absent. Insertion is deliberately outside this flag's contract.
    internal static byte[]? Rewrite(byte[] original, string propertyName, bool desired)
    {
        if (!IsAllowed(propertyName)) throw new ArgumentException("property-not-allowlisted", nameof(propertyName));
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
                    reader.GetString() is not ("off" or "shadow"))
                    throw new InvalidOperationException("executors-file-invalid");
                start = preamble + checked((int)reader.TokenStartIndex);
                end = preamble + checked((int)reader.BytesConsumed);
            }
        }
        if (start is null) return null;
        ReadOnlySpan<byte> replacement = desired ? "\"shadow\""u8 : "\"off\""u8;
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, start.Value));
        output.Write(replacement);
        output.Write(original.AsSpan(end));
        return output.ToArray();
    }
}
