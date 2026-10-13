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
        string? testIdentity) => new Request(rootPath).ResolveSourcePaths(testProjectPath, testIdentity);

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

    /// <summary>One stable completion's source snapshot; never retained across candidates or requests.</summary>
    internal sealed class Request
    {
        private readonly string? _rootPath;
        private readonly Func<string, IEnumerable<string>> _enumerate;
        private readonly Func<string, string> _read;
        private readonly Func<string, bool> _directoryExists;
        private readonly Dictionary<string, Dictionary<string, string[]>?> _indexes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CompilationUnitSyntax?> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlySet<string>> _identifiers = new(StringComparer.OrdinalIgnoreCase);

        internal Request(string? rootPath,
            Func<string, IEnumerable<string>>? enumerate = null,
            Func<string, string>? read = null,
            Func<string, bool>? directoryExists = null)
        {
            _enumerate = enumerate ?? (path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories));
            _read = read ?? File.ReadAllText;
            _directoryExists = directoryExists ?? Directory.Exists;
            try
            {
                if (!string.IsNullOrWhiteSpace(rootPath) && _directoryExists(rootPath))
                {
                    _rootPath = Path.GetFullPath(rootPath);
                }
            }
            catch (Exception ex) when (IsResolutionFailure(ex)) { }
        }

        internal int EnumerationCount { get; private set; }
        internal int ReadCount { get; private set; }

        internal IReadOnlyList<string> ResolveSourcePaths(string? testProjectPath, string? testIdentity)
        {
            if (_rootPath is null || string.IsNullOrWhiteSpace(testIdentity)) return [];
            var className = ExtractClassName(testIdentity);
            if (string.IsNullOrWhiteSpace(className)) return [];
            try
            {
                var matches = new List<string>();
                foreach (var searchRoot in ResolveSearchRoots(_rootPath, testProjectPath, _directoryExists))
                {
                    var index = GetIndex(searchRoot);
                    // A late enumeration fault invalidates the whole scope, including earlier roots.
                    if (index is null) return [];
                    if (index.TryGetValue(className, out var paths)) matches.AddRange(paths);
                }
                return matches.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (IsResolutionFailure(ex)) { return []; }
        }

        internal CompilationUnitSyntax? ReadSyntax(string relativePath)
        {
            if (_rootPath is null) return null;
            var path = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return IsWithinDirectory(path, _rootPath) && !HasGeneratedPathSegment(path)
                ? ReadSource(path) : null;
        }

        internal IReadOnlySet<string> ReadIdentifiers(string relativePath)
        {
            if (_identifiers.TryGetValue(relativePath, out var cached)) return cached;
            var syntax = ReadSyntax(relativePath);
            var identifiers = syntax?.DescendantTokens().Where(token => token.RawKind == (int)SyntaxKind.IdentifierToken)
                .Select(token => token.ValueText).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            _identifiers.Add(relativePath, identifiers);
            return identifiers;
        }

        private CompilationUnitSyntax? ReadSource(string path)
        {
            if (_sources.TryGetValue(path, out var cached)) return cached;
            CompilationUnitSyntax? syntax = null;
            try
            {
                ReadCount++;
                var source = _read(path);
                syntax = CSharpSyntaxTree.ParseText(source, path: path).GetCompilationUnitRoot();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _sources.Add(path, syntax);
            return syntax;
        }

        private Dictionary<string, string[]>? GetIndex(string searchRoot)
        {
            if (_indexes.TryGetValue(searchRoot, out var cached)) return cached;
            try
            {
                var matches = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                EnumerationCount++;
                foreach (var discoveredPath in _enumerate(searchRoot))
                {
                    var path = Path.GetFullPath(discoveredPath);
                    if (!IsWithinDirectory(path, searchRoot) || HasGeneratedPathSegment(path)) continue;
                    var syntax = ReadSource(path);
                    if (syntax is null) continue;
                    var source = syntax.ToFullString();
                    foreach (var name in syntax.DescendantNodes().OfType<TypeDeclarationSyntax>()
                        .Select(declaration => declaration.Identifier.ValueText).Distinct(StringComparer.Ordinal))
                    {
                        // Preserve the original literal text prefilter (notably escaped identifiers).
                        if (!source.Contains(name, StringComparison.Ordinal)) continue;
                        if (!matches.TryGetValue(name, out var paths)) matches[name] = paths = [];
                        paths.Add(ToRepositoryRelativePath(_rootPath!, path));
                    }
                }
                var index = matches.ToDictionary(pair => pair.Key,
                    pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    StringComparer.Ordinal);
                _indexes.Add(searchRoot, index);
                return index;
            }
            catch (Exception ex) when (IsResolutionFailure(ex))
            {
                _indexes.Add(searchRoot, null);
                return null;
            }
        }
    }

    private static bool IsResolutionFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException;

    private static IReadOnlyList<string> ResolveSearchRoots(string rootPath, string? testProjectPath,
        Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrWhiteSpace(testProjectPath))
        {
            var projectPath = Path.GetFullPath(Path.Combine(
                rootPath,
                testProjectPath.Replace('/', Path.DirectorySeparatorChar)));
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrWhiteSpace(projectDirectory) &&
                IsWithinDirectory(projectDirectory, rootPath) &&
                directoryExists(projectDirectory))
            {
                return [projectDirectory];
            }
        }

        // Without a declared test project, search the conventional source trees rather than the whole
        // worktree, so the scan never walks .git or orchestrator state.
        return new[] { "tests", "src" }
            .Select(segment => Path.Combine(rootPath, segment))
            .Where(directoryExists)
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
