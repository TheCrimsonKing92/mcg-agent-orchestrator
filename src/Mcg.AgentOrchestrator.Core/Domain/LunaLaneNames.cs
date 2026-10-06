using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public static class LunaLaneNames
{
    public const string LunaProfileName = "codex-luna";
    public const string RetiredProfileName = "codex-spark";
    public const string RetiredProviderKindName = "OpenAICodexSpark";

    [return: NotNullIfNotNull(nameof(name))]
    public static string? NormalizeProfileName(string? name) =>
        string.Equals(name, RetiredProfileName, StringComparison.OrdinalIgnoreCase)
            ? LunaProfileName
            : name;

    [return: NotNullIfNotNull(nameof(lane))]
    public static string? NormalizeDispatchLane(string? lane)
    {
        if (string.Equals(lane, RetiredProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return LunaProfileName;
        }

        return lane is not null && lane.StartsWith(RetiredProfileName + ":", StringComparison.OrdinalIgnoreCase)
            ? LunaProfileName + lane[RetiredProfileName.Length..]
            : lane;
    }
}

public sealed class ProviderKindJsonConverter : JsonConverter<ProviderKind>
{
    // Delegate the ordinary enum contract, including undefined integers, to the platform converter.
    private static readonly JsonConverter<ProviderKind> DefaultConverter =
        (JsonConverter<ProviderKind>)new JsonStringEnumConverter<ProviderKind>()
            .CreateConverter(typeof(ProviderKind), new JsonSerializerOptions());

    public override ProviderKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            string.Equals(reader.GetString()?.Trim(), LunaLaneNames.RetiredProviderKindName, StringComparison.OrdinalIgnoreCase))
        {
            return ProviderKind.OpenAICodexLuna;
        }

        return DefaultConverter.Read(ref reader, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, ProviderKind value, JsonSerializerOptions options) =>
        DefaultConverter.Write(writer, value, options);
}
