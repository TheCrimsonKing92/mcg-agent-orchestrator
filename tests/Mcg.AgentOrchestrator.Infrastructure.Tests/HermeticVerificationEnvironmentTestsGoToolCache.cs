using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class HermeticVerificationEnvironmentTestsGoToolCache : GoalAcceptanceVerifierTestBase
{
    private static readonly string[] GoVariables =
        ["GOPATH", "GOMODCACHE", "GOCACHE", "GOTOOLCHAIN", "GOENV", "GOFLAGS"];

    [Xunit.Fact]
    public void GateEnvironmentReplacesOperatorGoSettingsWithProfileCaches()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GOFLAGS"] = "-mod=mod",
            ["GOPROXY"] = "off",
            ["GOPATH"] = @"C:\ambient-go",
            ["GOCACHE"] = @"C:\ambient-cache",
            ["GONOSUMDB"] = "example.com"
        };

        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());

        AssertProfileGoValues(environment);
        Assert.DoesNotContain("GOPROXY", environment.Keys);
        Assert.DoesNotContain("GONOSUMDB", environment.Keys);
        Assert.All(environment.Keys, name => Assert.True(
            GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name),
            $"Unexpected verification environment variable survived: {name}"));
    }

    [Xunit.Fact]
    public void GoCachesStayHostSharedAcrossNestingAndLanes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var buildEnvironmentRoot = Path.Combine(Path.GetTempPath(), "mcg-hermetic-go-cache-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());
            AssertProfileGoValues(environment);
            var originalGoValues = GoVariables.ToDictionary(name => name, name => environment[name]);

            GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, Path.GetTempPath());

            Assert.All(GoVariables, name => Assert.Equal(originalGoValues[name], environment[name]));
            AssertPowerShellCache(environment);
            foreach (var lane in new[] { "first", "second" })
            {
                var laneRoot = Path.Combine(buildEnvironmentRoot, lane);
                var laneEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(
                    laneEnvironment, Path.GetTempPath(), laneRoot);

                Assert.Equal(Path.Combine(laneRoot, "dotnet-cli-home"), laneEnvironment["DOTNET_CLI_HOME"]);
                AssertProfileGoValues(laneEnvironment);
                Assert.All(GoVariables, name => Assert.Equal(originalGoValues[name], laneEnvironment[name]));
                AssertPowerShellCache(laneEnvironment);
            }
        }
        finally
        {
            if (Directory.Exists(buildEnvironmentRoot))
            {
                Directory.Delete(buildEnvironmentRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact]
    public void AllowListNamesOnlyTheDeclaredGoVariables()
    {
        foreach (var name in GoVariables)
        {
            Assert.True(GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name));
            Assert.True(GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name.ToLowerInvariant()));
            Assert.True(GoToolCacheEnvironment.IsVariable(name));
            Assert.True(GoToolCacheEnvironment.IsVariable(name.ToLowerInvariant()));
        }
        foreach (var name in new[] { "GOPROXY", "GONOSUMDB", "GOPRIVATE", "GOINSECURE", "GO", "GOCACHE_EXTRA" })
        {
            Assert.False(GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name));
            Assert.False(GoToolCacheEnvironment.IsVariable(name));
            Assert.False(GoToolCacheEnvironment.IsVariable(name.ToLowerInvariant()));
        }
    }

    private static void AssertProfileGoValues(IDictionary<string, string?> environment)
    {
        var profileRoot = Path.Combine(Path.GetTempPath(), "mcg-hvp");
        Assert.Equal(Path.Combine(profileRoot, "go"), environment["GOPATH"]);
        Assert.Equal(Path.Combine(profileRoot, "go", "pkg", "mod"), environment["GOMODCACHE"]);
        Assert.Equal(Path.Combine(profileRoot, "go-build"), environment["GOCACHE"]);
        Assert.Equal("-modcacherw", environment["GOFLAGS"]);
        Assert.Equal("local", environment["GOTOOLCHAIN"]);
        Assert.Equal("off", environment["GOENV"]);
    }

    private static void AssertPowerShellCache(IDictionary<string, string?> environment)
    {
        var expectedCache = Path.Combine(environment["USERPROFILE"]!, "powershell", "ModuleAnalysisCache");
        Assert.Equal(expectedCache, environment["PSModuleAnalysisCachePath"]);
        Assert.True(Directory.Exists(Path.GetDirectoryName(expectedCache)!));
    }
}
