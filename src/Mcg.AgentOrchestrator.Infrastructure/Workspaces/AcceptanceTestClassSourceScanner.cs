using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceTestClassDescriptor(string FullName, string? Collection);

internal static class AcceptanceTestClassSourceScanner
{
    internal static IReadOnlyList<AcceptanceTestClassDescriptor> Scan(string worktreePath)
    {
        var testRoot = Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        if (!Directory.Exists(testRoot))
        {
            return [];
        }

        var supportRoot = Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.TestSupport");
        var files = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.Exists(supportRoot)
                ? Directory.EnumerateFiles(supportRoot, "*.cs", SearchOption.AllDirectories)
                : [])
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var roots = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()).ToArray();
        var constants = roots.SelectMany(root => root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            .Where(field => field.Parent is ClassDeclarationSyntax { Identifier.ValueText: "TestCollections" })
            .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword) &&
                            field.Declaration.Type.ToString() == "string")
            .SelectMany(field => field.Declaration.Variables)
            .Where(variable => variable.Initializer?.Value is LiteralExpressionSyntax)
            .ToDictionary(variable => variable.Identifier.ValueText,
                variable => ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText,
                StringComparer.Ordinal);

        var classes = roots.SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Select(node => new SourceClass(
                NameOf(node),
                node.Identifier.ValueText,
                node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                node.BaseList?.Types.FirstOrDefault()?.Type.ToString().Split('.').Last(),
                HasFacts(node),
                CollectionOf(node, constants)))
            .GroupBy(item => item.FullName, StringComparer.Ordinal)
            .Select(group => group.Aggregate((left, right) => left with
            {
                Abstract = left.Abstract || right.Abstract,
                HasFacts = left.HasFacts || right.HasFacts,
                Collection = left.Collection ?? right.Collection,
                BaseName = left.BaseName ?? right.BaseName
            }))
            .ToArray();
        var byName = classes.GroupBy(item => item.ShortName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return classes.Where(item => !item.Abstract && HasRunnableFacts(item, byName, []))
            .Select(item => new AcceptanceTestClassDescriptor(item.FullName, InheritedCollection(item, byName, [])))
            .OrderBy(item => item.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NameOf(ClassDeclarationSyntax node)
    {
        var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(item => item.Name.ToString());
        var containing = node.Ancestors().OfType<ClassDeclarationSyntax>()
            .Reverse().Select(item => item.Identifier.ValueText);
        var typeName = string.Join("+", containing.Append(node.Identifier.ValueText));
        return string.Join(".", namespaces.Append(typeName));
    }

    private static bool HasFacts(ClassDeclarationSyntax node) =>
        node.Members.OfType<MethodDeclarationSyntax>().Any(method =>
            method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                attribute.Name.ToString().Split('.').Last() is "Fact" or "FactAttribute" or
                    "Theory" or "TheoryAttribute"));

    private static string? CollectionOf(ClassDeclarationSyntax node, IReadOnlyDictionary<string, string> constants)
    {
        var attribute = node.AttributeLists.SelectMany(list => list.Attributes)
            .FirstOrDefault(item => item.Name.ToString().Split('.').Last() is "Collection" or "CollectionAttribute");
        if (attribute is null)
        {
            return null;
        }
        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        var value = expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            InvocationExpressionSyntax invocation when invocation.Expression.ToString() == "nameof" =>
                invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Split('.').Last(),
            MemberAccessExpressionSyntax member when constants.TryGetValue(member.Name.Identifier.ValueText, out var resolved) => resolved,
            IdentifierNameSyntax identifier when constants.TryGetValue(identifier.Identifier.ValueText, out var resolved) => resolved,
            _ => null
        };
        return value ?? throw new InvalidDataException($"Cannot resolve test collection on '{NameOf(node)}'.");
    }

    private static bool HasRunnableFacts(SourceClass item, IReadOnlyDictionary<string, SourceClass[]> byName,
        HashSet<string> visited)
    {
        if (!visited.Add(item.FullName)) return false;
        return item.HasFacts || item.BaseName is not null && byName.TryGetValue(item.BaseName, out var bases) &&
            bases.Any(parent => HasRunnableFacts(parent, byName, visited));
    }

    private static string? InheritedCollection(SourceClass item, IReadOnlyDictionary<string, SourceClass[]> byName,
        HashSet<string> visited)
    {
        if (item.Collection is not null || !visited.Add(item.FullName)) return item.Collection;
        return item.BaseName is not null && byName.TryGetValue(item.BaseName, out var bases)
            ? bases.Select(parent => InheritedCollection(parent, byName, visited)).FirstOrDefault(value => value is not null)
            : null;
    }

    private sealed record SourceClass(string FullName, string ShortName, bool Abstract, string? BaseName,
        bool HasFacts, string? Collection);
}
