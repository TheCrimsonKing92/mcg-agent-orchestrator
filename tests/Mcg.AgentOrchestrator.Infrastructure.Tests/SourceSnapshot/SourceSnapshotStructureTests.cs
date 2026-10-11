using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: reads the verified repository through the shared immutable snapshot.
public sealed class SourceSnapshotStructureTests
{
    private static readonly string[] MigratedGuards =
    [
        "PowerShellLaunchSourceGuardTests", "RedirectedStreamDrainSourceGuardTests", "CallerFilePathRootSourceGuardTests",
        "DirectGitLaunchSourceGuardTests", "ParallelSharedStateSourceGuardTests", "ProcessStartInfoSourceGuardTests",
        "OrchestratorTempRootSourceGuardTests", "DashboardRemnantSourceGuardTests", "LegacySparkNameSourceGuardTests",
        "ExecutionOwnedGroupCloseSourceGuardTests", "BuildServerShutdownSourceGuardTests", "ExecutionWorkspacesReferenceGuardTests"
    ];
    private static readonly string[] SnapshotTypes =
        ["RepositorySourceSnapshot", "RepositorySourceSnapshotCache", "ISourceSnapshotObserver", "SourceGuardFileFilter"];
    private const string ProjectPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";

    [Fact]
    public void MigratedGuardsUseSnapshotAndNeverWalkReadOrParseRepositoryFiles()
    {
        var snapshot = RepositorySourceSnapshotCache.Get(VerifiedRepositoryRoot.Find());
        foreach (var guard in MigratedGuards)
        {
            var root = snapshot.GetSyntaxTree(ProjectPath + guard + ".cs").GetRoot();
            Assert.Contains(root.DescendantNodes().OfType<IdentifierNameSyntax>(), node => node.Identifier.ValueText == "RepositorySourceSnapshotCache");
            Assert.Empty(ForbiddenIo(root));
            foreach (var parse in Calls(root, "CSharpSyntaxTree.ParseText"))
            {
                var method = parse.Ancestors().OfType<MethodDeclarationSyntax>().First();
                var parameter = Assert.Single(method.ParameterList.Parameters);
                Assert.Equal("string", parameter.Type!.ToString());
                Assert.Equal(parameter.Identifier.ValueText, Assert.Single(parse.ArgumentList.Arguments).Expression.ToString());
                // The only direct parser is the inline-string detector overload. Cached-file callers
                // must supply GetSyntaxTree instead; no repository text can reach this overload.
                foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                             .Where(call => call.Expression.ToString() == method.Identifier.ValueText))
                {
                    var owner = call.Ancestors().OfType<MethodDeclarationSyntax>().First();
                    if (owner.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(node => node.Identifier.ValueText == "RepositorySourceSnapshotCache"))
                        Assert.Contains("GetSyntaxTree", call.ArgumentList.ToString(), StringComparison.Ordinal);
                }
            }
        }
        var source = snapshot.GetSyntaxTree(ProjectPath + "SourceSnapshot/RepositorySourceSnapshot.cs").GetRoot();
        Assert.NotEmpty(Calls(source, "Directory.EnumerateFiles"));
        Assert.NotEmpty(Calls(source, "File.ReadAllText"));
        Assert.NotEmpty(Calls(source, "CSharpSyntaxTree.ParseText"));
    }

    [Fact]
    public void StructuralDetectorRejectsPreMigrationWalkReadAndParse()
    {
        var root = CSharpSyntaxTree.ParseText("class C { void M(string folder) { " +
            "foreach (var file in Directory.EnumerateFiles(folder, \"*.cs\", SearchOption.AllDirectories)) " +
            "{ var text = File.ReadAllText(file); var lines = File.ReadAllLines(file); " +
            "var syntax = CSharpSyntaxTree.ParseText(text); } } }").GetRoot();
        Assert.Equal(3, ForbiddenIo(root).Count());
        var parse = Assert.Single(Calls(root, "CSharpSyntaxTree.ParseText"));
        var parameter = Assert.Single(parse.Ancestors().OfType<MethodDeclarationSyntax>().First().ParameterList.Parameters);
        Assert.NotEqual(parameter.Identifier.ValueText, parse.ArgumentList.Arguments[0].Expression.ToString());
    }

    [Fact]
    public void NewTypesEachHaveOneOwningFileAndNoPartialDeclarations()
    {
        var snapshot = RepositorySourceSnapshotCache.Get(VerifiedRepositoryRoot.Find());
        foreach (var name in SnapshotTypes)
        {
            var path = ProjectPath + "SourceSnapshot/" + name + ".cs";
            var syntax = snapshot.GetSyntaxTree(path).GetRoot();
            var declaration = Assert.Single(syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Where(node => !node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any()));
            Assert.Equal(name, declaration.Identifier.ValueText);
            Assert.DoesNotContain(syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>(),
                type => type.Modifiers.Any(SyntaxKind.PartialKeyword));
            var owners = snapshot.Files.Where(file => file.StartsWith("tests/", StringComparison.Ordinal))
                .Where(file => snapshot.GetText(file).Contains(name, StringComparison.Ordinal))
                .Where(file => snapshot.GetSyntaxTree(file).GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Any(type => type.Identifier.ValueText == name)).ToArray();
            Assert.Equal(new[] { path }, owners);
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == name);

    private static IEnumerable<string> ForbiddenIo(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.Expression.ToString())
            .Where(name => name is "Directory.EnumerateFiles" or "Directory.GetFiles" or "File.ReadAllText" or "File.ReadAllLines");
}
