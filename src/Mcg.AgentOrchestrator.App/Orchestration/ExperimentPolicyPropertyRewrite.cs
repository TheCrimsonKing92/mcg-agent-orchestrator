using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Edits root boolean tokens without dropping comments or changing unrelated JSON text.</summary>
internal static class ExperimentPolicyPropertyRewrite
{
    internal static string Rewrite(string json, string propertyName, bool desired)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        var replacements = new List<(int Start, int End)>();
        var propertyCount = 0;
        var closingOffset = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                closingOffset = checked((int)reader.TokenStartIndex);
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            propertyCount++;
            if (reader.GetString() != propertyName) continue;
            reader.Read();
            if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                throw new InvalidOperationException("policy-flag-not-boolean");
            replacements.Add((checked((int)reader.TokenStartIndex), checked((int)reader.BytesConsumed)));
        }
        var value = desired ? "true" : "false";
        if (replacements.Count == 0)
        {
            var insertion = (propertyCount == 0 ? "" : ",") + JsonSerializer.Serialize(propertyName) + ":" + value;
            return Encoding.UTF8.GetString(bytes.AsSpan(0, closingOffset)) + insertion +
                Encoding.UTF8.GetString(bytes.AsSpan(closingOffset));
        }
        using var output = new MemoryStream();
        var offset = 0;
        foreach (var (start, end) in replacements)
        {
            output.Write(bytes.AsSpan(offset, start - offset));
            output.Write(Encoding.UTF8.GetBytes(value));
            offset = end;
        }
        output.Write(bytes.AsSpan(offset));
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
