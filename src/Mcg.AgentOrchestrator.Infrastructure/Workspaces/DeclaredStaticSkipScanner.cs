using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Source declarations are proof only for unconditional literal skips on test methods.
internal static class DeclaredStaticSkipScanner
{
    internal static IReadOnlySet<string> Scan(string? projectDirectory)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory)) return declared;
        try
        {
            foreach (var path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !Path.GetRelativePath(projectDirectory, path)
                             .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                          part.Equals("obj", StringComparison.OrdinalIgnoreCase))))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("Skip", StringComparison.Ordinal)) declared.UnionWith(DeclaredIn(text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { declared.Clear(); }
        return declared;
    }

    internal static IReadOnlySet<string> DeclaredIn(string source)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error)) return declared;
        foreach (var method in syntax.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var testAttributes = method.AttributeLists.SelectMany(list => list.Attributes)
                .Where(attribute => attribute.Name.ToString().Split('.').Last() is
                    "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute").ToArray();
            if (testAttributes.Length != 1 || testAttributes[0].ArgumentList is not { } arguments) continue;
            if (arguments.Arguments.Any(argument => argument.NameEquals?.Name.Identifier.ValueText is
                    "SkipUnless" or "SkipWhen")) continue;
            if (!arguments.Arguments.Any(argument => argument.NameEquals?.Name.Identifier.ValueText == "Skip" &&
                    argument.Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))) continue;
            var type = method.Parent as ClassDeclarationSyntax;
            if (type is null) continue;
            var namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse().Select(item => item.Name.ToString());
            var containingTypes = type.Ancestors().OfType<ClassDeclarationSyntax>()
                .Reverse().Select(item => item.Identifier.ValueText);
            var className = string.Join(".", namespaces.Append(
                string.Join("+", containingTypes.Append(type.Identifier.ValueText))));
            declared.Add($"{className}.{method.Identifier.ValueText}");
        }
        return declared;
    }
}
