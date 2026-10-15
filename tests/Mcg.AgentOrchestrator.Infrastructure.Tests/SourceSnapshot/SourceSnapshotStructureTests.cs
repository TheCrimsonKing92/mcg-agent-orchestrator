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

    [Theory]
    [InlineData("SourceClassSizeRatchetTests", "RealTable_MeasuredTotalsAndCountsAreAtOrUnderCeilings", "snapshot.GetLines")]
    [InlineData("AcceptanceSourceMutationTests", "SourceRevertedArm_ExtractedMutation_DelegatesToSeparateOwner", "snapshot.GetSyntaxTree")]
    public void MixedFixtureRepositoryGuardsUseCachedFiles(string type, string method, string cachedRead)
    {
        var snapshot = RepositorySourceSnapshotCache.Get(VerifiedRepositoryRoot.Find());
        var root = snapshot.GetSyntaxTree(ProjectPath + type + ".cs").GetRoot();
        // These classes also exercise mutable temporary fixtures. Only the immutable repository
        // guard should share the snapshot; fixture reads must retain their fresh-read semantics.
        AssertCachedRepositoryGuard(root, method, cachedRead);
        if (type == "AcceptanceSourceMutationTests")
            Assert.NotEmpty(Calls(FindMethod(root, method), "snapshot.GetText"));
        else
            AssertHoistedJoin(FindMethod(root, method));
    }

    [Theory]
    [InlineData("Directory.EnumerateFiles(folder)")]
    [InlineData("Directory.GetFiles(folder)")]
    [InlineData("File.ReadAllText(file)")]
    [InlineData("File.ReadAllLines(file)")]
    [InlineData("CSharpSyntaxTree.ParseText(text)")]
    public void MethodScopedDetector_DirectRepositoryIo_IsRejected(string directCall)
    {
        var root = CSharpSyntaxTree.ParseText("class C { void Guard() { " +
            "var snapshot = RepositorySourceSnapshotCache.Get(folder); snapshot.GetLines(file); " +
            directCall + "; } void Fixture() { File.ReadAllText(file); } }").GetRoot();

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertCachedRepositoryGuard(root, "Guard", "snapshot.GetLines"));
    }

    [Fact]
    public void MethodScopedDetector_FreshIoInAnotherMethod_IsPermitted()
    {
        var root = CSharpSyntaxTree.ParseText("class C { void Guard() { " +
            "var snapshot = RepositorySourceSnapshotCache.Get(folder); snapshot.GetLines(file); } " +
            "void Fixture() { File.ReadAllText(file); File.ReadAllLines(file); " +
            "Directory.EnumerateFiles(folder); CSharpSyntaxTree.ParseText(text); } }").GetRoot();
        Assert.Equal(3, ForbiddenIo(root).Count());
        Assert.Single(Calls(root, "CSharpSyntaxTree.ParseText"));

        AssertCachedRepositoryGuard(root, "Guard", "snapshot.GetLines");
    }

    [Fact]
    public void HoistedJoinDetector_StringConstructionInsideClassLoop_IsRejected()
    {
        var root = CSharpSyntaxTree.ParseText("class C { void Guard() { " +
            "foreach (var row in rows) { var files = lines.Select(x => string.Join(\"\\n\", x)).ToArray(); } } }").GetRoot();

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertHoistedJoin(FindMethod(root, "Guard")));
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

    private static MethodDeclarationSyntax FindMethod(SyntaxNode root, string method) =>
        Assert.Single(root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == method));

    private static void AssertCachedRepositoryGuard(SyntaxNode root, string method, string cachedRead)
    {
        var guard = FindMethod(root, method);
        Assert.NotEmpty(Calls(guard, "RepositorySourceSnapshotCache.Get"));
        Assert.NotEmpty(Calls(guard, cachedRead));
        Assert.Empty(ForbiddenIo(guard));
        Assert.Empty(Calls(guard, "CSharpSyntaxTree.ParseText"));
    }

    private static void AssertHoistedJoin(MethodDeclarationSyntax guard)
    {
        var join = Assert.Single(Calls(guard, "string.Join"));
        Assert.Empty(join.Ancestors().OfType<ForEachStatementSyntax>());
        var materialization = join.Ancestors().OfType<LocalDeclarationStatementSyntax>().First();
        Assert.Contains(materialization.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "ToArray");
        var classLoop = Assert.Single(guard.Body!.Statements.OfType<ForEachStatementSyntax>());
        Assert.True(materialization.Span.End < classLoop.SpanStart);
    }

    private static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == name);

    private static IEnumerable<string> ForbiddenIo(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.Expression.ToString())
            .Where(name => name is "Directory.EnumerateFiles" or "Directory.GetFiles" or "File.ReadAllText" or "File.ReadAllLines");
}
