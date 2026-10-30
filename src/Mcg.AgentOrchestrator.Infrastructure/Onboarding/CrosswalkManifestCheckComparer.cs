using System.Text.Json;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CrosswalkManifestCheckComparer
{
    internal static IReadOnlyList<ManifestCheckComparison> Compare(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The manifest must be an object.");
        var checks = document.RootElement.TryGetProperty("checks", out var declarations)
            ? declarations.Deserialize<Check[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Manifest checks must be an array.")
            : [];
        var results = new List<ManifestCheckComparison>();
        var matched = false;
        foreach (var check in checks)
        {
            if (check is null || check.Arguments is null)
                throw new JsonException("Each manifest check must be an object with an arguments array.");
            var equivalent = !matched && check.Type == "crosswalk-test" && check.Project == "." &&
                check.Command is null && check.Arguments.Count == 0;
            results.Add(new(check.Name, equivalent ? ManifestCheckResultKind.Match : ManifestCheckResultKind.MissingFromLearned,
                equivalent ? "crosswalk-test ." : null, check.Type, equivalent ? "" : "No learned crosswalk check matches this invocation."));
            matched |= equivalent;
        }
        if (!matched)
            results.Add(new(".", ManifestCheckResultKind.MissingFromManifest, "crosswalk-test .", null,
                "No manifest check runs crosswalk-test on the repository root."));
        return results;
    }
}
