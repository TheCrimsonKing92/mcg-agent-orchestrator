using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Reads explicit xunit serialization declarations, without treating usages as hazards.</summary>
internal static class DotnetSharedStateHazardReader
{
    public static IReadOnlyList<SharedStateHazard> Read(string root, IReadOnlyList<ProjectUnit> units,
        IReadOnlyList<UnitDependency> dependencies, List<ProjectOwnerQuestion> questions)
    {
        var hazards = new List<SharedStateHazard>();
        var knownUnits = units.Select(unit => unit.Id).ToHashSet(StringComparer.Ordinal);
        var referencedConstants = new Dictionary<string, LiteralConstant[]>(StringComparer.Ordinal);
        foreach (var unit in units.Where(unit => unit.IsTest.Value == true).OrderBy(unit => unit.Id, StringComparer.Ordinal))
        {
            var directory = Path.GetDirectoryName(Path.Combine(root, unit.Id))!;
            var sources = ReadSources(root, directory, unit.Id, questions);
            LiteralConstant[]? constants = null;
            LiteralConstant[]? directConstants = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, text) in sources)
            {
                if (!text.Contains("CollectionDefinition", StringComparison.Ordinal) &&
                    !text.Contains("CollectionBehavior", StringComparison.Ordinal))
                    continue;
                var syntax = CSharpSyntaxTree.ParseText(text).GetRoot();
                foreach (var attribute in syntax.DescendantNodes().OfType<AttributeSyntax>())
                {
                    string name;
                    string key;
                    var confidence = FactConfidence.High;
                    if (IsAttribute(attribute, "CollectionDefinition") &&
                        IsTrue(attribute, "DisableParallelization"))
                    {
                        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault(argument =>
                            argument.NameEquals is null && (argument.NameColon is null ||
                                argument.NameColon.Name.Identifier.ValueText == "name"))?.Expression;
                        name = expression?.ToString() ?? "unresolved collection name";
                        string? literal = null;
                        if (expression is LiteralExpressionSyntax value && value.IsKind(SyntaxKind.StringLiteralExpression))
                            literal = value.Token.ValueText;
                        else if (expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or AliasQualifiedNameSyntax)
                        {
                            constants ??= ReadConstants(sources);
                            literal = Resolve(expression, constants, out var foundInUnit);
                            // An ambiguous local name stays unknown; references cannot override it.
                            // Cross-unit lookup requires a type/member reference, never a bare member.
                            if (!foundInUnit && ReferenceName(expression) is { } referenceName && referenceName.Contains('.'))
                            {
                                directConstants ??= ReadDirectConstants(root, unit.Id, knownUnits,
                                    dependencies, referencedConstants, questions);
                                literal = Resolve(expression, directConstants, out _);
                            }
                        }
                        if (string.IsNullOrWhiteSpace(literal))
                        {
                            key = "";
                            confidence = FactConfidence.Low;
                        }
                        else
                        {
                            name = literal;
                            key = "xunit:" + literal;
                        }
                    }
                    else if (IsAttribute(attribute, "CollectionBehavior") &&
                        attribute.Parent is AttributeListSyntax { Target.Identifier.ValueText: "assembly" } &&
                        IsTrue(attribute, "DisableTestParallelization"))
                    {
                        name = "assembly:DisableTestParallelization";
                        key = "xunit:*";
                    }
                    else
                        continue;

                    var source = new FactSource(path, attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                    if (!seen.Add(key.Length == 0 ? $"unknown:{path}#{source.Line}" : $"key:{key}"))
                        continue;
                    var fact = DotnetProjectDiscoveryAdapter.Fact(key, confidence, source,
                        $"hazards/{unit.Id}/{path}#{source.Line}",
                        $"Confirm the isolation key for collection name '{name}'; no literal value could be resolved in this unit.", questions);
                    hazards.Add(new SharedStateHazard(unit.Id, name, fact));
                }
            }
        }
        return hazards;
    }

    private static bool IsAttribute(AttributeSyntax attribute, string name)
    {
        var actual = attribute.Name.ToString();
        if (actual.StartsWith("global::", StringComparison.Ordinal))
            actual = actual["global::".Length..];
        return actual == name || actual == name + "Attribute" ||
            actual == "Xunit." + name || actual == "Xunit." + name + "Attribute";
    }

    private static bool IsTrue(AttributeSyntax attribute, string property) =>
        attribute.ArgumentList?.Arguments.Any(argument =>
            argument.NameEquals?.Name.Identifier.ValueText == property &&
            argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression)) == true;

    private static SortedDictionary<string, string> ReadSources(string root, string directory, string unitId,
        List<ProjectOwnerQuestion> questions)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory) || !DotnetProjectDiscoveryAdapter.IsSafePath(root, directory))
            return sources;
        foreach (var path in SourcePaths(root, directory).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            try
            {
                sources.Add(relative, File.ReadAllText(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DotnetProjectDiscoveryAdapter.Ask($"hazards/{unitId}/{relative}",
                    $"Confirm shared-state declarations; source could not be read: {exception.Message}",
                    new FactSource(relative, 1), questions);
            }
        }
        return sources;
    }

    private static IEnumerable<string> SourcePaths(string root, string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            if (DotnetProjectDiscoveryAdapter.IsSafePath(root, file))
                yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                name is ".git" or ".scratch" or ".orchestrator-prototype" or ".orchestrator-worktrees" or "TestResults" or "playwright-report" ||
                !DotnetProjectDiscoveryAdapter.IsSafePath(root, child) ||
                Directory.EnumerateFiles(child).Any(file => Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj"))
                continue;
            foreach (var file in SourcePaths(root, child))
                yield return file;
        }
    }

    private static LiteralConstant[] ReadConstants(SortedDictionary<string, string> sources)
    {
        var constants = new List<LiteralConstant>();
        foreach (var text in sources.Values.Where(text => text.Contains("const", StringComparison.Ordinal)))
        {
            var syntax = CSharpSyntaxTree.ParseText(text).GetRoot();
            foreach (var field in syntax.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (!field.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                    field.Declaration.Type.ToString() is not ("string" or "String" or "System.String" or "global::System.String"))
                    continue;
                var types = field.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
                    .Select(type => type.Identifier.ValueText);
                var namespaces = field.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
                    .Select(declaration => declaration.Name.ToString());
                var prefix = string.Join(".", namespaces.Concat(types));
                foreach (var variable in field.Declaration.Variables)
                    constants.Add(new LiteralConstant(prefix + "." + variable.Identifier.ValueText,
                        variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                            literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null));
            }
        }
        return constants.ToArray();
    }

    private static LiteralConstant[] ReadDirectConstants(string root, string unitId, HashSet<string> knownUnits,
        IReadOnlyList<UnitDependency> dependencies, Dictionary<string, LiteralConstant[]> cache,
        List<ProjectOwnerQuestion> questions)
    {
        var constants = new List<LiteralConstant>();
        foreach (var target in dependencies.Where(edge => edge.FromUnit == unitId)
            .GroupBy(edge => edge.ToUnit, StringComparer.Ordinal)
            .Where(group => knownUnits.Contains(group.Key) && group.All(edge => edge.Confidence == FactConfidence.High))
            .Select(group => group.Key).Order(StringComparer.Ordinal))
        {
            if (!cache.TryGetValue(target, out var referenced))
            {
                var directory = Path.GetDirectoryName(Path.Combine(root, target))!;
                referenced = ReadConstants(ReadSources(root, directory, target, questions));
                cache.Add(target, referenced);
            }
            constants.AddRange(referenced);
        }
        return constants.ToArray();
    }

    private static string? Resolve(ExpressionSyntax expression, LiteralConstant[] constants, out bool found)
    {
        var name = ReferenceName(expression);
        found = false;
        if (name is null)
            return null;
        var candidates = constants.Where(constant => constant.Name == name ||
            constant.Name.EndsWith("." + name, StringComparison.Ordinal)).Select(constant => constant.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        found = candidates.Length != 0;
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string? ReferenceName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member when ReferenceName(member.Expression) is { } prefix =>
            prefix + "." + member.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax alias when alias.Alias.Identifier.ValueText == "global" => alias.Name.Identifier.ValueText,
        _ => null
    };

    private sealed record LiteralConstant(string Name, string? Value);
}
