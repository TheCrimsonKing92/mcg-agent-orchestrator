using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Selects discovery from repository declarations without executing tools.</summary>
public static class ProjectDiscoveryAdapterSelector
{
    // crosswalk 1fac141, detect/detect.go detectLua: on-disk source denominator.
    private const string CrosswalkSourceExtensions =
        "lua go js jsx ts tsx c h cc cpp cxx hpp hxx cs fs fsx vb py pyw rs java kt kts swift m mm rb php pl pm r scala ex exs erl hrl clj cljs cljc dart groovy sql vue svelte asm s el vim sh bash zsh fish ps1 bat cmd";
    private static readonly HashSet<string> SourceExtensions = new(CrosswalkSourceExtensions.Split(' '), StringComparer.OrdinalIgnoreCase);

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
        var packageJson = Path.Combine(root, "package.json");
        var hasPackageJson = File.Exists(packageJson) && (File.GetAttributes(packageJson) & FileAttributes.ReparsePoint) == 0;
        if (hasPackageJson && hasGoMod)
            return new(null, null, "Ambiguous stack markers: go.mod and package.json.");
        if (hasPackageJson && solution is not null)
            return new(null, null, $"Ambiguous stack markers: package.json and {solution}.");
        if (hasGoMod)
            return new(ProjectStack.Go, "go.mod", null);
        if (solution is not null)
            return new(ProjectStack.Dotnet, solution, null);

        var exclusions = new HashSet<string>(excludedDirectoryNames ?? [], StringComparer.OrdinalIgnoreCase);
        exclusions.UnionWith(["bin", "obj", ".git"]);
        var marker = ProjectMarkers(root, exclusions).Order(StringComparer.Ordinal).FirstOrDefault();
        if (hasPackageJson)
            return marker is null ? new(ProjectStack.Node, "package.json", null)
                : new(null, null, $"Ambiguous stack markers: package.json and {marker}.");
        return marker is not null ? new(ProjectStack.Dotnet, marker, null)
            : DetectLua(root, exclusions);
    }

    public static IProjectDiscoveryAdapter Select(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null)
    {
        var detection = Detect(repositoryRoot, excludedDirectoryNames);
        return detection.Stack switch
        {
            ProjectStack.Dotnet => new DotnetProjectDiscoveryAdapter(),
            ProjectStack.Node => new NodeProjectDiscoveryAdapter(),
            ProjectStack.Lua => new LuaProjectDiscoveryAdapter(),
            ProjectStack.Go when GoModReader.Read(Path.Combine(repositoryRoot, "go.mod")).Path is not null => new GoProjectDiscoveryAdapter(),
            ProjectStack.Go => throw new InvalidOperationException(
                $"No discovery adapter is available for stack 'go' (marker {detection.Marker})."),
            _ => throw new InvalidOperationException($"No discovery adapter is available for this repository: {detection.Refusal}")
        };
    }

    private static Detection DetectLua(string root, HashSet<string> exclusions)
    {
        var files = LuaProjectDiscoveryAdapter.RepositoryFiles(root, exclusions);
        var marker = LuaProjectDiscoveryAdapter.BustedEvidence(files) ?? LuaProjectDiscoveryAdapter.Rockspec(files);
        foreach (var name in new[] { ".luarc.json", ".luarc.jsonc", ".luacheckrc", "stylua.toml", ".stylua.toml", "selene.toml" })
            marker ??= files.FirstOrDefault(file => file.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (marker is not null)
            return new(ProjectStack.Lua, marker, null);

        var sourceFiles = files.Where(file => SourceExtensions.Contains(Path.GetExtension(file).TrimStart('.'))).ToArray();
        var luaCount = sourceFiles.Count(file => Path.GetExtension(file).Equals(".lua", StringComparison.OrdinalIgnoreCase));
        return luaCount > sourceFiles.Length / 2 ? new(ProjectStack.Lua, "lua-majority", null)
            : new(null, null, "No stack marker found; looked for root go.mod, package.json, *.sln, *.slnx, and *.csproj, *.fsproj, *.vbproj below the root.");
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
