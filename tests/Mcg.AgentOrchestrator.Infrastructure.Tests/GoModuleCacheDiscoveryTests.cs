using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns a unique copy of the Go discovery fixture; no Go process runs.
public sealed class GoModuleCacheDiscoveryTests
{
    [Theory]
    [InlineData("go-path")]
    [InlineData("downloads")]
    public void LocalCache_ExcludesModulesAndSources_PreservesOrdinaryNestedModule(string cache)
    {
        using var fixture = new ProjectOnboardingFixture("go-module");
        ProjectOnboardingFixture.SeedRemoteDefault(fixture.Root);
        var adapter = new GoProjectDiscoveryAdapter();
        var baseline = adapter.Discover(fixture.Root);
        Assert.Equal("units/tools/module", Assert.Single(baseline.OwnerQuestions).FactKey);
        Assert.NotEmpty(baseline.Units);
        var cachePrefix = cache + "/pkg/mod/";
        Write(cachePrefix + "example.com/m@v1.0.0/go.mod", "module example.com/m\ngo 1.23\n");
        Write(cachePrefix + "example.com/m@v1.0.0/m.go", "package m\n");
        // A package directly under pkg/mod also detects source-file enumeration leakage.
        Write(cachePrefix + "loose/loose.go", "package loose\n");
        Assert.True(File.Exists(Path.Combine(fixture.Root, cachePrefix + "example.com/m@v1.0.0/go.mod")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "tools/go.mod")));

        var model = adapter.Discover(fixture.Root);

        Assert.DoesNotContain(model.Units, unit => unit.Id.StartsWith(cachePrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(model.OwnerQuestions, question =>
            question.FactKey.Contains(cachePrefix, StringComparison.Ordinal));
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "units/tools/module");
        Assert.Equal(ProjectModelJson.Serialize(baseline), ProjectModelJson.Serialize(model));

        void Write(string relative, string text)
        {
            var path = Path.Combine(fixture.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }
}
