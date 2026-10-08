using Mcg.AgentOrchestrator.Core;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetEnvironmentNeedReader
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly string[] ManifestPaths = [".config/dotnet-tools.json", "dotnet-tools.json"];

    public static IReadOnlyList<EnvironmentNeed> Read(string root, List<UnitCommands> commands, List<ProjectOwnerQuestion> questions)
    {
        var needs = new List<EnvironmentNeed>();
        var global = Path.Combine(root, "global.json");
        var source = File.Exists(global) ? new FactSource("global.json", 1) : commands.FirstOrDefault()?.BuildCommand.Source;
        string? version = null;
        if (File.Exists(global) && DotnetProjectDiscoveryAdapter.IsSafePath(root, global))
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(global));
                using var json = JsonDocument.Parse(bytes, Options);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("sdk", out var sdk) &&
                    sdk.ValueKind == JsonValueKind.Object && sdk.TryGetProperty("version", out var value) &&
                    value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    version = value.GetString();
                    source = new FactSource("global.json", KeyLine(bytes, "sdk", "version"));
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
        }
        if (source is not null)
            needs.Add(new EnvironmentNeed("dotnet-sdk", DotnetProjectDiscoveryAdapter.Fact(version ?? "undetermined",
                version is null ? FactConfidence.Low : FactConfidence.High, source, "environment/dotnet-sdk",
                "Which .NET SDK version is required? No readable SDK pin establishes it.", questions)));

        var manifest = ManifestPaths.FirstOrDefault(relative =>
            File.Exists(Path.Combine(root, relative)) && DotnetProjectDiscoveryAdapter.IsSafePath(root, Path.Combine(root, relative)));
        if (manifest is null)
            return needs;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root, manifest)));
            using var json = JsonDocument.Parse(bytes, Options);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Object)
                throw new JsonException("A tool manifest requires a tools object.");
            foreach (var tool in tools.EnumerateObject().OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                var pinned = tool.Value.ValueKind == JsonValueKind.Object && tool.Value.TryGetProperty("version", out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(pinned)) pinned = null;
                needs.Add(new EnvironmentNeed(tool.Name, DotnetProjectDiscoveryAdapter.Fact(pinned ?? "undetermined",
                    pinned is null ? FactConfidence.Low : FactConfidence.High, new FactSource(manifest, KeyLine(bytes, "tools", tool.Name)),
                    $"environment/{tool.Name}", "Confirm this tool's required version; its manifest has no version pin.", questions)));
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            DotnetProjectDiscoveryAdapter.Ask("environment/tool-manifest", $"Confirm tool requirements: {exception.Message}",
                new FactSource(manifest, 1), questions);
        }
        return needs;
    }

    // Token offsets avoid mistaking comments or a different object's version key for evidence.
    private static int KeyLine(byte[] bytes, string parent, string key)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var inParent = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                inParent = reader.ValueTextEquals(parent);
            if (inParent && reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 2 && reader.ValueTextEquals(key))
                return 1 + Encoding.UTF8.GetString(bytes.AsSpan(0, checked((int)reader.TokenStartIndex))).Count(character => character == '\n');
        }
        throw new JsonException("The declaration source could not be located.");
    }
}
