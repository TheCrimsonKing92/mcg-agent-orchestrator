using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ReverseDependencyTestImpactLookupResult(
    bool Resolved, IReadOnlyList<string> TestClassNames, string? DegradationKind, string? Reason)
{
    public IReadOnlyList<string> AffectedTestLanes { get; init; } = [];
}

// Read-only observation with no focused-selection limits; the index size bound still applies.
public static class ReverseDependencyTestImpactReaderLookup
{
    private const string InfrastructureTestProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    public static ReverseDependencyTestImpactLookupResult Find(
        string repositoryRoot, IReadOnlyList<string> changedSourcePaths)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var manifestLanes = ReadManifestLanes(repositoryRoot);
        var lanes = new List<string>();
        var seenLanes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in changedSourcePaths.Select(path => path.Replace('\\', '/'))
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var owningLanes = FindOwningLanes(repositoryRoot, path, manifestLanes);
            if (owningLanes.Count > 0)
            {
                foreach (var lane in owningLanes)
                    if (seenLanes.Add(lane)) lanes.Add(lane);
                continue;
            }

            var selection = ReverseDependencyTestImpactReader.Read(repositoryRoot, [path],
                maximumSelectedTestClasses: int.MaxValue, maximumFrontierSymbols: int.MaxValue);
            if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved)
                return new(false, [], selection.DegradationKind?.ToString() ?? selection.Outcome.ToString(),
                    selection.Reason) { AffectedTestLanes = lanes.ToArray() };
            names.UnionWith(selection.TestClassNames);
        }
        return new(true, names.Order(StringComparer.Ordinal).ToArray(), null, null)
            { AffectedTestLanes = lanes.ToArray() };
    }

    private static IReadOnlyList<(string Project, string Name)> ReadManifestLanes(string repositoryRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(repositoryRoot, "config", "acceptance-manifest.json")));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("checks", out var checks) ||
                checks.ValueKind != JsonValueKind.Array)
                return [];

            var lanes = new List<(string Project, string Name)>();
            foreach (var check in checks.EnumerateArray())
            {
                if (check.ValueKind != JsonValueKind.Object ||
                    !check.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                    !check.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.String ||
                    !check.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    continue;
                var projectPath = project.GetString()!.Replace('\\', '/');
                var laneName = name.GetString()!;
                if (!string.IsNullOrWhiteSpace(projectPath) && !string.IsNullOrWhiteSpace(laneName) &&
                    !projectPath.Equals(InfrastructureTestProject, StringComparison.OrdinalIgnoreCase))
                    lanes.Add((projectPath, laneName));
            }
            return lanes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         JsonException or ArgumentException or NotSupportedException)
        {
            // Without readable manifest evidence every path uses the existing dependency lookup.
            return [];
        }
    }

    private static IReadOnlyList<string> FindOwningLanes(string repositoryRoot, string path,
        IReadOnlyList<(string Project, string Name)> manifestLanes)
    {
        if (manifestLanes.Count == 0) return [];
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, path)));
            while (directory is not null && (directory.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                                             directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var projects = Directory.EnumerateFiles(directory, "*.csproj")
                    .Select(project => Path.GetRelativePath(root, project).Replace('\\', '/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (projects.Count > 0)
                    return manifestLanes.Where(lane => projects.Contains(lane.Project))
                        .Select(lane => lane.Name).ToArray();
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException)
        {
            // Unresolved ownership is not evidence to skip the dependency lookup.
        }
        return [];
    }
}
