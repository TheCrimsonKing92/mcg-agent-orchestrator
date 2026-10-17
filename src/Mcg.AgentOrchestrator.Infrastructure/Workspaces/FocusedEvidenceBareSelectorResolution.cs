using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceBareSelectorResolution
{
    internal static bool TryResolve(
        Func<IEnumerable<string>> discoverSourceFiles,
        string project,
        string originalToken,
        string value,
        out FocusedEvidenceFilterToken selection,
        out FocusedEvidenceRejection rejection)
    {
        selection = new FocusedEvidenceFilterToken(
            originalToken, $"FullyQualifiedName~{value}", FocusedEvidenceTokenKind.Class, value, value);
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            originalToken,
            $"focused evidence selection '{originalToken}' could not be resolved");
        var owners = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var sourceFiles = discoverSourceFiles().ToArray();
            if (sourceFiles.Length == 0)
            {
                return true;
            }

            foreach (var path in sourceFiles)
            {
                var source = File.ReadAllText(path);
                if (!source.Contains(value, StringComparison.Ordinal))
                {
                    continue;
                }

                var declarations = CSharpSyntaxTree.ParseText(source, path: path)
                    .GetCompilationUnitRoot().DescendantNodes().OfType<TypeDeclarationSyntax>();
                foreach (var declaration in declarations)
                {
                    var simpleName = declaration.Identifier.ValueText;
                    if (simpleName.Contains(value, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (!declaration.Members.OfType<MethodDeclarationSyntax>().Any(method =>
                            method.Identifier.ValueText.Equals(value, StringComparison.Ordinal) &&
                            method.AttributeLists.SelectMany(list => list.Attributes)
                                .Any(attribute => FocusedEvidenceRequestResolver.IsFocusedEvidenceTestAttribute(attribute.Name.ToString()))))
                    {
                        continue;
                    }

                    var namespaceNames = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse().Select(item => item.Name.ToString());
                    var containingTypeNames = declaration.Ancestors().OfType<TypeDeclarationSyntax>()
                        .Reverse().Select(item => item.Identifier.ValueText);
                    var qualifiedName = string.Join('.', namespaceNames.Concat(containingTypeNames).Append(simpleName));
                    owners[qualifiedName] = simpleName;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the historical class fallback when source discovery cannot establish ownership.
            return true;
        }

        if (owners.Count == 1)
        {
            var owner = owners.Single().Value;
            var method = $"{owner}.{value}";
            selection = new FocusedEvidenceFilterToken(
                originalToken, $"FullyQualifiedName~{method}", FocusedEvidenceTokenKind.Method, method, owner);
            return true;
        }

        selection = null!;
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            originalToken,
            owners.Count == 0
                ? $"focused evidence selection '{originalToken}' does not resolve to a class or method in {GoalAcceptanceVerifier.ProjectLabel(project)}"
                : $"focused evidence selection '{originalToken}' is ambiguous in {GoalAcceptanceVerifier.ProjectLabel(project)}; candidates: {string.Join(", ", owners.Keys)}");
        return false;
    }
}
