using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Core;

internal static class ReverseDependencyTestImpactReader
{
    private const string InfrastructureTestProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    internal const int MaximumChangedSourceFiles = 5;
    private const int MaximumDependencyHops = 2;
    private const int MaximumFrontierSymbols = 64;
    // The current classifier closure resolves 49 consumer test classes. Keep a bounded focused
    // filter with room for new tests before widening to the full Infrastructure suite.
    internal const int MaximumSelectedTestClasses = 64;
    private const int MaximumIndexedSourceFiles = 2_000;
    private const int MaximumRetainedSnapshots = 4;
    private const string CacheSchema = "reverse-dependency-index-v1";
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CachedIndex>> CacheByFingerprint =
        new(StringComparer.Ordinal);
    private static readonly LinkedList<CachedIndex> CacheRecency = new();

    internal static ReverseDependencyTestSelection Read(
        string repositoryRoot,
        IReadOnlyList<string> changedSourcePaths,
        bool bypassCache = false)
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
            if (testProjectPath is null)
            {
                return ReverseDependencyTestSelection.Unreadable(
                    $"The dependent test project could not be read: {InfrastructureTestProject}");
            }

            if (!File.Exists(testProjectPath))
            {
                return IsDeliberatelyPartialRepositoryRoot(repositoryRoot)
                    ? ReverseDependencyTestSelection.Unavailable
                    : ReverseDependencyTestSelection.Unreadable(
                        $"The dependent test project could not be read: {InfrastructureTestProject}");
            }

            var projects = ReadProjectClosure(repositoryRoot, testProjectPath, out var projectFailure);
            if (projects is null)
            {
                return ReverseDependencyTestSelection.Unreadable(projectFailure!);
            }

            var sourceOwnership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                var projectDirectory = Path.GetDirectoryName(project.Path)!;
                foreach (var sourcePath in FileSystemTestClassDeclarationReader
                    .EnumerateProjectSourceFiles(projectDirectory))
                {
                    sourceOwnership.TryAdd(
                        sourcePath,
                        project.Path.Equals(testProjectPath, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (sourceOwnership.Count > MaximumIndexedSourceFiles)
            {
                return ReverseDependencyTestSelection.Abandoned(
                    $"Reverse-dependency indexing exceeded the {MaximumIndexedSourceFiles}-source-file bound.");
            }

            var snapshot = ReadSnapshot(
                repositoryRoot,
                projects,
                sourceOwnership,
                out var snapshotFailure);
            if (snapshot is null)
            {
                return ReverseDependencyTestSelection.Unreadable(snapshotFailure!);
            }

            var disposition = bypassCache
                ? ReverseDependencyCacheDisposition.Bypass
                : ReverseDependencyCacheDisposition.Miss;
            var reparsedFileCount = 0;
            ReverseDependencyIndex index;
            if (!bypassCache && TryGetCachedIndex(snapshot.Fingerprint, out var cachedIndex, out var hitCount))
            {
                index = cachedIndex;
                disposition = ReverseDependencyCacheDisposition.Hit;
                var hitReceipt = CreateReceipt(snapshot, disposition, reparsedFileCount, hitCount);
                return Select(index, changedSourcePaths, repositoryRoot, hitReceipt);
            }

            index = ParseSnapshot(snapshot, repositoryRoot, out var parseFailure, out reparsedFileCount)!;
            if (index is null)
            {
                var failedReceipt = CreateReceipt(
                    snapshot,
                    disposition,
                    reparsedFileCount,
                    RetainedSnapshotCount());
                return ReverseDependencyTestSelection.Unreadable(parseFailure!, failedReceipt);
            }

            var retainedSnapshotCount = bypassCache
                ? RetainedSnapshotCount()
                : RetainIndex(snapshot.Fingerprint, index);
            var receipt = CreateReceipt(snapshot, disposition, reparsedFileCount, retainedSnapshotCount);
            return Select(index, changedSourcePaths, repositoryRoot, receipt);
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
        catch (Exception exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency evidence failed: {exception.Message}");
        }
    }

    private static ReverseDependencyTestSelection Select(
        ReverseDependencyIndex index,
        IReadOnlyList<string> changedSourcePaths,
        string repositoryRoot,
        ReverseDependencyCacheReceipt receipt)
    {
        var indexedFiles = index.Files;
        try
        {
            var changedPaths = new List<string>();
            foreach (var changedSourcePath in changedSourcePaths
                .Select(NormalizeRepositoryPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal))
            {
                var fullPath = ResolveWithinRepository(repositoryRoot, changedSourcePath);
                if (fullPath is null)
                {
                    return ReverseDependencyTestSelection.Unreadable(
                        $"Changed source is outside the dependent project graph or unreadable: {changedSourcePath}",
                        receipt);
                }

                if (!indexedFiles.ContainsKey(fullPath))
                {
                    if (File.Exists(fullPath))
                    {
                        return ReverseDependencyTestSelection.Unreadable(
                            $"Changed source is outside the dependent project graph or unreadable: {changedSourcePath}",
                            receipt);
                    }

                    continue;
                }

                changedPaths.Add(fullPath);
            }

            if (changedPaths.Count == 0)
            {
                return ReverseDependencyTestSelection.Unavailable with { CacheReceipt = receipt };
            }

            var declarationsByName = indexedFiles.Values
                .SelectMany(file => file.Symbols!.DeclaredTypes.Select(declaration => (file, declaration)))
                .GroupBy(pair => pair.declaration.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var frontier = changedPaths
                .SelectMany(path => indexedFiles[path].Symbols!.DeclaredTypes)
                .Select(declaration => new FrontierSymbol(
                    declaration.Name,
                    TestSelectionMode.AllReferencingClasses))
                .Distinct()
                .OrderBy(symbol => symbol.Name, StringComparer.Ordinal)
                .ToArray();
            if (frontier.Length == 0)
            {
                return ReverseDependencyTestSelection.Unreadable(
                    "Changed source declared no top-level type for reverse-dependency selection.",
                    receipt);
            }

            var seenPaths = changedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedTestClasses = new SortedSet<string>(StringComparer.Ordinal);
            for (var hop = 1; hop <= MaximumDependencyHops; hop++)
            {
                if (frontier.Length > MaximumFrontierSymbols)
                {
                    return ReverseDependencyTestSelection.Abandoned(
                        $"Reverse-dependency hop {hop} exceeded the {MaximumFrontierSymbols}-symbol frontier bound.",
                        receipt);
                }

                foreach (var symbol in frontier.Select(item => item.Name).Distinct(StringComparer.Ordinal))
                {
                    if (declarationsByName.TryGetValue(symbol, out var declarations) &&
                        declarations.Length > 1 &&
                        declarations.Any(pair => !pair.declaration.IsPartial))
                    {
                        return ReverseDependencyTestSelection.Abandoned(
                            $"Reverse-dependency symbol '{symbol}' has ambiguous non-partial declarations.",
                            receipt);
                    }
                }

                var frontierSet = frontier
                    .Select(symbol => symbol.Name)
                    .ToHashSet(StringComparer.Ordinal);
                var consumers = indexedFiles.Values
                    .Where(file => !seenPaths.Contains(file.Path))
                    .Where(file => file.Symbols!.DeclaredTypes.Any(declaration =>
                        declaration.ReferencedIdentifiers.Overlaps(frontierSet)))
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToArray();
                foreach (var consumer in consumers.Where(file => file.IsTargetTestProject))
                {
                    var referencedFrontier = frontier
                        .Where(symbol => consumer.Symbols!.DeclaredTypes.Any(declaration =>
                            declaration.ReferencedIdentifiers.Contains(symbol.Name)))
                        .ToArray();
                    var allReferencingSymbols = referencedFrontier
                        .Where(symbol => symbol.TestSelectionMode == TestSelectionMode.AllReferencingClasses)
                        .Select(symbol => symbol.Name)
                        .ToHashSet(StringComparer.Ordinal);
                    var ownedClassNames = referencedFrontier
                        .Where(symbol => symbol.TestSelectionMode == TestSelectionMode.OwnedClassOnly)
                        .Select(symbol => $"{symbol.Name}Tests")
                        .ToHashSet(StringComparer.Ordinal);
                    selectedTestClasses.UnionWith(
                        consumer.Symbols!.DeclaredTypes
                            .Where(declaration => consumer.Symbols.TestClassNames.Contains(
                                declaration.Name,
                                StringComparer.Ordinal))
                            .Where(declaration =>
                                ownedClassNames.Contains(declaration.Name) ||
                                declaration.ReferencedIdentifiers.Overlaps(allReferencingSymbols))
                            .Select(declaration => declaration.Name));

                    if (selectedTestClasses.Count > MaximumSelectedTestClasses)
                    {
                        return ReverseDependencyTestSelection.Abandoned(
                            $"Reverse-dependency selection exceeded the {MaximumSelectedTestClasses}-test-class bound.",
                            receipt);
                    }
                }

                foreach (var consumer in consumers)
                {
                    seenPaths.Add(consumer.Path);
                }

                frontier = consumers
                    .SelectMany(file => file.Symbols!.DeclaredTypes
                        .Where(declaration => declaration.ReferencedIdentifiers.Overlaps(frontierSet))
                        .Where(declaration =>
                            !file.IsTargetTestProject ||
                            !file.Symbols.TestClassNames.Contains(declaration.Name, StringComparer.Ordinal))
                        .Select(declaration => new FrontierSymbol(
                            declaration.Name,
                            file.IsTargetTestProject
                                ? TestSelectionMode.AllReferencingClasses
                                : TestSelectionMode.OwnedClassOnly)))
                    .GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
                    .Select(group => new FrontierSymbol(
                        group.Key,
                        group.Any(symbol =>
                            symbol.TestSelectionMode == TestSelectionMode.AllReferencingClasses)
                            ? TestSelectionMode.AllReferencingClasses
                            : TestSelectionMode.OwnedClassOnly))
                    .OrderBy(symbol => symbol.Name, StringComparer.Ordinal)
                    .ToArray();
                if (frontier.Length == 0)
                {
                    break;
                }
            }

            return ReverseDependencyTestSelection.Resolved(selectedTestClasses, receipt);
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
        catch (Exception exception)
        {
            return ReverseDependencyTestSelection.Unreadable(
                $"Reverse-dependency evidence failed: {exception.Message}");
        }
    }

    internal static bool IsDeliberatelyPartialRepositoryRoot(string repositoryRoot) =>
        !Directory.Exists(Path.Combine(repositoryRoot, ".git")) &&
        !File.Exists(Path.Combine(repositoryRoot, ".git")) &&
        !File.Exists(Path.Combine(repositoryRoot, "Mcg.AgentOrchestrator.sln")) &&
        !File.Exists(Path.Combine(
            repositoryRoot,
            InfrastructureTestProject.Replace('/', Path.DirectorySeparatorChar)));

    private static ProjectSnapshot[]? ReadProjectClosure(
        string repositoryRoot,
        string testProjectPath,
        out string? failure)
    {
        failure = null;
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectSnapshot>();
        var pending = new Queue<string>();
        pending.Enqueue(testProjectPath);
        while (pending.Count > 0)
        {
            var projectPath = pending.Dequeue();
            if (!found.Add(projectPath))
            {
                continue;
            }

            var projectSource = File.ReadAllText(projectPath);
            var document = XDocument.Parse(projectSource, LoadOptions.None);
            projects.Add(new ProjectSnapshot(projectPath, HashText(projectSource)));
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

        return projects.OrderBy(project => project.Path, StringComparer.Ordinal).ToArray();
    }

    private static SourceSnapshot? ReadSnapshot(
        string repositoryRoot,
        IReadOnlyList<ProjectSnapshot> projects,
        IReadOnlyDictionary<string, bool> sourceOwnership,
        out string? failure)
    {
        failure = null;
        var sources = new List<SourceFileSnapshot>(sourceOwnership.Count);
        long sourceBytes = 0;
        foreach (var pair in sourceOwnership.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var before = ReadFileVersion(pair.Key);
            var source = File.ReadAllText(pair.Key);
            var after = ReadFileVersion(pair.Key);
            if (before != after)
            {
                failure =
                    $"Reverse-dependency source changed while it was indexed: {ToRepositoryPath(repositoryRoot, pair.Key)}";
                return null;
            }

            sourceBytes += after.Length;
            sources.Add(new SourceFileSnapshot(
                pair.Key,
                pair.Value,
                source,
                HashText(source),
                after));
        }

        foreach (var project in projects)
        {
            if (!HashText(File.ReadAllText(project.Path)).Equals(project.ContentHash, StringComparison.Ordinal))
            {
                failure =
                    $"Reverse-dependency project graph changed while it was indexed: {ToRepositoryPath(repositoryRoot, project.Path)}";
                return null;
            }
        }

        var currentOwnership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var testProjectPath = ResolveWithinRepository(repositoryRoot, InfrastructureTestProject)!;
        foreach (var project in projects)
        {
            foreach (var sourcePath in FileSystemTestClassDeclarationReader.EnumerateProjectSourceFiles(
                Path.GetDirectoryName(project.Path)!))
            {
                currentOwnership.TryAdd(
                    sourcePath,
                    project.Path.Equals(testProjectPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (currentOwnership.Count != sourceOwnership.Count ||
            currentOwnership.Any(pair =>
                !sourceOwnership.TryGetValue(pair.Key, out var ownership) || ownership != pair.Value))
        {
            failure = "Reverse-dependency source membership changed while it was indexed.";
            return null;
        }

        foreach (var source in sources)
        {
            if (ReadFileVersion(source.Path) != source.Version)
            {
                failure =
                    $"Reverse-dependency source changed while it was indexed: {ToRepositoryPath(repositoryRoot, source.Path)}";
                return null;
            }
        }

        var repositoryIdentity = ReadRepositoryIdentity(repositoryRoot);
        var fingerprint = BuildFingerprint(
            repositoryRoot,
            repositoryIdentity,
            projects,
            sources);
        return new SourceSnapshot(
            fingerprint,
            repositoryIdentity,
            projects,
            sources,
            sourceBytes);
    }

    private static ReverseDependencyIndex? ParseSnapshot(
        SourceSnapshot snapshot,
        string repositoryRoot,
        out string? failure,
        out int reparsedFileCount)
    {
        failure = null;
        reparsedFileCount = 0;
        var files = new Dictionary<string, IndexedSourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in snapshot.Sources)
        {
            reparsedFileCount++;
            if (!CSharpTestClassScanner.TryReadSourceSymbols(source.Source, out var symbols))
            {
                failure =
                    $"Reverse-dependency source could not be parsed: {ToRepositoryPath(repositoryRoot, source.Path)}";
                return null;
            }

            files.Add(
                source.Path,
                new IndexedSourceFile(source.Path, source.IsTargetTestProject, symbols));
        }

        return new ReverseDependencyIndex(files);
    }

    private static bool TryGetCachedIndex(
        string fingerprint,
        out ReverseDependencyIndex index,
        out int retainedSnapshotCount)
    {
        lock (CacheLock)
        {
            if (!CacheByFingerprint.TryGetValue(fingerprint, out var node))
            {
                index = null!;
                retainedSnapshotCount = CacheByFingerprint.Count;
                return false;
            }

            CacheRecency.Remove(node);
            CacheRecency.AddFirst(node);
            index = node.Value.Index;
            retainedSnapshotCount = CacheByFingerprint.Count;
            return true;
        }
    }

    private static int RetainIndex(string fingerprint, ReverseDependencyIndex index)
    {
        lock (CacheLock)
        {
            if (CacheByFingerprint.TryGetValue(fingerprint, out var existing))
            {
                CacheRecency.Remove(existing);
                CacheRecency.AddFirst(existing);
                return CacheByFingerprint.Count;
            }

            var node = CacheRecency.AddFirst(new CachedIndex(fingerprint, index));
            CacheByFingerprint.Add(fingerprint, node);
            while (CacheByFingerprint.Count > MaximumRetainedSnapshots)
            {
                var expired = CacheRecency.Last!;
                CacheRecency.RemoveLast();
                CacheByFingerprint.Remove(expired.Value.Fingerprint);
            }

            return CacheByFingerprint.Count;
        }
    }

    private static int RetainedSnapshotCount()
    {
        lock (CacheLock)
        {
            return CacheByFingerprint.Count;
        }
    }

    internal static void ClearCacheForTests()
    {
        lock (CacheLock)
        {
            CacheByFingerprint.Clear();
            CacheRecency.Clear();
        }
    }

    private static ReverseDependencyCacheReceipt CreateReceipt(
        SourceSnapshot snapshot,
        ReverseDependencyCacheDisposition disposition,
        int reparsedFileCount,
        int retainedSnapshotCount) =>
        new(
            disposition,
            snapshot.Fingerprint,
            snapshot.Sources.Count,
            snapshot.SourceBytes,
            reparsedFileCount,
            retainedSnapshotCount);

    private static string BuildFingerprint(
        string repositoryRoot,
        string repositoryIdentity,
        IReadOnlyList<ProjectSnapshot> projects,
        IReadOnlyList<SourceFileSnapshot> sources)
    {
        var builder = new StringBuilder();
        AppendIdentity(builder, CacheSchema);
        AppendIdentity(builder, Path.GetFullPath(repositoryRoot));
        AppendIdentity(builder, repositoryIdentity);
        foreach (var project in projects)
        {
            AppendIdentity(builder, ToRepositoryPath(repositoryRoot, project.Path));
            AppendIdentity(builder, project.ContentHash);
        }

        foreach (var source in sources)
        {
            AppendIdentity(builder, ToRepositoryPath(repositoryRoot, source.Path));
            AppendIdentity(builder, source.IsTargetTestProject ? "test" : "production");
            AppendIdentity(builder, source.ContentHash);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendIdentity(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static string HashText(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static FileVersion ReadFileVersion(string path)
    {
        var info = new FileInfo(path);
        return new FileVersion(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string ReadRepositoryIdentity(string repositoryRoot)
    {
        var markerPath = Path.Combine(repositoryRoot, ".git");
        if (!Directory.Exists(markerPath) && !File.Exists(markerPath))
        {
            return "git:none";
        }

        var gitDirectory = markerPath;
        var markerIdentity = "git:directory";
        if (File.Exists(markerPath))
        {
            var marker = File.ReadAllText(markerPath).Trim();
            markerIdentity = $"git:file:{HashText(marker)}";
            const string prefix = "gitdir:";
            if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return markerIdentity;
            }

            var declaredDirectory = marker[prefix.Length..].Trim();
            gitDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, declaredDirectory));
        }

        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            return markerIdentity;
        }

        var head = File.ReadAllText(headPath).Trim();
        var resolved = head;
        const string refPrefix = "ref:";
        if (head.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reference = head[refPrefix.Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            resolved = TryReadGitReference(gitDirectory, reference) ?? head;
        }

        return $"{markerIdentity};head={head};resolved={resolved}";
    }

    private static string? TryReadGitReference(string gitDirectory, string reference)
    {
        var directPath = Path.Combine(gitDirectory, reference);
        if (File.Exists(directPath))
        {
            return File.ReadAllText(directPath).Trim();
        }

        var commonDirectory = gitDirectory;
        var commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonDirectoryPath))
        {
            commonDirectory = Path.GetFullPath(Path.Combine(
                gitDirectory,
                File.ReadAllText(commonDirectoryPath).Trim()));
            var commonRefPath = Path.Combine(commonDirectory, reference);
            if (File.Exists(commonRefPath))
            {
                return File.ReadAllText(commonRefPath).Trim();
            }
        }

        var packedRefsPath = Path.Combine(commonDirectory, "packed-refs");
        if (!File.Exists(packedRefsPath))
        {
            return null;
        }

        var normalizedReference = reference.Replace(Path.DirectorySeparatorChar, '/');
        foreach (var line in File.ReadLines(packedRefsPath))
        {
            if (line.Length == 0 || line[0] is '#' or '^')
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            if (separator > 0 && line[(separator + 1)..].Equals(normalizedReference, StringComparison.Ordinal))
            {
                return line[..separator];
            }
        }

        return null;
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

    private sealed record ReverseDependencyIndex(
        IReadOnlyDictionary<string, IndexedSourceFile> Files);

    private sealed record CachedIndex(string Fingerprint, ReverseDependencyIndex Index);

    private sealed record ProjectSnapshot(string Path, string ContentHash);

    private sealed record SourceSnapshot(
        string Fingerprint,
        string RepositoryIdentity,
        IReadOnlyList<ProjectSnapshot> Projects,
        IReadOnlyList<SourceFileSnapshot> Sources,
        long SourceBytes);

    private sealed record SourceFileSnapshot(
        string Path,
        bool IsTargetTestProject,
        string Source,
        string ContentHash,
        FileVersion Version);

    private readonly record struct FileVersion(long Length, long LastWriteTimeUtcTicks);

    private sealed record FrontierSymbol(string Name, TestSelectionMode TestSelectionMode);

    private enum TestSelectionMode
    {
        AllReferencingClasses,
        OwnedClassOnly
    }
}
