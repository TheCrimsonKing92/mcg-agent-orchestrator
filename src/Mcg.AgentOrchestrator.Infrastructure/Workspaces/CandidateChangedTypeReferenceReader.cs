using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Best-effort source evidence relating failing tests to candidate-changed types.</summary>
internal static class CandidateChangedTypeReferenceReader
{
    internal static IReadOnlySet<string> ReadDeclaredTypeNames(
        string? sourceRoot,
        IReadOnlyList<string> changedPaths)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                return names;
            }

            var root = Path.GetFullPath(sourceRoot);
            foreach (var changedPath in changedPaths)
            {
                var normalized = changedPath.Replace('\\', '/');
                if (normalized.StartsWith("./", StringComparison.Ordinal))
                {
                    normalized = normalized[2..];
                }

                if (!normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
                    !normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = ResolveContainedPath(root, normalized);
                if (path is null || !File.Exists(path) ||
                    !Path.GetRelativePath(root, path).Replace('\\', '/')
                        .StartsWith("src/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetCompilationUnitRoot();
                foreach (var declaration in syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    names.Add(declaration.Identifier.ValueText);
                }
            }

            return names;
        }
        catch (Exception ex) when (IsReadingFailure(ex))
        {
            // Optional source evidence must not change the gate outcome on a reading failure.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    internal static IReadOnlyList<string> MatchReferencedTypes(
        string? sourceRoot,
        IReadOnlyList<string> testSourcePaths,
        IReadOnlySet<string> declaredTypes)
    {
        try
        {
            if (declaredTypes.Count == 0 || string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                return [];
            }

            var root = Path.GetFullPath(sourceRoot);
            var matches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var testSourcePath in testSourcePaths)
            {
                var path = ResolveContainedPath(root, testSourcePath);
                if (path is null || !File.Exists(path))
                {
                    continue;
                }

                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetCompilationUnitRoot();
                foreach (var token in syntax.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken) && declaredTypes.Contains(token.ValueText))
                    {
                        matches.Add(token.ValueText);
                    }
                }
            }

            return matches.Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (IsReadingFailure(ex))
        {
            return [];
        }
    }

    private static string? ResolveContainedPath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool IsReadingFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
