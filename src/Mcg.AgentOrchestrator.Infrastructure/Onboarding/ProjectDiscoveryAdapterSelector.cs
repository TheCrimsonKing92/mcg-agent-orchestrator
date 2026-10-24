using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Selects discovery from repository declarations without executing tools.</summary>
public static class ProjectDiscoveryAdapterSelector
{
    public sealed record Detection(ProjectStack? Stack, string? Marker, string? Refusal);

    public static Detection Detect(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");

        var rootFiles = Directory.GetFiles(root);
        var solution = rootFiles.Where(file => Path.GetExtension(file).ToLowerInvariant() is ".sln" or ".slnx")
            .Where(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).FirstOrDefault();
        var goMod = Path.Combine(root, "go.mod");
        var hasGoMod = File.Exists(goMod) && (File.GetAttributes(goMod) & FileAttributes.ReparsePoint) == 0;
        if (hasGoMod && solution is not null)
            return new(null, null, $"Ambiguous stack markers: go.mod and {solution}.");
        if (hasGoMod)
            return new(ProjectStack.Go, "go.mod", null);
        if (solution is not null)
            return new(ProjectStack.Dotnet, solution, null);

        var exclusions = new HashSet<string>(excludedDirectoryNames ?? [], StringComparer.OrdinalIgnoreCase);
        exclusions.UnionWith(["bin", "obj", ".git"]);
        var marker = ProjectMarkers(root, exclusions).Order(StringComparer.Ordinal).FirstOrDefault();
        return marker is not null ? new(ProjectStack.Dotnet, marker, null)
            : new(null, null, "No stack marker found; looked for root go.mod, *.sln, *.slnx, and *.csproj, *.fsproj, *.vbproj below the root.");
    }

    public static IProjectDiscoveryAdapter Select(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null)
    {
        var detection = Detect(repositoryRoot, excludedDirectoryNames);
        return detection.Stack switch
        {
            ProjectStack.Dotnet => new DotnetProjectDiscoveryAdapter(),
            ProjectStack.Go => throw new InvalidOperationException(
                $"No discovery adapter is available for stack 'go' (marker {detection.Marker})."),
            _ => throw new InvalidOperationException($"No discovery adapter is available for this repository: {detection.Refusal}")
        };
    }

    private static List<string> ProjectMarkers(string root, HashSet<string> exclusions)
    {
        var markers = new List<string>();
        Visit(ReadEntries(root));
        EnsureRootPresent();
        return markers;

        static (string[] Files, string[] Directories) ReadEntries(string directory) =>
            (Directory.GetFiles(directory), Directory.GetDirectories(directory));

        void EnsureRootPresent()
        {
            if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
                throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        void Visit((string[] Files, string[] Directories) entries)
        {
            foreach (var file in entries.Files)
                if (Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" &&
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    markers.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));

            foreach (var directory in entries.Directories)
            {
                if (exclusions.Contains(Path.GetFileName(directory)))
                    continue;
                (string[] Files, string[] Directories) children;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    children = ReadEntries(directory);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    EnsureRootPresent();
                    continue;
                }
                Visit(children);
            }
        }
    }
}
