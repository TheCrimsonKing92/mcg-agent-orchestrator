using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkDetectResult(
    Toolchain Stack, IReadOnlyList<CrosswalkDetectResult.Evidence> EvidenceItems, string? TestFramework)
{
    internal sealed record Evidence(string Path, string Reason);

    internal static bool TryParse(string? json, out CrosswalkDetectResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("stack", out var stack) || stack.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
                return false;

            Toolchain? toolchain = stack.GetString() switch
            {
                "go" => Toolchain.Go,
                "node" => Toolchain.Node,
                "dotnet" => Toolchain.Dotnet,
                "python" => Toolchain.Python,
                "unknown" => Toolchain.Unknown,
                _ => null
            };
            if (toolchain is null)
                return false;

            var items = new List<Evidence>();
            foreach (var item in evidence.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String)
                    return false;
                items.Add(new(path.GetString()!, reason.GetString()!));
            }

            string? testFramework = null;
            if (root.TryGetProperty("testFramework", out var framework))
            {
                if (framework.ValueKind != JsonValueKind.String)
                    return false;
                testFramework = framework.GetString();
            }

            result = new(toolchain.Value, items, testFramework);
            return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
