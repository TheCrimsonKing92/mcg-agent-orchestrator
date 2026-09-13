using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Resolves a fully-qualified failing test identity to the repository-relative source file(s) that
/// declare its test class. This is the candidate/apparatus boundary input: a failing test whose
/// source the candidate never touched cannot have been broken by the candidate.
///
/// Deliberately a fresh collaborator rather than a widened <see cref="GoalAcceptanceVerifier"/>
/// member, so the verifier's source-discovery surface is unchanged.
/// </summary>
internal static class AcceptanceTestSourceResolver
{
    internal static IReadOnlyList<string> ResolveSourcePaths(
        string? rootPath,
        string? testProjectPath,
        string? testIdentity)
    {
        if (string.IsNullOrWhiteSpace(rootPath) ||
            string.IsNullOrWhiteSpace(testIdentity) ||
            !Directory.Exists(rootPath))
        {
            return [];
        }

        var className = ExtractClassName(testIdentity);
        if (string.IsNullOrWhiteSpace(className))
        {
            return [];
        }

        try
        {
            return ResolveCore(Path.GetFullPath(rootPath), testProjectPath, className);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unresolvable identity must read as "unknown", which the classifier treats as genuine.
            return [];
        }
    }

    /// <summary>
    /// The class portion of <c>Namespace.Class.Method</c>. Generic arity and xunit theory argument
    /// suffixes are stripped before splitting.
    /// </summary>
    internal static string? ExtractClassName(string testIdentity)
    {
        var normalized = testIdentity.Trim();
        var argumentStart = normalized.IndexOf('(');
        if (argumentStart >= 0)
        {
            normalized = normalized[..argumentStart];
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return null;
        }

        var className = segments[^2];
        var genericStart = className.IndexOf('<');
        return genericStart >= 0 ? className[..genericStart] : className;
    }

    private static IReadOnlyList<string> ResolveCore(string rootPath, string? testProjectPath, string className)
    {
        var searchRoots = ResolveSearchRoots(rootPath, testProjectPath);
        var matches = new List<string>();
        foreach (var searchRoot in searchRoots)
        {
            foreach (var path in Directory.EnumerateFiles(searchRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (HasGeneratedPathSegment(path))
                {
                    continue;
                }

                string source;
                try
                {
                    source = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!source.Contains(className, StringComparison.Ordinal))
                {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(source, path: path).GetCompilationUnitRoot();
                if (root.DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Any(declaration => declaration.Identifier.ValueText.Equals(className, StringComparison.Ordinal)))
                {
                    matches.Add(ToRepositoryRelativePath(rootPath, path));
                }
            }
        }

        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> ResolveSearchRoots(string rootPath, string? testProjectPath)
    {
        if (!string.IsNullOrWhiteSpace(testProjectPath))
        {
            var projectPath = Path.GetFullPath(Path.Combine(
                rootPath,
                testProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrWhiteSpace(projectDirectory) &&
                IsWithinDirectory(projectDirectory, rootPath) &&
                Directory.Exists(projectDirectory))
            {
                return [projectDirectory];
            }
        }

        // Without a declared test project, search the conventional source trees rather than the whole
        // worktree, so the scan never walks .git or orchestrator state.
        return new[] { "tests", "src" }
            .Select(segment => Path.Combine(rootPath, segment))
            .Where(Directory.Exists)
            .ToArray();
    }

    private static string ToRepositoryRelativePath(string rootPath, string path) =>
        Path.GetRelativePath(rootPath, path).Replace('\\', '/');

    private static bool HasGeneratedPathSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
