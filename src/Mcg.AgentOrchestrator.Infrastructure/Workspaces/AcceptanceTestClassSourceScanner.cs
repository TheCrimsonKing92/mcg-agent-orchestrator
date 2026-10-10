using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceTestClassDescriptor(string FullName, string? Collection);
internal sealed record AcceptanceTestClassSource(string FullName, IReadOnlyList<string> SourcePaths, string SourceText);

internal static class AcceptanceTestClassSourceScanner
{
    // Preserve Scan's membership contract while exposing declaring and ancestor files for observation.
    internal static IReadOnlyList<AcceptanceTestClassSource> ScanSources(string worktreePath)
    {
        var classes = Scan(worktreePath);
        var declarations = new Dictionary<string, List<(string Path, string Text, string? BaseName)>>(StringComparer.Ordinal);
        foreach (var project in new[] { "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.TestSupport" })
        {
            var root = Path.Combine(worktreePath, "tests", project);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             .Any(part => part is "bin" or "obj")))
            {
                var text = File.ReadAllText(path);
                var syntax = CSharpSyntaxTree.ParseText(text).GetRoot();
                if (syntax.ContainsDiagnostics && syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                    throw new InvalidDataException($"Cannot parse test source '{path}'.");
                foreach (var node in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var name = NameOf(node);
                    if (!declarations.TryGetValue(name, out var parts)) declarations[name] = parts = [];
                    var baseType = node.BaseList?.Types.FirstOrDefault()?.Type;
                    var baseName = BaseNameOf(baseType);
                    parts.Add((Path.GetRelativePath(worktreePath, path).Replace('\\', '/'), text, baseName));
                }
            }
        }
        var byShortName = declarations.Keys.GroupBy(name => name.Split('.', '+').Last(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return classes.Select(item =>
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Include(string name)
            {
                if (!visited.Add(name) || !declarations.TryGetValue(name, out var parts)) return;
                foreach (var part in parts)
                {
                    files[part.Path] = part.Text;
                    if (part.BaseName is not null && byShortName.TryGetValue(part.BaseName, out var bases))
                        foreach (var parent in bases) Include(parent);
                }
            }
            Include(item.FullName);
            return new AcceptanceTestClassSource(item.FullName, files.Keys.ToArray(), string.Join("\n", files.Values));
        }).ToArray();
    }

    private static string? BaseNameOf(TypeSyntax? type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => BaseNameOf(qualified.Right),
        AliasQualifiedNameSyntax alias => BaseNameOf(alias.Name),
        _ => null
    };

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> Scan(string worktreePath)
    {
        var testRoot = Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        if (!Directory.Exists(testRoot))
        {
            AcceptanceTestClassSourceCache.Current.Forget(worktreePath);
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
        var extracts = AcceptanceTestClassSourceCache.Current.Extract(worktreePath, files, ExtractFile);
        var constants = extracts.SelectMany(extract => extract.Constants)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var classes = extracts.SelectMany(extract => extract.Classes)
            .Select(item => new SourceClass(item.FullName, item.ShortName, item.Abstract, item.BaseName,
                item.HasFacts, ResolveCollection(item.FullName, item.Collection, constants)))
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

    private static FileExtract ExtractFile(string path)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        var constants = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Parent is ClassDeclarationSyntax)
            .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword) &&
                            field.Declaration.Type.ToString() == "string")
            .SelectMany(field => field.Declaration.Variables.Select(variable => new
            {
                Owner = ((ClassDeclarationSyntax)field.Parent!).Identifier.ValueText,
                Variable = variable
            }))
            .Where(item => item.Variable.Initializer?.Value is LiteralExpressionSyntax)
            .Select(item => new ConstantExtract($"{item.Owner}.{item.Variable.Identifier.ValueText}",
                ((LiteralExpressionSyntax)item.Variable.Initializer!.Value).Token.ValueText))
            .ToArray();

        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(node => new ClassExtract(
                NameOf(node),
                node.Identifier.ValueText,
                node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                node.BaseList?.Types.FirstOrDefault()?.Type.ToString().Split('.').Last(),
                HasFacts(node),
                ExtractCollection(node)))
            .ToArray();
        return new FileExtract(constants, classes);
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

    private static CollectionExpression ExtractCollection(ClassDeclarationSyntax node)
    {
        var attribute = node.AttributeLists.SelectMany(list => list.Attributes)
            .FirstOrDefault(item => item.Name.ToString().Split('.').Last() is "Collection" or "CollectionAttribute");
        if (attribute is null)
        {
            return new(CollectionExpressionKind.None, null);
        }
        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
                new(CollectionExpressionKind.Literal, literal.Token.ValueText),
            InvocationExpressionSyntax invocation when invocation.Expression.ToString() == "nameof" =>
                new(CollectionExpressionKind.NameOf,
                    invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Split('.').Last()),
            MemberAccessExpressionSyntax member => new(CollectionExpressionKind.Member, member.ToString()),
            IdentifierNameSyntax identifier => new(CollectionExpressionKind.Identifier, identifier.Identifier.ValueText),
            _ => new(CollectionExpressionKind.Unresolvable, null)
        };
    }

    private static string? ResolveCollection(string fullName, CollectionExpression expression,
        IReadOnlyDictionary<string, string[]> constants)
    {
        if (expression.Kind == CollectionExpressionKind.None) return null;
        var value = expression.Kind switch
        {
            CollectionExpressionKind.Literal or CollectionExpressionKind.NameOf => expression.Value,
            CollectionExpressionKind.Member when constants.TryGetValue(expression.Value!, out var resolved) &&
                                                 resolved.Length == 1 => resolved[0],
            CollectionExpressionKind.Identifier => ResolveUnqualifiedConstant(expression.Value!, constants),
            _ => null
        };
        if (value is null && expression.Kind switch
            {
                CollectionExpressionKind.Member => constants.TryGetValue(expression.Value!, out var values) &&
                                                   values.Length > 1,
                CollectionExpressionKind.Identifier => constants.Any(item =>
                    item.Key.EndsWith($".{expression.Value}", StringComparison.Ordinal) &&
                    item.Value.Length > 1),
                _ => false
            }) return null;
        return value ?? throw new InvalidDataException($"Cannot resolve test collection on '{fullName}'.");
    }

    private static string? ResolveUnqualifiedConstant(string name, IReadOnlyDictionary<string, string[]> constants)
    {
        var matches = constants.Where(item => item.Key.EndsWith($".{name}", StringComparison.Ordinal)).ToArray();
        if (matches.Any(item => item.Value.Length != 1)) return null;
        var values = matches.Select(item => item.Value[0]).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1 ? values[0] : null;
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

    // Only file-local syntax facts are cached; resolution against other files happens on every scan.
    internal sealed record FileExtract(IReadOnlyList<ConstantExtract> Constants, IReadOnlyList<ClassExtract> Classes);
    internal sealed record ConstantExtract(string Key, string Value);
    internal sealed record ClassExtract(string FullName, string ShortName, bool Abstract, string? BaseName,
        bool HasFacts, CollectionExpression Collection);
    internal sealed record CollectionExpression(CollectionExpressionKind Kind, string? Value);
    internal enum CollectionExpressionKind { None, Literal, NameOf, Member, Identifier, Unresolvable }
}
