using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns conservative pruning of additional siblings, never the explicitly requested classes.
internal sealed class FocusedEvidenceSiblingExpansionBound
{
    private const int WithinBoundLimit = 16;
    internal static FocusedEvidenceSiblingExpansionBound Shared { get; } = new(32);

    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly LinkedList<string> _recency = new();
    private readonly Dictionary<string, (string[] Names, bool HasAbstractClass,
        LinkedListNode<string> Recency)> _memo = new(StringComparer.Ordinal);

    internal FocusedEvidenceSiblingExpansionBound(int memoCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoCapacity);
        _capacity = memoCapacity;
    }

    internal (IReadOnlyList<string> KeptSiblings, string Reason) Decide(
        string worktreePath,
        string project,
        IReadOnlyList<string> resolvedSiblings,
        IReadOnlyList<string>? changedPaths)
    {
        if (resolvedSiblings.Count <= WithinBoundLimit)
            return (resolvedSiblings, "within-bound");
        if (changedPaths is not { Count: > 0 })
            return (resolvedSiblings, "changed-paths-unavailable");

        var facts = ReadChangedFacts(worktreePath, project, changedPaths);
        return facts is { } readable
            ? ApplyFacts(resolvedSiblings, readable.Names, readable.HasAbstractClass)
            : (resolvedSiblings, "changed-source-unreadable");
    }

    internal (IReadOnlyList<string> KeptSiblings, string Reason) Resolve(
        string worktreePath,
        string project,
        string requestedClass,
        Func<string, IReadOnlyList<string>> changedPathsProvider)
    {
        var siblings = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            worktreePath, project, requestedClass);
        if (siblings.Count <= WithinBoundLimit)
            return (siblings, "within-bound");

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
            var projectPath = Path.GetFullPath(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar)));
            var fingerprint = ComputeFingerprint(GetProjectDirectory(root, projectPath));
            var key = root.ToUpperInvariant() + "\n" + projectPath.ToUpperInvariant() + "\n" + fingerprint;
            // Population under the same lock coalesces equivalent concurrent requests.
            // Entries retain only declaration names, the abstract flag and LRU bookkeeping.
            lock (_sync)
            {
                if (_memo.TryGetValue(key, out var entry))
                {
                    _recency.Remove(entry.Recency);
                    _recency.AddFirst(entry.Recency);
                    return ApplyFacts(siblings, entry.Names, entry.HasAbstractClass);
                }

                IReadOnlyList<string>? changedPaths;
                try { changedPaths = changedPathsProvider(root); }
                catch { changedPaths = null; }
                if (changedPaths is not { Count: > 0 })
                    return (siblings, "changed-paths-unavailable");

                var facts = ReadChangedFacts(root, project, changedPaths);
                if (facts is not { } readable)
                    return (siblings, "changed-source-unreadable");

                _memo.Add(key, (readable.Names, readable.HasAbstractClass, _recency.AddFirst(key)));
                if (_memo.Count > _capacity)
                {
                    _memo.Remove(_recency.Last!.Value);
                    _recency.RemoveLast();
                }
                return ApplyFacts(siblings, readable.Names, readable.HasAbstractClass);
            }
        }
        catch
        {
            return (siblings, "changed-source-unreadable");
        }
    }

    private static (IReadOnlyList<string> KeptSiblings, string Reason) ApplyFacts(
        IReadOnlyList<string> siblings, string[] declaredNames, bool hasAbstractClass)
    {
        if (hasAbstractClass)
            return (siblings, "abstract-layer-changed");

        var names = new HashSet<string>(declaredNames, StringComparer.Ordinal);
        var kept = siblings.Where(names.Contains).ToArray();
        return (kept, $"bounded family={siblings.Count} kept={kept.Length}");
    }

    private static (string[] Names, bool HasAbstractClass)? ReadChangedFacts(
        string worktreePath, string project, IReadOnlyList<string> changedPaths)
    {
        try
        {
            var root = Path.GetFullPath(worktreePath);
            var projectPath = Path.GetFullPath(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar)));
            var directory = GetProjectDirectory(root, projectPath);
            var nestedDirectories = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(path => path is not null && !path.Equals(directory, StringComparison.OrdinalIgnoreCase))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var hasAbstractClass = false;
            foreach (var changedPath in changedPaths)
            {
                if (!changedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.GetFullPath(Path.Combine(root, changedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithinDirectory(path, directory) || HasGeneratedPathSegment(path) ||
                    nestedDirectories.Any(nested => IsWithinDirectory(path, nested))) continue;

                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
                if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    return null;

                foreach (var declaration in tree.GetCompilationUnitRoot().DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(type => !type.Ancestors().OfType<TypeDeclarationSyntax>().Any()))
                {
                    names.Add(declaration.Identifier.ValueText);
                    hasAbstractClass |= declaration is ClassDeclarationSyntax &&
                        declaration.Modifiers.Any(SyntaxKind.AbstractKeyword);
                }
            }
            // Validate every relevant source before allowing an abstract-layer decision.
            return (names.ToArray(), hasAbstractClass);
        }
        catch
        {
            return null;
        }
    }

    private static string GetProjectDirectory(string root, string projectPath)
    {
        if (!IsWithinDirectory(projectPath, root) || !File.Exists(projectPath))
            throw new IOException("Focused evidence project is unavailable.");
        return Path.GetDirectoryName(projectPath)!;
    }

    private static string ComputeFingerprint(string directory)
    {
        var facts = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasGeneratedPathSegment(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var file = new FileInfo(path);
                return Path.GetRelativePath(directory, path).Replace('\\', '/') + "|" +
                    file.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                    file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', facts))));
    }

    private static bool HasGeneratedPathSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsWithinDirectory(string path, string directory) =>
        path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
