using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: each filesystem test owns unique temporary roots; source checks are read-only.
public sealed class AcceptanceSourceMutationTests
{
    [Fact]
    public void Apply_MultilineSpan_PreservesBomSurroundingBytesAndFileLineBreaks()
    {
        using var fixture = new MutationFixture();
        var encoding = new UTF8Encoding(true);
        var prefix = encoding.GetPreamble().Concat(encoding.GetBytes("π before\r\n")).ToArray();
        var suffix = encoding.GetBytes("\r\n雪 after\r\n");
        var original = prefix.Concat(encoding.GetBytes("old first\r\nold second")).Concat(suffix).ToArray();
        var target = fixture.Write("src/CrLf.cs", original);

        AcceptanceSourceMutation.Apply(fixture.Root,
            new FindingEvidenceMutation("src/CrLf.cs", "old first\nold second", "new first\nnew second"));

        var replacement = encoding.GetBytes("new first\r\nnew second");
        var result = File.ReadAllBytes(target);
        Assert.Equal(prefix.Concat(replacement).Concat(suffix).ToArray(), result);
        Assert.Equal(prefix, result[..prefix.Length]);
        Assert.Equal(suffix, result[^suffix.Length..]);
        Assert.Equal(encoding.GetPreamble(), result[..encoding.GetPreamble().Length]);

        var lfTarget = fixture.Write("src/Lf.cs", Encoding.UTF8.GetBytes("π before\nold first\nold second\n雪 after\n"));
        AcceptanceSourceMutation.Apply(fixture.Root,
            new FindingEvidenceMutation("src/Lf.cs", "old first\r\nold second", "new first\r\nnew second"));
        Assert.Equal(Encoding.UTF8.GetBytes("π before\nnew first\nnew second\n雪 after\n"), File.ReadAllBytes(lfTarget));
    }

    [Theory]
    [InlineData("empty", FindingEvidenceRevertPathsRejection.MutationEmptyOldText)]
    [InlineData("unchanged", FindingEvidenceRevertPathsRejection.MutationUnchangedText)]
    [InlineData("missing-file", FindingEvidenceRevertPathsRejection.MutationOldTextNotFound)]
    [InlineData("absent-text", FindingEvidenceRevertPathsRejection.MutationOldTextNotFound)]
    [InlineData("ambiguous", FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous)]
    [InlineData("selected-class", FindingEvidenceRevertPathsRejection.MutationSelectedTestClass)]
    [InlineData("under-tests", FindingEvidenceRevertPathsRejection.MutationUnderTests)]
    [InlineData("outside-src", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [InlineData("not-changed", FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal)]
    public void Validate_RejectedMutation_PreservesRejectionAndPolicyOrder(
        string scenario, FindingEvidenceRevertPathsRejection expected)
    {
        using var fixture = new MutationFixture();
        if (scenario != "missing-file")
            fixture.Write("src/Example.cs", Encoding.UTF8.GetBytes(scenario switch
            {
                "absent-text" => "unrelated text",
                "ambiguous" => "old text / old text",
                _ => "old text"
            }));
        var oldText = scenario == "empty" ? "" : scenario == "unchanged" ? "old\r\ntext" : "old text";
        var newText = scenario == "unchanged" ? "old\ntext" : "new text";
        var mutation = new FindingEvidenceMutation(@"src\Example.cs", oldText, newText);
        FindingEvidenceRevertPathsRejection? policyRejection = scenario switch
        {
            "under-tests" => FindingEvidenceRevertPathsRejection.UnderTests,
            "outside-src" => FindingEvidenceRevertPathsRejection.OutsideSrc,
            "not-changed" => FindingEvidenceRevertPathsRejection.NotChangedByGoal,
            _ => null
        };
        var calls = new List<string>();
        FindingEvidenceRevertPathsRejection? ValidatePath(string path)
        {
            Assert.Equal("src/Example.cs", path);
            calls.Add("path");
            return policyRejection;
        }
        bool IsSelectedTestClass(string path)
        {
            Assert.Equal("src/Example.cs", path);
            calls.Add("selected-class");
            return scenario == "selected-class";
        }

        var result = AcceptanceSourceMutation.Validate(mutation, [], fixture.Root, "candidate", "base", [], null,
            ValidatePath, IsSelectedTestClass);

        Assert.Equal(expected, result);
        Assert.Equal(policyRejection is null ? new[] { "path", "selected-class" } : new[] { "path" }, calls);
    }

    [Fact]
    public void Validate_UniqueMultilineMatch_ReturnsNoRejection()
    {
        using var fixture = new MutationFixture();
        fixture.Write("src/Example.cs", Encoding.UTF8.GetBytes("before\r\nold first\r\nold second\r\nafter"));
        Assert.Null(AcceptanceSourceMutation.Validate(
            new FindingEvidenceMutation("src/Example.cs", "old first\nold second", "new text"),
            [], fixture.Root, "candidate", "base", [], null, _ => null, _ => false));
    }

    [Fact]
    public void Apply_InvalidUtf8_ThrowsWithoutChangingBytes()
    {
        using var fixture = new MutationFixture();
        byte[] original = [0x6f, 0x6c, 0x64, 0x20, 0xc3, 0x28];
        var target = fixture.Write("src/Invalid.cs", original);

        Assert.ThrowsAny<Exception>(() => AcceptanceSourceMutation.Apply(fixture.Root,
            new FindingEvidenceMutation("src/Invalid.cs", "old", "new")));

        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void Apply_OutsideWorktree_ThrowsInvalidDataWithoutChangingBytes()
    {
        using var fixture = new MutationFixture();
        using var outside = new MutationFixture();
        var original = Encoding.UTF8.GetBytes("old text");
        var target = outside.Write("Outside.cs", original);
        var escapedPath = Path.GetRelativePath(fixture.Root, target);

        Assert.Throws<InvalidDataException>(() => AcceptanceSourceMutation.Apply(fixture.Root,
            new FindingEvidenceMutation(escapedPath, "old text", "new text")));

        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void Apply_DirectoryLink_ThrowsInvalidDataWithoutChangingBytes()
    {
        using var outside = new MutationFixture();
        using var fixture = new MutationFixture();
        var original = Encoding.UTF8.GetBytes("old text");
        var target = outside.Write("Example.cs", original);
        var link = Path.Combine(fixture.Root, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Directory symbolic links are unavailable on this host: {exception.Message}");
        }
        fixture.TrackLink(link);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(link, "Example.cs")));

        Assert.Throws<InvalidDataException>(() => AcceptanceSourceMutation.Apply(fixture.Root,
            new FindingEvidenceMutation("linked/Example.cs", "old text", "new text")));

        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void SourceRevertedArm_ExtractedMutation_DelegatesToSeparateOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        var workspaces = Path.Combine(root, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces");
        var arm = File.ReadAllText(Path.Combine(workspaces, "GoalAcceptanceVerifier.SourceRevertedArm.cs"));
        Assert.Contains("AcceptanceSourceMutation.Validate", arm);
        Assert.Contains("AcceptanceSourceMutation.Apply", arm);
        Assert.False(File.Exists(Path.Combine(workspaces, "GoalAcceptanceVerifier.SourceMutation.cs")));
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositorySourceInventory.IsExcludedRelativePath(Path.GetRelativePath(root, path)))
            .Where(path => !string.Equals(Path.GetFullPath(path),
                Path.GetFullPath(Path.Combine(workspaces, "AcceptanceSourceMutation.cs")), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var declarations = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>();
            Assert.DoesNotContain(declarations, declaration => declaration.Identifier.ValueText == "FindSourceMutationSpan");
        }
    }

    [Fact]
    public void Canary_NewMutationOwner_TriggersAcceptanceCanary()
    {
        Assert.True(PostLandingCanaryTrigger.Evaluate(
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceSourceMutation.cs"]).ShouldRun);
    }

    private sealed class MutationFixture : IDisposable
    {
        private readonly List<string> links = [];
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "acceptance-source-mutation-" + Guid.NewGuid().ToString("N"));

        internal MutationFixture() => Directory.CreateDirectory(Root);

        internal string Write(string path, byte[] bytes)
        {
            var target = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            return target;
        }

        internal void TrackLink(string path) => links.Add(path);

        public void Dispose()
        {
            foreach (var link in links)
                Directory.Delete(link);
            Directory.Delete(Root, recursive: true);
        }
    }
}
