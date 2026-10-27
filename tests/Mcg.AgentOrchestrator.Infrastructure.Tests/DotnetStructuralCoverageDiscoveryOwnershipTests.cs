using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: reflection is read-only and filesystem fixtures own unique temporary roots.
public sealed class DotnetStructuralCoverageDiscoveryOwnershipTests
{
    private const string Project = "tests/Sample.Tests/Sample.Tests.csproj";
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Fact]
    public void Verifier_DeclaresOnlyTheThreeRequiredForwarders()
    {
        var verifier = typeof(GoalAcceptanceVerifier);
        foreach (var name in new[]
                 { "IsBroadTestProjectCheck", "RemoveTestFilter", "BuildDiscoveryArguments", "UseDotnetHostForManagedExecutable" })
            Assert.Empty(verifier.GetMember(name, DeclaredMembers));

        var declarations = Forwarder("EnsureTrustedStructuralCoverageDeclarations");
        Assert.True(declarations.IsAssembly);
        foreach (var name in new[] { "BuildTrustedStructuralCoverageCheck", "BuildUnattendedDiscoveryArguments" })
            Assert.True(Forwarder(name).IsPrivate);
    }

    [Fact]
    public void DiscoveryOwner_IsStandaloneStaticAndDeclaresEachMethodOnce()
    {
        var owner = typeof(DotnetStructuralCoverageDiscovery);
        Assert.True(owner.IsNotPublic);
        Assert.True(owner.IsAbstract);
        Assert.True(owner.IsSealed);
        Assert.False(owner.IsNested);
        foreach (var name in new[]
                 { "EnsureDeclarations", "IsBroadTestProjectCheck", "BuildTrustedCheck", "RemoveTestFilter",
                     "BuildDiscoveryArguments", "BuildUnattendedDiscoveryArguments" })
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(Assert.Single(owner.GetMember(name, DeclaredMembers)));
            Assert.True(method.IsStatic);
            Assert.True(method.IsAssembly);
        }
    }

    [Theory]
    [InlineData("vstest")]
    [InlineData("mtp")]
    public void UnattendedForwarder_BothRunners_MatchesOwner(string runner)
    {
        var check = new AcceptanceManifestCheck { Type = "dotnet-test", Project = Project, Runner = runner };
        var environment = new DotnetBuildEnvironment("lease", "root", @"C:\repo\artifacts", "lock",
            ["--artifacts-path", @"C:\repo\artifacts", "-maxcpucount:4"], "owner");
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations =
            [new AcceptanceMtpInvocation { Project = Project, ExecutablePathTemplate = "bin/{projectName}{executableExtension}" }]
        };

        var forwarded = Assert.IsType<string[]>(Forwarder("BuildUnattendedDiscoveryArguments")
            .Invoke(null, [check, settings, environment]));

        Assert.Equal(DotnetStructuralCoverageDiscovery.BuildUnattendedDiscoveryArguments(check, settings, environment), forwarded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrustedForwarder_FilteredAndMissingChecks_MatchesOwner(bool hasFilteredCheck)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-engine-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "tests", "Sample.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Sample.Tests.csproj"), "<Project />");
            AcceptanceManifestCheck[] effective = hasFilteredCheck
                ? [new AcceptanceManifestCheck
                {
                    Name = "infrastructure tests: Alpha", Type = "dotnet-test", Project = Project,
                    Arguments = ["--filter", "FullyQualifiedName~Alpha", "--no-build"],
                    TimeoutMinutes = 7, Runner = "mtp"
                }]
                : [];

            var direct = DotnetStructuralCoverageDiscovery.BuildTrustedCheck(root, Project, effective);
            var forwarded = Assert.IsType<AcceptanceManifestCheck>(Forwarder("BuildTrustedStructuralCoverageCheck")
                .Invoke(null, [root, Project, effective]));

            Assert.Equal(direct.Name, forwarded.Name);
            Assert.Equal(direct.Type, forwarded.Type);
            Assert.Equal(direct.Project, forwarded.Project);
            Assert.Equal(direct.Arguments, forwarded.Arguments);
            Assert.Equal(direct.TimeoutMinutes, forwarded.TimeoutMinutes);
            Assert.Equal(direct.Runner, forwarded.Runner);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoveryLiterals_OccurOnlyInTheOwnerSource()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositorySourceInventory.IsExcludedRelativePath(Path.GetRelativePath(root, path)))
            .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), Text: File.ReadAllText(path)))
            .ToArray();

        foreach (var literal in new[]
                 { "trusted structural discovery: ", "Category=AcceptanceOptIn", "&Category!=AcceptanceOptIn",
                     "has no discovery project", "produced no discovery filter" })
        {
            var source = Assert.Single(sources.Where(source => source.Text.Contains(literal, StringComparison.Ordinal)));
            Assert.Equal("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetStructuralCoverageDiscovery.cs", source.Path);
        }
    }

    private static MethodInfo Forwarder(string name)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(
            Assert.Single(typeof(GoalAcceptanceVerifier).GetMember(name, DeclaredMembers)));
        Assert.True(method.IsStatic);
        return method;
    }
}
