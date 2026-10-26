using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

// Parallel-safe: synthetic scans are pure; the repository scan only reads the shared snapshot.
public sealed class OwnerConsoleControllerConstructionGuardTests
{
    private const string FactoryPath =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsoleControllerFactory.cs";
    private const string ConstructionMarker = "new " + "OwnerConsoleScreenController(";

    [Fact]
    public void TestSources_ControllerConstruction_UsesOnlySharedFactory()
    {
        var snapshot = RepositorySourceSnapshotCache.Get(InfrastructureTestSupport.FindRepositoryRoot());
        var sources = snapshot.Files
            .Where(path => path.StartsWith("tests/", StringComparison.Ordinal) &&
                path.EndsWith(".cs", StringComparison.Ordinal) &&
                !SourceGuardFileFilter.BuildOutputAndScratch.Excludes(path))
            .ToDictionary(path => path, path => snapshot.GetText(path), StringComparer.Ordinal);

        Assert.NotEmpty(sources);
        Assert.Empty(Scan(sources));
        Assert.Equal(1, sources[FactoryPath].Split(ConstructionMarker, StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("Explicit.cs", ConstructionMarker + "a, b);")]
    [InlineData("FieldAssignment.cs", "class C { OwnerConsoleScreenController Controller; C() { Controller = new(a, b); } }")]
    [InlineData("MethodBody.cs", "class C { static OwnerConsoleScreenController Controller() => new(a, b); }")]
    [InlineData("Local.cs", "class C { void M() { OwnerConsoleScreenController c = new(a, b); } }")]
    [InlineData("elsewhere/OwnerConsoleControllerFactory.cs", ConstructionMarker + "a, b);")]
    [InlineData("View.cs", "new OwnerConsoleFullScreenView(app, new(a, b), x);")]
    [InlineData("NamedView.cs", "new OwnerConsoleFullScreenView(controller: new(a, b), app: app, refresh: x);")]
    [InlineData("ImplicitView.cs", "class C { OwnerConsoleFullScreenView View() => new(app, new(a, b), x); }")]
    [InlineData("FieldInitializer.cs", "class C { OwnerConsoleScreenController c = new(a, b); }")]
    [InlineData("PropertyInitializer.cs", "class C { OwnerConsoleScreenController Ctl { get; } = new(a, b); }")]
    [InlineData("PropertyAssignment.cs", "class C { OwnerConsoleScreenController Ctl { get; set; } void M() { Ctl = new(a, b); } }")]
    [InlineData("ThisField.cs", "class C { OwnerConsoleScreenController c; void M(Other c) { this.c = new(a, b); } }")]
    [InlineData("Return.cs", "class C { OwnerConsoleScreenController M() { return new(a, b); } }")]
    [InlineData("Parenthesized.cs", "class C { OwnerConsoleScreenController M() { return ((new(a, b))); } }")]
    [InlineData("Qualified.cs", "class C { global::App.OwnerConsoleScreenController? c = new(a, b); }")]
    [InlineData("OuterLocal.cs", "class C { void M() { OwnerConsoleScreenController c; { c = new(a, b); } } }")]
    [InlineData("LocalFunction.cs", "class C { void M() { OwnerConsoleScreenController F() { return new(a, b); } } }")]
    public void Scan_ConstructionShape_ReportsItsPath(string path, string source)
    {
        Assert.Equal([path], Scan(new Dictionary<string, string> { [path] = source }));
    }

    [Theory]
    [InlineData("new OwnerConsoleFullScreenView(app, controller, x);")]
    [InlineData("OwnerConsoleScreenController.AnswersInConsole(x);")]
    [InlineData("class C { void M(OwnerConsoleScreenController controller) { } }")]
    [InlineData("class C { OwnerConsoleScreenController c; C() { c = OwnerConsoleControllerFactory.Create(a, b); } }")]
    [InlineData("class C { Other c; C() { c = new(a, b); } }")]
    [InlineData("class C { OwnerConsoleScreenController c; void M(Other c) { c = new(a, b); } }")]
    [InlineData("class C { OwnerConsoleScreenController c; void M() { Other c; c = new(a, b); } }")]
    [InlineData("class C { OwnerConsoleScreenController c; void M() { { Other c; c = new(a, b); } } }")]
    [InlineData("class C { OwnerConsoleScreenController M() { Func<Other> f = () => { return new(a, b); }; return c; } }")]
    [InlineData("class C { OwnerConsoleScreenController M() { Other F() { return new(a, b); } return c; } }")]
    [InlineData("class C { OwnerConsoleScreenController c; void M() { Action<Other> f = c => { c = new(a, b); }; } }")]
    [InlineData("class C { OwnerConsoleScreenController c; void M() { Action<Other> f = delegate(Other c) { c = new(a, b); }; } }")]
    [InlineData("new OwnerConsoleFullScreenView(app, controller, refresh: new(a, b));")]
    [InlineData("class C { OwnerConsoleScreenController c; class Nested { Other c; void M() { c = new(a, b); } } }")]
    public void Scan_NonConstructionOrDifferentTarget_ReportsNothing(string source)
    {
        Assert.Empty(Scan(new Dictionary<string, string> { ["Read.cs"] = source }));
    }

    [Fact]
    public void Scan_FactoryExemption_MatchesOnlyExactNormalizedPath()
    {
        var source = ConstructionMarker + "a, b);";
        Assert.Empty(Scan(new Dictionary<string, string> { [FactoryPath] = source }));
        Assert.Empty(Scan(new Dictionary<string, string> { [FactoryPath.Replace('/', '\\')] = source }));
        Assert.Equal([FactoryPath.ToUpperInvariant()], Scan(new Dictionary<string, string>
        {
            [FactoryPath.ToUpperInvariant()] = source
        }));
        Assert.Equal(["prefix/" + FactoryPath], Scan(new Dictionary<string, string>
        {
            ["prefix/" + FactoryPath] = source
        }));
    }

    [Fact]
    public void Scan_MultipleConstructions_ReturnsEachOriginalPathOnceInOrdinalOrder()
    {
        var source = ConstructionMarker + "a, b); " + ConstructionMarker + "c, d);";
        Assert.Equal(["A.cs", "z\\B.cs"], Scan(new Dictionary<string, string>
        {
            ["z\\B.cs"] = source,
            ["A.cs"] = source
        }));
    }

    private static string[] Scan(IReadOnlyDictionary<string, string> sources) => sources
        .Where(source => !string.Equals(source.Key.Replace('\\', '/'), FactoryPath, StringComparison.Ordinal))
        .Where(source => source.Value.Contains(ConstructionMarker, StringComparison.Ordinal) ||
            CSharpSyntaxTree.ParseText(source.Value).GetRoot().DescendantNodes()
                .OfType<ImplicitObjectCreationExpressionSyntax>()
                .Any(creation => IsType(TargetType(creation), "OwnerConsoleScreenController")))
        .Select(source => source.Key)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static bool IsType(TypeSyntax? type, string name) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == name,
        QualifiedNameSyntax qualified => IsType(qualified.Right, name),
        AliasQualifiedNameSyntax qualified => IsType(qualified.Name, name),
        NullableTypeSyntax nullable => IsType(nullable.ElementType, name),
        _ => false
    };

    private static TypeSyntax? TargetType(ExpressionSyntax expression)
    {
        while (expression.Parent is ParenthesizedExpressionSyntax parentheses)
            expression = parentheses;

        return expression.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }
                => declaration.Type,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => property.Type,
            AssignmentExpressionSyntax assignment when assignment.Right == expression
                => AssignedType(assignment.Left),
            ArrowExpressionClauseSyntax arrow => DeclaredReturnType(arrow.Parent),
            ReturnStatementSyntax statement => EnclosingReturnType(statement),
            ArgumentSyntax argument => ViewControllerType(argument),
            _ => null
        };
    }

    private static TypeSyntax? DeclaredReturnType(SyntaxNode? declaration) => declaration switch
    {
        MethodDeclarationSyntax method => method.ReturnType,
        LocalFunctionStatementSyntax function => function.ReturnType,
        PropertyDeclarationSyntax property => property.Type,
        IndexerDeclarationSyntax indexer => indexer.Type,
        AccessorDeclarationSyntax accessor => DeclaredReturnType(accessor.Parent?.Parent),
        _ => null
    };

    private static TypeSyntax? EnclosingReturnType(ReturnStatementSyntax statement)
    {
        foreach (var ancestor in statement.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax)
                return null;
            if (ancestor is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax)
                return DeclaredReturnType(ancestor);
        }
        return null;
    }

    private static TypeSyntax? ViewControllerType(ArgumentSyntax argument)
    {
        if (argument.Parent is not ArgumentListSyntax arguments)
            return null;
        var viewType = arguments.Parent switch
        {
            ObjectCreationExpressionSyntax creation => creation.Type,
            ImplicitObjectCreationExpressionSyntax creation => TargetType(creation),
            _ => null
        };
        if (!IsType(viewType, "OwnerConsoleFullScreenView"))
            return null;
        var isController = argument.NameColon is { } named
            ? named.Name.Identifier.ValueText == "controller"
            : arguments.Arguments.IndexOf(argument) == 1;
        return isController ? SyntaxFactory.IdentifierName("OwnerConsoleScreenController") : null;
    }

    private static TypeSyntax? AssignedType(ExpressionSyntax target)
    {
        while (target is ParenthesizedExpressionSyntax parentheses)
            target = parentheses.Expression;
        if (target is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member)
            return MemberType(target.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault(),
                member.Name.Identifier.ValueText);
        if (target is not IdentifierNameSyntax identifier)
            return null;

        var name = identifier.Identifier.ValueText;
        foreach (var scope in target.Ancestors())
        {
            // Only direct block declarations are visible here; declarations in sibling blocks cannot shadow.
            if (scope is BlockSyntax block)
            {
                var local = block.Statements.OfType<LocalDeclarationStatementSyntax>()
                    .Where(statement => statement.SpanStart < target.SpanStart)
                    .Select(statement => statement.Declaration)
                    .FirstOrDefault(declaration => declaration.Variables.Any(v => v.Identifier.ValueText == name));
                if (local is not null)
                    return local.Type;
            }
            if (scope is ForEachStatementSyntax each && each.Identifier.ValueText == name)
                return each.Type;
            if (scope is CatchClauseSyntax { Declaration: { } caught } && caught.Identifier.ValueText == name)
                return caught.Type;
            var variables = scope switch
            {
                ForStatementSyntax loop => loop.Declaration,
                UsingStatementSyntax statement => statement.Declaration,
                _ => null
            };
            if (variables?.Variables.Any(v => v.Identifier.ValueText == name) == true)
                return variables.Type;

            var parameters = scope switch
            {
                BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
                LocalFunctionStatementSyntax function => function.ParameterList.Parameters,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
                SimpleLambdaExpressionSyntax lambda => SyntaxFactory.SingletonSeparatedList(lambda.Parameter),
                AnonymousMethodExpressionSyntax anonymous => anonymous.ParameterList?.Parameters ?? default,
                TypeDeclarationSyntax type => type.ParameterList?.Parameters ?? default,
                _ => default
            };
            var parameter = parameters.FirstOrDefault(p => p.Identifier.ValueText == name);
            if (parameter is not null)
                return parameter.Type;
            if (scope is TypeDeclarationSyntax containingType)
                return MemberType(containingType, name);
        }
        return null;
    }

    private static TypeSyntax? MemberType(TypeDeclarationSyntax? type, string name)
    {
        if (type is null)
            return null;
        foreach (var member in type.Members)
        {
            if (member is FieldDeclarationSyntax field &&
                field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name))
                return field.Declaration.Type;
            if (member is PropertyDeclarationSyntax property && property.Identifier.ValueText == name)
                return property.Type;
        }
        return null;
    }
}
