using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: checks are in memory and filesystem cases own a unique temporary root.
public sealed class DotnetStructuralCoverageDiscoveryTests
{
    private const string Project = "tests/Sample.Tests/Sample.Tests.csproj";

    [Fact]
    public void Discovery_Vstest_AppendsBuildEnvironmentInOrder()
    {
        var arguments = DotnetStructuralCoverageDiscovery.BuildDiscoveryArguments(
            Check("vstest"), Settings(), Environment());

        Assert.Equal(
            ["dotnet", "test", Project, "--no-build", "--list-tests", "--filter",
                "Category!=HostIntegration", "--artifacts-path", @"C:\repo\artifacts", "-maxcpucount:4"],
            arguments);
    }

    [Fact]
    public void Discovery_Mtp_UsesManagedAssemblyWithoutBuildArguments()
    {
        var settings = Settings();
        var environment = Environment();
        var arguments = DotnetStructuralCoverageDiscovery.BuildDiscoveryArguments(
            Check("mtp"), settings, environment);

        Assert.Equal(
            ["dotnet", settings.ResolveMtpInvocation(Project).ResolveManagedAssemblyPath(environment),
                "--no-ansi", "--progress", "off", "--list-tests", "json",
                "--filter-not-trait", "Category=HostIntegration"],
            arguments);
        foreach (var argument in environment.Arguments)
            Assert.DoesNotContain(argument, arguments);
    }

    [Fact]
    public void Unattended_Vstest_ExtendsTheExistingFilter()
    {
        Assert.Equal(
            ["dotnet", "test", Project, "--no-build", "--list-tests", "--filter",
                "Category!=HostIntegration&Category!=AcceptanceOptIn", "--artifacts-path",
                @"C:\repo\artifacts", "-maxcpucount:4"],
            DotnetStructuralCoverageDiscovery.BuildUnattendedDiscoveryArguments(
                Check("vstest"), Settings(), Environment()));
    }

    [Fact]
    public void Unattended_Mtp_AppendsTheOptInExclusion()
    {
        var settings = Settings();
        var environment = Environment();

        Assert.Equal(
            ["dotnet", settings.ResolveMtpInvocation(Project).ResolveManagedAssemblyPath(environment),
                "--no-ansi", "--progress", "off", "--list-tests", "json",
                "--filter-not-trait", "Category=HostIntegration",
                "--filter-not-trait", "Category=AcceptanceOptIn"],
            DotnetStructuralCoverageDiscovery.BuildUnattendedDiscoveryArguments(
                Check("mtp"), settings, environment));
    }

    [Fact]
    public void Discovery_MissingProject_FailsWithTheDiscoveryDiagnostic()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            DotnetStructuralCoverageDiscovery.BuildDiscoveryArguments(
                new AcceptanceManifestCheck { Name = "missing", Type = "dotnet-test", Project = null },
                Settings(), Environment()));

        Assert.Contains("has no discovery project", exception.Message);
    }

    [Fact]
    public void Discovery_UnconfiguredMtpProject_FailsLoudly()
    {
        Assert.Throws<InvalidDataException>(() =>
            DotnetStructuralCoverageDiscovery.BuildDiscoveryArguments(
                new AcceptanceManifestCheck
                {
                    Type = "dotnet-test", Project = "tests/Other.Tests/Other.Tests.csproj", Runner = "mtp"
                }, Settings(), Environment()));
    }

    [Fact]
    public void RemoveFilter_MixedArguments_KeepsNonFilterArgumentsInOrder()
    {
        Assert.Equal(
            ["--no-build", "--logger", "trx"],
            DotnetStructuralCoverageDiscovery.RemoveTestFilter(
                ["--no-build", "--filter", "X", "--logger", "trx"]));
        Assert.Empty(DotnetStructuralCoverageDiscovery.RemoveTestFilter(["--FILTER", "X"]));
    }

    [Fact]
    public void BroadCheck_RequiresAnUnfilteredDotnetTestProject()
    {
        Assert.True(DotnetStructuralCoverageDiscovery.IsBroadTestProjectCheck(Check("vstest")));
        AcceptanceManifestCheck[] excluded =
        [
            new() { Type = "command", Project = Project },
            new() { Type = "dotnet-test", Project = "tests/Sample.sln" },
            new() { Type = "dotnet-test", Project = null },
            new() { Type = "dotnet-test", Project = Project, Arguments = ["--filter", "X"] }
        ];
        foreach (var check in excluded)
            Assert.False(DotnetStructuralCoverageDiscovery.IsBroadTestProjectCheck(check));
    }

    [Theory]
    [InlineData(Project)]
    [InlineData(@"tests\Sample.Tests\Sample.Tests.csproj")]
    public void TrustedCheck_BroadMatch_PreservesInstance(string declaredProject)
    {
        using var fixture = new ProjectRoot();
        var broad = new AcceptanceManifestCheck { Type = "dotnet-test", Project = declaredProject };

        Assert.Same(broad, DotnetStructuralCoverageDiscovery.BuildTrustedCheck(
            fixture.Root, Project, [broad]));
    }

    [Fact]
    public void TrustedCheck_FilteredMatch_CopiesMetadataWithoutTheFilter()
    {
        using var fixture = new ProjectRoot();
        var filtered = new AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Alpha", Type = "dotnet-test", Project = Project,
            Arguments = ["--filter", "FullyQualifiedName~Alpha", "--no-build"],
            TimeoutMinutes = 7, Runner = "mtp"
        };

        var trusted = DotnetStructuralCoverageDiscovery.BuildTrustedCheck(fixture.Root, Project, [filtered]);

        Assert.NotSame(filtered, trusted);
        Assert.Equal("infrastructure tests", trusted.Name);
        Assert.Equal("dotnet-test", trusted.Type);
        Assert.Equal(Project, trusted.Project);
        Assert.Equal(["--no-build"], trusted.Arguments);
        Assert.Equal(7, trusted.TimeoutMinutes);
        Assert.Equal("mtp", trusted.Runner);
    }

    [Fact]
    public void TrustedCheck_NoMatch_UsesProjectNameAndResolvedRunner()
    {
        using var fixture = new ProjectRoot();

        var trusted = DotnetStructuralCoverageDiscovery.BuildTrustedCheck(fixture.Root, Project, []);

        Assert.Equal("trusted structural discovery: Sample.Tests", trusted.Name);
        Assert.Equal("dotnet-test", trusted.Type);
        Assert.Equal(Project, trusted.Project);
        Assert.Equal("mtp", trusted.Runner);
    }

    [Fact]
    public void Declarations_AddsUmbrellaAndPreservesVerifierParity()
    {
        using var fixture = new ProjectRoot();
        var sentinel = new AcceptanceManifestCheck { Name = "sentinel", Type = "command" };
        var umbrella = Check("mtp");

        var direct = DotnetStructuralCoverageDiscovery.EnsureDeclarations([sentinel], [umbrella], fixture.Root);
        var forwarded = GoalAcceptanceVerifier.EnsureTrustedStructuralCoverageDeclarations(
            [sentinel], [umbrella], fixture.Root);

        foreach (var plan in new[] { direct, forwarded })
        {
            Assert.Equal(2, plan.Checks.Count);
            Assert.Same(sentinel, plan.Checks[0]);
            Assert.Same(umbrella, plan.Checks[1]);
            Assert.Equal(["tests/Extra.Tests/Extra.Tests.csproj"], plan.UndeclaredProjects);
        }
    }

    private static AcceptanceManifestCheck Check(string runner) =>
        new() { Name = "sample tests", Type = "dotnet-test", Project = Project, Runner = runner };

    private static DotnetBuildEnvironment Environment() =>
        new("lease", "root", @"C:\repo\artifacts", "lock",
            ["--artifacts-path", @"C:\repo\artifacts", "-maxcpucount:4"], "owner");

    private static AcceptanceGateEngineSettings Settings() => new()
    {
        MtpInvocations =
        [new AcceptanceMtpInvocation { Project = Project, ExecutablePathTemplate = "bin/{projectName}{executableExtension}" }]
    };

    private sealed class ProjectRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-engine-tests", Guid.NewGuid().ToString("N"));

        public ProjectRoot()
        {
            foreach (var name in new[] { "Sample.Tests", "Extra.Tests" })
            {
                var directory = Path.Combine(Root, "tests", name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, name + ".csproj"), "<Project />");
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
