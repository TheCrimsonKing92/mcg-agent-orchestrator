using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Replaces one setting token; all surrounding bytes belong to the operator.
internal static class RemoteLaneExecutorSettingRewrite
{
    internal static byte[]? RewriteSlots(byte[] original, string executorId, int slots) =>
        Rewrite(original, executorId, slots);

    internal static byte[]? RewriteSampleEvery(byte[] original, int sampleEvery) =>
        Rewrite(original, null, sampleEvery);

    internal static byte[] RewriteEnabled(byte[] original, string executorId, bool enabled) =>
        Rewrite(original, executorId, 0, enabled) ?? original;

    private static byte[]? Rewrite(byte[] original, string? executorId, int value, bool? enabled = null)
    {
        try
        {
            var preamble = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var reader = new Utf8JsonReader(original.AsSpan(preamble));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Invalid();
            var seen = false;
            (int Start, int End)? span = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
                var target = reader.ValueTextEquals(executorId is null ? "focusedEvidence" : "executors");
                if (!reader.Read()) throw Invalid();
                if (target)
                {
                    if (seen) throw Invalid();
                    seen = true;
                    span = executorId is null ? ReadSample(ref reader) : ReadExecutors(ref reader, executorId, enabled);
                }
                else reader.Skip();
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw Invalid();
            if (!seen && executorId is not null) throw Invalid();
            if (span is null) return null;
            var replacement = Encoding.UTF8.GetBytes(enabled is { } admission ? admission ? "true" : "false"
                : value.ToString(CultureInfo.InvariantCulture));
            if (span.Value.Start == span.Value.End)
            {
                // Insert a legacy property before the first property, using its existing indentation.
                var first = preamble + span.Value.Start;
                var open = first - 1;
                while (open >= preamble && original[open] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') open--;
                var gap = Encoding.UTF8.GetString(original, open + 1, first - open - 1);
                replacement = Encoding.UTF8.GetBytes("\"enabled\": false," + gap);
            }
            using var output = new MemoryStream();
            output.Write(original.AsSpan(0, preamble + span.Value.Start));
            output.Write(replacement);
            output.Write(original.AsSpan(preamble + span.Value.End));
            return output.ToArray();
        }
        catch (JsonException error) { throw new InvalidOperationException("executors-file-invalid", error); }
    }

    private static (int, int)? ReadExecutors(ref Utf8JsonReader reader, string executorId, bool? enabled)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var found = false;
        (int, int)? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw Invalid();
            string? id = null;
            (int, int)? slots = null;
            int? firstProperty = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
                firstProperty ??= checked((int)reader.TokenStartIndex);
                var isId = reader.ValueTextEquals("id");
                var isSlots = reader.ValueTextEquals(enabled is null ? "slots" : "enabled");
                if (!reader.Read()) throw Invalid();
                if (isId)
                {
                    if (id is not null || reader.TokenType != JsonTokenType.String) throw Invalid();
                    id = reader.GetString();
                }
                else if (isSlots)
                {
                    if (slots is not null) throw Invalid();
                    if (enabled is null) slots = IntegerSpan(ref reader);
                    else
                    {
                        if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) throw Invalid();
                        slots = (checked((int)reader.TokenStartIndex), checked((int)reader.BytesConsumed));
                    }
                }
                else reader.Skip();
            }
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw Invalid();
            if (id == executorId)
            {
                found = true;
                result = slots ?? (enabled == false ? (firstProperty!.Value, firstProperty.Value) : null);
            }
        }
        if (!found) throw new InvalidOperationException("executor-not-found");
        return result;
    }

    private static (int, int)? ReadSample(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Invalid();
        (int, int)? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
            var target = reader.ValueTextEquals("sampleEvery");
            if (!reader.Read()) throw Invalid();
            if (target)
            {
                if (result is not null) throw Invalid();
                result = IntegerSpan(ref reader);
            }
            else reader.Skip();
        }
        return result;
    }

    private static (int, int) IntegerSpan(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out _)) throw Invalid();
        return (checked((int)reader.TokenStartIndex), checked((int)reader.BytesConsumed));
    }

    private static InvalidOperationException Invalid() => new("executors-file-invalid");
}
