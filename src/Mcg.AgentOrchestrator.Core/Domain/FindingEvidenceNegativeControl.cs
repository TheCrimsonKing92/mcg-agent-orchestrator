using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(FindingEvidenceNegativeControlJsonConverter))]
public enum FindingEvidenceNegativeControl
{
    RevertSrc
}

public sealed class FindingEvidenceNegativeControlJsonConverter : JsonConverter<FindingEvidenceNegativeControl>
{
    public override FindingEvidenceNegativeControl Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() == "revert-src"
            ? FindingEvidenceNegativeControl.RevertSrc
            : throw new JsonException("negative_control must be 'revert-src'");

    public override void Write(Utf8JsonWriter writer, FindingEvidenceNegativeControl value, JsonSerializerOptions options)
    {
        if (value != FindingEvidenceNegativeControl.RevertSrc)
            throw new JsonException("negative_control must be 'revert-src'");
        writer.WriteStringValue("revert-src");
    }
}

[JsonConverter(typeof(FindingEvidenceNegativeControlOutcomeJsonConverter))]
public enum FindingEvidenceNegativeControlOutcome
{
    Inconclusive,
    Demonstrated,
    NotDemonstrated
}

public sealed class FindingEvidenceNegativeControlOutcomeJsonConverter : JsonConverter<FindingEvidenceNegativeControlOutcome>
{
    public override FindingEvidenceNegativeControlOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "negative-control-inconclusive" => FindingEvidenceNegativeControlOutcome.Inconclusive,
                "negative-control-demonstrated" => FindingEvidenceNegativeControlOutcome.Demonstrated,
                "negative-control-not-demonstrated" => FindingEvidenceNegativeControlOutcome.NotDemonstrated,
                _ => throw new JsonException("Unknown negative-control outcome")
            }
            : throw new JsonException("Expected a negative-control outcome string");

    public override void Write(Utf8JsonWriter writer, FindingEvidenceNegativeControlOutcome value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWireValue(value));

    public static string ToWireValue(FindingEvidenceNegativeControlOutcome value) => value switch
    {
        FindingEvidenceNegativeControlOutcome.Inconclusive => "negative-control-inconclusive",
        FindingEvidenceNegativeControlOutcome.Demonstrated => "negative-control-demonstrated",
        FindingEvidenceNegativeControlOutcome.NotDemonstrated => "negative-control-not-demonstrated",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
