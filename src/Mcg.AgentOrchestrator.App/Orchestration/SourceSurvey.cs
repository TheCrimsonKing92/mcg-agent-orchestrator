using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SourceSurveyReport(
    string Root,
    int MaxFiles,
    int ReturnedFiles,
    int TotalMatchedFiles,
    IReadOnlyList<string> Files,
    IReadOnlyList<SourceSurveyGroup> Groups,
    IReadOnlyList<string> ExcludedDirectoryNames,
    string RecommendedCommand,
    string InventorySource = "filesystem-fallback",
    bool TraversalComplete = true,
    IReadOnlyList<string>? IncompleteReasons = null);

internal sealed record SourceSurveyGroup(string Directory, int Count);

internal static class SourceSurvey
{
    public const int DefaultMaxFiles = 200;

    public static SourceSurveyReport Build(string root, int maxFiles = DefaultMaxFiles)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Source survey root cannot be empty.", nameof(root));
        }

        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Source survey root '{fullRoot}' was not found.");
        }

        var limit = Math.Clamp(maxFiles, 1, 1000);
        var inventory = RepositorySourceInventory.Build(fullRoot);
        var files = new List<string>(limit);
        var total = 0;

        foreach (var file in inventory.Files)
        {
            total++;
            if (files.Count < limit)
            {
                files.Add(file);
            }
        }

        var groups = files
            .GroupBy(GetTopLevelDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceSurveyGroup(group.Key, group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SourceSurveyReport(
            fullRoot,
            limit,
            files.Count,
            total,
            files,
            groups,
            RepositorySourceInventory.ExcludedDirectoryNames,
            RepositorySourceInventory.BuildRecommendedRgCommand(),
            inventory.Origin,
            inventory.Complete,
            inventory.IncompleteReasons);
    }

    internal static IReadOnlyList<string> EnumerateSourceDirectories(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        return RepositorySourceInventory.Build(fullRoot).Directories
            .Where(directory => directory.Length > 0)
            .ToArray();
    }

    private static string GetTopLevelDirectory(string relativePath)
    {
        var index = relativePath.IndexOf('/', StringComparison.Ordinal);
        return index < 0 ? "." : relativePath[..index];
    }
}
