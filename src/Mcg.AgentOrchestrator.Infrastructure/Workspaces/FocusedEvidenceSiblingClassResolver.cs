using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceSiblingClassResolver
{
    internal static IReadOnlyList<string> ResolveSiblingTestClassNames(
        string worktreePath,
        string project,
        string requestedClass)
    {
        try
        {
            return ResolveSiblingTestClassNamesCore(worktreePath, project, requestedClass);
        }
        catch
        {
            // Sibling expansion is best-effort and may only widen the original selection.
            return [];
        }
    }

    private static IReadOnlyList<string> ResolveSiblingTestClassNamesCore(
        string worktreePath,
        string project,
        string requestedClass)
    {
        var normalizedRequestedClass = requestedClass.Trim();
        if (normalizedRequestedClass.Length == 0)
        {
            return [];
        }

        var rootPath = Path.GetFullPath(worktreePath);
        var projectPath = Path.GetFullPath(Path.Combine(
            rootPath,
            project.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinDirectory(projectPath, rootPath) || !File.Exists(projectPath))
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            return [];
        }

        var nestedProjectDirectories = Directory
            .EnumerateFiles(projectDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                !path.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedSimpleName = normalizedRequestedClass.Split('.').Last();
        var declaringFiles = new List<CompilationUnitSyntax>();

        // Deliberately mirrors GoalAcceptanceVerifier's source-discovery mechanism without
        // changing that verifier: resolve one declaring file, then inspect only that file.
        foreach (var path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (HasGeneratedPathSegment(path) ||
                nestedProjectDirectories.Any(directory => IsWithinDirectory(path, directory)))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            if (!source.Contains(requestedSimpleName, StringComparison.Ordinal))
            {
                continue;
            }

            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            if (syntaxTree.GetDiagnostics().Any(diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                return [];
            }

            var root = syntaxTree.GetCompilationUnitRoot();
            if (root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Any(declaration => IsTopLevel(declaration) && TypeMatches(declaration, normalizedRequestedClass)))
            {
                declaringFiles.Add(root);
            }
        }

        if (declaringFiles.Count != 1)
        {
            return [];
        }

        return declaringFiles[0]
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(IsQualifyingTestClass)
            .Where(declaration => !TypeMatches(declaration, normalizedRequestedClass))
            .Select(declaration => declaration.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsQualifyingTestClass(TypeDeclarationSyntax declaration)
    {
        if (!IsTopLevel(declaration) ||
            declaration.Modifiers.All(modifier => modifier.RawKind != (int)SyntaxKind.PublicKeyword) ||
            declaration.Modifiers.Any(modifier =>
                modifier.RawKind == (int)SyntaxKind.AbstractKeyword ||
                modifier.RawKind == (int)SyntaxKind.StaticKeyword) ||
            declaration is not ClassDeclarationSyntax &&
            declaration is not RecordDeclarationSyntax)
        {
            return false;
        }

        if (declaration is RecordDeclarationSyntax record &&
            record.ClassOrStructKeyword.RawKind == (int)SyntaxKind.StructKeyword)
        {
            return false;
        }

        return declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(method => method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => IsTestAttribute(attribute.Name.ToString())));
    }

    private static bool IsTestAttribute(string attributeName)
    {
        var simpleName = attributeName.Split('.').Last().Split("::").Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            simpleName = simpleName[..^"Attribute".Length];
        }

        return simpleName.EndsWith("Fact", StringComparison.Ordinal) ||
            simpleName.EndsWith("Theory", StringComparison.Ordinal);
    }

    private static bool TypeMatches(TypeDeclarationSyntax declaration, string requestedClass)
    {
        if (declaration.Identifier.ValueText.Equals(requestedClass, StringComparison.Ordinal))
        {
            return true;
        }

        var namespaceNames = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString());
        var qualifiedName = string.Join('.', namespaceNames.Append(declaration.Identifier.ValueText));
        return qualifiedName.Equals(requestedClass, StringComparison.Ordinal) ||
            qualifiedName.EndsWith('.' + requestedClass, StringComparison.Ordinal);
    }

    private static bool IsTopLevel(TypeDeclarationSyntax declaration) =>
        !declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any();

    private static bool HasGeneratedPathSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(segment =>
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
