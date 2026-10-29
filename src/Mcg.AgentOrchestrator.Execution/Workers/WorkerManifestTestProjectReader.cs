using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerManifestTestProjectReader
{
    internal static string ResolvePath(string workingDirectory, WorkerTargetHome targetHome) =>
        targetHome.AcceptanceManifestPath is { } resolveManifest
            ? resolveManifest(workingDirectory)
            : Path.Combine(workingDirectory, "config", "acceptance-manifest.json");

    internal static ManifestDeclaredTestProjects? Read(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var projects = new List<string?>();
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("checks", out var checks) &&
                checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    if (check.ValueKind == JsonValueKind.Object &&
                        check.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                        string.Equals(type.GetString(), "dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                        check.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.String)
                    {
                        projects.Add(project.GetString());
                    }
                }
            }

            return ManifestDeclaredTestProjects.From(projects);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
