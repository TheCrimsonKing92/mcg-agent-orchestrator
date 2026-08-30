using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Core;

internal static class ReverseDependencyTestImpactReader
{
    private const string InfrastructureTestProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const int MaximumChangedSourceFiles = 5;
    private const int MaximumDependencyHops = 2;
    private const int MaximumFrontierSymbols = 64;
    private const int MaximumSelectedTestClasses = 16;
    private const int MaximumIndexedSourceFiles = 2_000;

    internal static ReverseDependencyTestSelection Read(
        string repositoryRoot,
        IReadOnlyList<string> changedSourcePaths)
    {
        if (changedSourcePaths.Count is 0 or > MaximumChangedSourceFiles)
        {
            return ReverseDependencyTestSelection.Abandoned(
                $"Focused reverse-dependency selection supports 1-{MaximumChangedSourceFiles} changed source files.");
        }

        try
        {
            repositoryRoot = Path.GetFullPath(repositoryRoot);
            var testProjectPath = ResolveWithinRepository(repositoryRoot, InfrastructureTestProject);
            if (testProjectPath is null || !File.Exists(testProjectPath))
            {
                return ReverseDependencyTestSelection.Unreadable(
                    $"The dependent test project could not be read: {InfrastructureTestProject}");
            }

            var projectPaths = ReadProjectClosure(repositoryRoot, testProjectPath, out var projectFailure);
            if (projectPaths is null)
            {
                return ReverseDependencyTestSelection.Unreadable(projectFailure!);
            }

            var indexedFiles = new Dictionary<string, IndexedSourceFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var projectPath in projectPaths)
            {
                var projectDirectory = Path.GetDirectoryName(projectPath)!;
                foreach (var sourcePath in FileSystemTestClassDeclarationReader
                    .EnumerateProjectSourceFiles(projectDirectory))
                {
                    indexedFiles.TryAdd(
                        sourcePath,
                        new IndexedSourceFile(
                            sourcePath,
                            projectPath.Equals(testProjectPath, StringComparison.OrdinalIgnoreCase),
                            Symbols: null));
                }
            }

            if (indexedFiles.Count > MaximumIndexedSourceFiles)
            {
                return ReverseDependencyTestSelection.Abandoned(
                    $"Reverse-dependency indexing exceeded the {MaximumIndexedSourceFiles}-source-file bound.");
            }

            foreach (var path in indexedFiles.Keys.Order(StringComparer.Ordinal).ToArray())
            {
                if (!CSharpTestClassScanner.TryReadSourceSymbols(
                    File.ReadAllText(path),
                    out var symbols))
                {
                    return ReverseDependencyTestSelection.Unreadable(
                        $"Reverse-dependency source could not be parsed: {ToRepositoryPath(repositoryRoot, path)}");
                }

                indexedFiles[path] = indexedFiles[path] with { Symbols = symbols };
            }

            var changedPaths = new List<string>();
            foreach (var changedSourcePath in changedSourcePaths
                .Select(NormalizeRepositoryPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal))
            {
                var fullPath = ResolveWithinRepository(repositoryRoot, changedSourcePath);
                if (fullPath is null || !indexedFiles.ContainsKey(fullPath))
                {
                    return ReverseDependencyTestSelection.Unreadable(
                        $"Changed source is outside the dependent project graph or unreadable: {changedSourcePath}");
                }

                changedPaths.Add(fullPath);
            }

            var declarationsByName = indexedFiles.Values
                .SelectMany(file => file.Symbols!.DeclaredTypes.Select(declaration => (file, declaration)))
                .GroupBy(pair => pair.declaration.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var frontier = changedPaths
                .SelectMany(path => indexedFiles[path].Symbols!.DeclaredTypes)
                .Select(declaration => declaration.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (frontier.Length == 0)
            {
                return ReverseDependencyTestSelection.Unreadable(
                    "Changed source declared no top-level type for reverse-dependency selection.");
            }

            var seenPaths = changedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedTestClasses = new SortedSet<string>(StringComparer.Ordinal);
            for (var hop = 1; hop <= MaximumDependencyHops; hop++)
            {
                if (frontier.Length > MaximumFrontierSymbols)
                {
                    return ReverseDependencyTestSelection.Abandoned(
                        $"Reverse-dependency hop {hop} exceeded the {MaximumFrontierSymbols}-symbol frontier bound.");
                }

                foreach (var symbol in frontier)
                {
                    if (declarationsByName.TryGetValue(symbol, out var declarations) &&
                        declarations.Length > 1 &&
                        declarations.Any(pair => !pair.declaration.IsPartial))
                    {
                        return ReverseDependencyTestSelection.Abandoned(
                            $"Reverse-dependency symbol '{symbol}' has ambiguous non-partial declarations.");
                    }
                }

                var frontierSet = frontier.ToHashSet(StringComparer.Ordinal);
                var consumers = indexedFiles.Values
                    .Where(file => !seenPaths.Contains(file.Path))
                    .Where(file => file.Symbols!.ReferencedIdentifiers.Overlaps(frontierSet))
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToArray();
                foreach (var consumer in consumers.Where(file => file.IsTargetTestProject))
                {
                    selectedTestClasses.UnionWith(consumer.Symbols!.TestClassNames);
                    if (selectedTestClasses.Count > MaximumSelectedTestClasses)
                    {
                        return ReverseDependencyTestSelection.Abandoned(
                            $"Reverse-dependency selection exceeded the {MaximumSelectedTestClasses}-test-class bound.");
                    }
                }

                foreach (var consumer in consumers)
                {
                    seenPaths.Add(consumer.Path);
                }

                frontier = consumers
                    .Where(file => !file.IsTargetTestProject)
                    .SelectMany(file => file.Symbols!.DeclaredTypes)
                    .Select(declaration => declaration.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (frontier.Length == 0)
                {
                    break;
                }
            }

            return ReverseDependencyTestSelection.Resolved(selectedTestClasses);
        }
        catch (IOException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency source I/O failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency source access failed: {exception.Message}");
        }
        catch (System.Xml.XmlException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency project graph is malformed: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency path evidence is malformed: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency path evidence is unsupported: {exception.Message}");
        }
    }

    private static string[]? ReadProjectClosure(
        string repositoryRoot,
        string testProjectPath,
        out string? failure)
    {
        failure = null;
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(testProjectPath);
        while (pending.Count > 0)
        {
            var projectPath = pending.Dequeue();
            if (!found.Add(projectPath))
            {
                continue;
            }

            var document = XDocument.Load(projectPath, LoadOptions.None);
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var projectReferences = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("ProjectReference", StringComparison.Ordinal))
                .ToArray();
            if (projectReferences.Any(reference =>
                string.IsNullOrWhiteSpace(reference.Attribute("Include")?.Value)))
            {
                failure = $"ProjectReference in '{ToRepositoryPath(repositoryRoot, projectPath)}' has no Include path.";
                return null;
            }

            foreach (var reference in projectReferences
                .Select(element => element.Attribute("Include")!.Value)
                .Order(StringComparer.Ordinal))
            {
                var referencedPath = Path.GetFullPath(Path.Combine(projectDirectory, reference));
                if (!IsWithinRepository(repositoryRoot, referencedPath) || !File.Exists(referencedPath))
                {
                    failure = $"Project reference is outside the repository or unreadable: {reference}";
                    return null;
                }

                pending.Enqueue(referencedPath);
            }
        }

        return found.Order(StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeRepositoryPath(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('/');

    private static string? ResolveWithinRepository(string repositoryRoot, string repositoryRelativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return IsWithinRepository(repositoryRoot, fullPath) ? fullPath : null;
    }

    private static bool IsWithinRepository(string repositoryRoot, string path)
    {
        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRepositoryPath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

    private sealed record IndexedSourceFile(
        string Path,
        bool IsTargetTestProject,
        CSharpTestClassScanner.SourceSymbols? Symbols);
}
