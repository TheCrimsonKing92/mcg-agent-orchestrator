using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AgentReadEnvelope
{
    public const string RemoteExecutorsSchema = "mcg.remote-executors";
    public const int RemoteExecutorsSchemaVersion = 1;
    public const string GoalsSchema = "mcg.goals";
    public const int GoalsSchemaVersion = 1;
    public const string AttentionSchema = "mcg.attention";
    public const int AttentionSchemaVersion = 1;
    public const string AttentionShowSchema = "mcg.attention-show";
    public const int AttentionShowSchemaVersion = 1;
    public const string GateStatusSchema = "mcg.gate-status";
    public const int GateStatusSchemaVersion = 1;

    internal static string Serialize<T>(string schema, int schemaVersion, T payload,
        JsonSerializerOptions payloadOptions, TimeProvider? time = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", schema);
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WriteString("generatedAt", (time ?? TimeProvider.System).GetUtcNow()
                .UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WritePropertyName("payload");
            writer.WriteRawValue(JsonSerializer.Serialize(payload, payloadOptions));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
