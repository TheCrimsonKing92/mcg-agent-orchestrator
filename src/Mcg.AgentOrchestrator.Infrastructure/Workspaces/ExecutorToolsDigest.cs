using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ExecutorToolsDigest
{
    internal const string ManifestFileName = "tools-digest.json";

    internal static string? Compute(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var files = Directory.EnumerateFiles(directory)
            .Where(path => string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Name: Path.GetFileName(path).ToLowerInvariant()))
            .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) return null;
        var lines = new StringBuilder();
        foreach (var file in files)
            lines.Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file.Path))))
                .Append("  ").Append(file.Name).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
    }

    internal static string? ReadManifest(string? text)
    {
        if (text is null) return null;
        try
        {
            using var json = JsonDocument.Parse(text.TrimStart('\uFEFF'));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("digest", out var value) || value.ValueKind != JsonValueKind.String)
                return null;
            var digest = value.GetString()!;
            return digest.Length == 64 && digest.All(char.IsAsciiHexDigit) ? digest.ToLowerInvariant() : null;
        }
        catch (JsonException) { return null; }
    }
}
