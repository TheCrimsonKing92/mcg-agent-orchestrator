using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTestInventorySource
{
    internal static AcceptanceTestInventory Read(string worktreePath, string integrationBranch,
        Func<string, string[], string?> gitText)
    {
        var classes = AcceptanceTestClassSourceScanner.Scan(worktreePath);
        if (classes.Count == 0) throw new InvalidDataException("Runnable test inventory is empty.");
        var testRoot = Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        if (!Directory.Exists(testRoot)) throw new InvalidDataException("Test source tree is unavailable.");
        var supportRoot = Path.Combine(worktreePath, "tests", "Mcg.AgentOrchestrator.TestSupport");
        var candidateSources = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.Exists(supportRoot)
                ? Directory.EnumerateFiles(supportRoot, "*.cs", SearchOption.AllDirectories) : [])
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal).Select(File.ReadAllText).ToArray();
        var mainPaths = gitText(worktreePath,
            ["grep", "-l", "-e", "CollectionDefinition", "-e", "class .*TestCollections", integrationBranch, "--",
             "tests/Mcg.AgentOrchestrator.Infrastructure.Tests", "tests/Mcg.AgentOrchestrator.TestSupport"]);
        if (mainPaths is null) throw new InvalidDataException("Main collection metadata file list is unavailable.");
        var mainSources = mainPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.StartsWith(integrationBranch + ":", StringComparison.Ordinal) ? path[(integrationBranch.Length + 1)..] : path)
            .Order(StringComparer.Ordinal)
            .Select(path => gitText(worktreePath, ["show", $"{integrationBranch}:{path}"])
                ?? throw new InvalidDataException($"Main collection metadata is unavailable: {path}"))
            .ToArray();
        var candidate = ReadDefinitions(candidateSources);
        var main = ReadDefinitions(mainSources);
        var disabled = candidate.Disabled.Concat(main.Disabled).ToHashSet(StringComparer.Ordinal);
        return new AcceptanceTestInventory(classes, disabled, main.ProcessLocal);
    }

    private static (HashSet<string> Disabled, HashSet<string> ProcessLocal) ReadDefinitions(IEnumerable<string> sources)
    {
        var roots = sources.Select(source => CSharpSyntaxTree.ParseText(source).GetRoot()).ToArray();
        if (roots.Any(root => root.ContainsDiagnostics))
            throw new InvalidDataException("Test collection source contains a parse error.");
        var constants = roots.SelectMany(root => root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword) && field.Declaration.Type.ToString() == "string")
            .SelectMany(field => field.Declaration.Variables.Select(variable => new
            {
                Owner = (field.Parent as ClassDeclarationSyntax)?.Identifier.ValueText,
                Variable = variable
            }))
            .Where(item => item.Owner is not null && item.Variable.Initializer?.Value is LiteralExpressionSyntax)
            .GroupBy(item => $"{item.Owner}.{item.Variable.Identifier.ValueText}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => ((LiteralExpressionSyntax)item.Variable.Initializer!.Value).Token.ValueText)
                    .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var processLocal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in roots.SelectMany(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()))
        {
            var attributes = node.AttributeLists.SelectMany(list => list.Attributes).ToArray();
            var definition = attributes.FirstOrDefault(attribute => AttributeName(attribute) is "CollectionDefinition" or "CollectionDefinitionAttribute");
            var local = attributes.Any(attribute => AttributeName(attribute) is "ProcessLocalTestCollection" or "ProcessLocalTestCollectionAttribute");
            if (definition is null)
            {
                if (local) throw new InvalidDataException("Process-local marker lacks a collection definition.");
                continue;
            }
            var args = definition.ArgumentList?.Arguments;
            if (args is null || args.Value.Count == 0) throw new InvalidDataException("Collection name is unavailable.");
            var collection = ResolveName(args.Value[0].Expression, constants);
            var serial = args.Value.Any(argument => argument.NameEquals?.Name.Identifier.ValueText == "DisableParallelization" &&
                argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression));
            if (local && !serial) throw new InvalidDataException($"Process-local collection '{collection}' is not serial.");
            if (serial) disabled.Add(collection);
            if (local) processLocal.Add(collection);
        }
        return (disabled, processLocal);
    }

    private static string AttributeName(AttributeSyntax attribute) => attribute.Name.ToString().Split('.').Last();

    private static string ResolveName(Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression,
        IReadOnlyDictionary<string, string[]> constants)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;
        if (expression is InvocationExpressionSyntax invocation && invocation.Expression.ToString() == "nameof")
            return invocation.ArgumentList.Arguments.First().Expression.ToString().Split('.').Last();
        var key = expression.ToString();
        if (constants.TryGetValue(key, out var values) && values.Length == 1) return values[0];
        var matches = constants.Where(item => item.Key.EndsWith($".{key}", StringComparison.Ordinal))
            .SelectMany(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length == 1) return matches[0];
        throw new InvalidDataException($"Cannot resolve collection definition name '{expression}'.");
    }
}
