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
    NotDemonstrated,
    CompileRed
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
                "negative-control-compile-red" => FindingEvidenceNegativeControlOutcome.CompileRed,
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
        FindingEvidenceNegativeControlOutcome.CompileRed => "negative-control-compile-red",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

[JsonConverter(typeof(FindingEvidenceRevertPathsRejectionJsonConverter))]
public enum FindingEvidenceRevertPathsRejection
{
    EmptyList,
    UnderTests,
    OutsideSrc,
    NotChangedByGoal,
    MutationUnderTests,
    MutationOutsideSrc,
    MutationNotChangedByGoal,
    MutationEmptyOldText,
    MutationUnchangedText,
    MutationOldTextNotFound,
    MutationOldTextAmbiguous
}

public sealed class FindingEvidenceRevertPathsRejectionJsonConverter : JsonConverter<FindingEvidenceRevertPathsRejection>
{
    public override FindingEvidenceRevertPathsRejection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "revert-paths-empty" => FindingEvidenceRevertPathsRejection.EmptyList,
            "revert-paths-under-tests" => FindingEvidenceRevertPathsRejection.UnderTests,
            "revert-paths-outside-src" => FindingEvidenceRevertPathsRejection.OutsideSrc,
            "revert-paths-not-changed" => FindingEvidenceRevertPathsRejection.NotChangedByGoal,
            "mutation-under-tests" => FindingEvidenceRevertPathsRejection.MutationUnderTests,
            "mutation-outside-src" => FindingEvidenceRevertPathsRejection.MutationOutsideSrc,
            "mutation-not-changed" => FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal,
            "mutation-old-text-empty" => FindingEvidenceRevertPathsRejection.MutationEmptyOldText,
            "mutation-old-text-equals-new-text" => FindingEvidenceRevertPathsRejection.MutationUnchangedText,
            "mutation-old-text-missing" => FindingEvidenceRevertPathsRejection.MutationOldTextNotFound,
            "mutation-old-text-ambiguous" => FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous,
            _ => throw new JsonException("Unknown revert_paths rejection")
        } : throw new JsonException("Expected a revert_paths rejection string");

    public override void Write(Utf8JsonWriter writer, FindingEvidenceRevertPathsRejection value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWireValue(value));

    public static string ToWireValue(FindingEvidenceRevertPathsRejection value) => value switch
    {
        FindingEvidenceRevertPathsRejection.EmptyList => "revert-paths-empty",
        FindingEvidenceRevertPathsRejection.UnderTests => "revert-paths-under-tests",
        FindingEvidenceRevertPathsRejection.OutsideSrc => "revert-paths-outside-src",
        FindingEvidenceRevertPathsRejection.NotChangedByGoal => "revert-paths-not-changed",
        FindingEvidenceRevertPathsRejection.MutationUnderTests => "mutation-under-tests",
        FindingEvidenceRevertPathsRejection.MutationOutsideSrc => "mutation-outside-src",
        FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal => "mutation-not-changed",
        FindingEvidenceRevertPathsRejection.MutationEmptyOldText => "mutation-old-text-empty",
        FindingEvidenceRevertPathsRejection.MutationUnchangedText => "mutation-old-text-equals-new-text",
        FindingEvidenceRevertPathsRejection.MutationOldTextNotFound => "mutation-old-text-missing",
        FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous => "mutation-old-text-ambiguous",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public static class FindingEvidenceRevertPaths
{
    public static string Normalize(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    public static string[] Canonicalize(IReadOnlyList<string> paths) =>
        paths.Select(Normalize).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();

    public static bool SamePaths(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null || right is null ? left is null && right is null :
            Canonicalize(left).SequenceEqual(Canonicalize(right), StringComparer.Ordinal);
}
