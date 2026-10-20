using System.Xml.Linq;

public sealed class MtpTestRunnerScriptTestsBridgeCandidateBindings
{
    [Xunit.Fact]
    public void CandidateTransformationPreservesProductionSourceAndProperties()
    {
        var candidate = MtpTestRunnerScriptTests.RepositoryRoot();
        var bindings = new WorkerBuildBridgeCandidateBindings(candidate);
        using var copy = new GraphFixture(createGraph: false);
        bindings.WriteFixture(copy.Root);
        var original = XDocument.Load(bindings.SourceGraph.ToolProject);
        var transformed = XDocument.Load(Path.Combine(copy.Root, "scripts", "WorkerBuildScopeTool", "WorkerBuildScopeTool.csproj"));
        Assert.Empty(transformed.Descendants("ProjectReference"));
        var references = transformed.Descendants("Reference").ToArray();
        Assert.Equal(bindings.Bindings.Count, references.Length);
        foreach (var binding in bindings.Bindings)
        {
            var reference = Assert.Single(references.Where(reference => reference.Attribute("Include")!.Value == binding.Assembly.FullName));
            Assert.Equal(binding.HostPath, reference.Element("HintPath")!.Value);
            Assert.Equal("true", reference.Element("Private")!.Value);
            Assert.Equal(binding.Assembly, WorkerBuildBridgeCandidateBindings.ReadIdentity(binding.HostPath));
        }
        Assert.Equal(bindings.SourceGraph.Packages.Select(package => package.ToString()), transformed.Descendants("PackageReference").Select(package => package.ToString()));
        Normalize(original);
        Normalize(transformed);
        Assert.True(XNode.DeepEquals(original, transformed), "Only the declared reference transformation may differ.");

        foreach (var relative in new[] { "scripts/WorkerBuildScopeTool/Program.cs", "Directory.Build.props", "Directory.Build.targets", "global.json" })
        {
            var destination = Path.Combine(copy.Root, relative);
            Assert.Equal(File.ReadAllBytes(Path.Combine(candidate, relative)), File.ReadAllBytes(destination));
        }
        bindings.VerifyHostUnchanged();

        static void Normalize(XDocument document)
        {
            document.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "Reference" or "PackageReference").Remove();
            document.Descendants("ItemGroup").Where(group => !group.HasElements && !group.HasAttributes).Remove();
        }
    }

    [Xunit.Fact]
    public void GraphDerivesTransitiveReferencesAndPackageVersions()
    {
        using var fixture = new GraphFixture();
        fixture.Write("src/Entry/Entry.csproj", "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><ProjectReference Include='../Leaf/Leaf.csproj'/><PackageReference Include='Example.Package' Version='4.3.2'/></ItemGroup></Project>");
        fixture.Write("src/Leaf/Leaf.csproj", "<Project Sdk='Microsoft.NET.Sdk'/>");
        var graph = WorkerBuildBridgeCandidateBindings.ReadGraph(fixture.Root);
        Assert.Equal(new[] { "Leaf.csproj", "Entry.csproj" }, graph.Projects.Select(Path.GetFileName));
        var package = Assert.Single(graph.Packages);
        Assert.Equal("Example.Package", package.Attribute("Include")!.Value);
        Assert.Equal("4.3.2", package.Attribute("Version")!.Value);
        Assert.All(graph.Projects, project => Assert.Equal(WorkerBuildBridgeCandidateBindings.Hash(project), graph.Inputs[project]));
    }

    [Xunit.Theory]
    [Xunit.InlineData("<ProjectReference Include='../Entry/Entry.csproj'/>", "cycle")]
    [Xunit.InlineData("<ProjectReference Include='$(Foreign)/Other.csproj'/>", "Nonliteral")]
    [Xunit.InlineData("<ProjectReference Include='Other.csproj' Condition='true'/>", "Conditional")]
    [Xunit.InlineData("<PackageReference Include='Example' Version='$(Version)'/>", "Unsupported PackageReference")]
    [Xunit.InlineData("<PackageReference Include='Example'/>", "Unsupported PackageReference")]
    [Xunit.InlineData("<PackageReference Include='Example' Version='1' PrivateAssets='all'/>", "Unsupported PackageReference")]
    [Xunit.InlineData("<PackageReference Include='Example' Version='1'/><PackageReference Include='Example' Version='2'/>", "Conflicting")]
    public void UnsupportedGraphRejectsRatherThanApproximates(string item, string reason)
    {
        using var fixture = new GraphFixture();
        fixture.Write("src/Entry/Entry.csproj", "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup>" + item + "</ItemGroup></Project>");
        var failure = Assert.Throws<InvalidOperationException>(() => WorkerBuildBridgeCandidateBindings.ReadGraph(fixture.Root));
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Directory.Build.props", "<Project><ItemGroup><PackageReference Include='Injected' Version='1'/></ItemGroup></Project>")]
    [Xunit.InlineData("Directory.Build.targets", "<Project><Import Project='foreign.targets'/></Project>")]
    [Xunit.InlineData("Directory.Packages.props", "<Project/>")]
    public void ImportedOrCentralReferenceMetadataRejects(string leaf, string xml)
    {
        using var fixture = new GraphFixture();
        fixture.Write(leaf, xml);
        Assert.Throws<InvalidOperationException>(() => WorkerBuildBridgeCandidateBindings.ReadGraph(fixture.Root));
    }

    [Xunit.Fact]
    public void ForeignGenerationPathRejectsWithoutSearchingOtherOutputs()
    {
        using var fixture = new GraphFixture();
        var failure = Assert.Throws<InvalidOperationException>(() => WorkerBuildBridgeCandidateBindings.SafeRelative(fixture.Root, Path.GetDirectoryName(fixture.Root)!));
        Assert.Contains("escapes candidate generation", failure.Message, StringComparison.Ordinal);
    }

    private sealed class GraphFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bridge-graph-" + Guid.NewGuid().ToString("N"));
        internal GraphFixture(bool createGraph = true)
        {
            Directory.CreateDirectory(Root);
            if (!createGraph) return;
            Write("scripts/WorkerBuildScopeTool/WorkerBuildScopeTool.csproj", "<Project Sdk='Microsoft.NET.Sdk'><ItemGroup><ProjectReference Include='../../src/Entry/Entry.csproj'/></ItemGroup></Project>");
            Write("scripts/WorkerBuildScopeTool/Program.cs", "// fixture source");
            Write("src/Entry/Entry.csproj", "<Project Sdk='Microsoft.NET.Sdk'/>");
        }
        internal void Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        public void Dispose() => ScriptSandboxCleanup.DeleteOrThrow([Root]);
    }
}
