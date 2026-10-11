using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Indexes literal xUnit DisplayNames by declaring type and method in main's known sources.</summary>
internal sealed class MainTestDisplayNameResolver
{
    private readonly Dictionary<(string Class, string Method), HashSet<string>> _names = new();

    internal MainTestDisplayNameResolver(string? repositoryRoot, IEnumerable<string> sourceFiles)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            return;
        }

        foreach (var sourceFile in sourceFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = Path.IsPathRooted(sourceFile) ? sourceFile : Path.Combine(repositoryRoot, sourceFile);
                if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
                if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    continue;
                }

                foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var className = string.Join(".", type.AncestorsAndSelf().Reverse().Select(node => node switch
                    {
                        BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                        TypeDeclarationSyntax containingType => containingType.Identifier.ValueText,
                        _ => null
                    }).Where(name => name is not null));
                    foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                    {
                        foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
                        {
                            var attributeName = attribute.Name.ToString().Split('.').Last().Split("::").Last();
                            if (attributeName is not ("Fact" or "Theory" or "FactAttribute" or "TheoryAttribute"))
                            {
                                continue;
                            }

                            foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
                            {
                                if (argument.NameEquals?.Name.Identifier.ValueText != "DisplayName" ||
                                    argument.Expression is not LiteralExpressionSyntax literal ||
                                    !literal.IsKind(SyntaxKind.StringLiteralExpression))
                                {
                                    continue;
                                }

                                var key = (className, method.Identifier.ValueText);
                                if (!_names.TryGetValue(key, out var names))
                                {
                                    _names[key] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                }
                                names.Add(literal.Token.ValueText);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                // Unavailable source cannot corroborate a removal; retain the previous coverage floor.
            }
        }
    }

    internal IReadOnlyList<string> Resolve(string declaredIdentity)
    {
        var separator = declaredIdentity.LastIndexOf('.');
        if (separator <= 0 || separator == declaredIdentity.Length - 1)
        {
            return [];
        }

        var declaredClass = declaredIdentity[..separator];
        var method = declaredIdentity[(separator + 1)..];
        return _names.Where(entry =>
                entry.Key.Method.Equals(method, StringComparison.OrdinalIgnoreCase) &&
                (entry.Key.Class.Equals(declaredClass, StringComparison.OrdinalIgnoreCase) ||
                    entry.Key.Class.EndsWith("." + declaredClass, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(entry => entry.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveAll(
        string? repositoryRoot, IEnumerable<string> sourceFiles, IReadOnlyList<string> declaredIdentities)
    {
        var resolved = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (declaredIdentities.Count == 0)
        {
            return resolved;
        }

        var resolver = new MainTestDisplayNameResolver(repositoryRoot, sourceFiles);
        foreach (var identity in declaredIdentities.Where(identity => !string.IsNullOrWhiteSpace(identity)))
        {
            resolved[identity] = resolver.Resolve(identity);
        }
        return resolved;
    }
}
