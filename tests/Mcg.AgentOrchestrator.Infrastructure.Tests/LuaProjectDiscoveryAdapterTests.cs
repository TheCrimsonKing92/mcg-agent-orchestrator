using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture owns its directory; discovery executes no tools.
public sealed class LuaProjectDiscoveryAdapterTests
{
    [Fact]
    public void Discover_BustedFileAndSpec_RecordsBustedAndNoQuestion()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        fixture.Write("spec/a_spec.lua");

        var model = new LuaProjectDiscoveryAdapter().Discover(fixture.Root, measurer: new RejectingMeasurer());

        Assert.Equal(ProjectStack.Lua, model.Stack);
        Assert.Equal(".", model.RepositoryRoot);
        Assert.Empty(model.OwnerQuestions);
        Assert.Empty(model.Units);
        Assert.Empty(model.Commands);
        Assert.Empty(model.Measurements);
        Assert.Empty(model.Dependencies);
        Assert.Empty(model.TestSetups);
        var framework = Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-test-framework").Value;
        Assert.Equal("busted", framework.Value);
        Assert.Equal(FactConfidence.High, framework.Confidence);
        Assert.Equal(".busted", framework.Source.Path);
        Assert.Equal(1, framework.Source.Line);
        Assert.Equal("crosswalk-test .", Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-test-command").Value.Value);
        Assert.Equal("main", model.IntegrationBranch?.Value);
        Assert.Equal(FactConfidence.High, model.IntegrationBranch?.Confidence);
        Assert.DoesNotContain(model.EnvironmentNeeds, need => need.Name == "lua-install-command");
    }

    [Theory]
    [InlineData(".busted")]
    [InlineData("spec/unit/a_spec.lua")]
    public void Discover_EitherBustedEvidence_DeterminesFramework(string path)
    {
        using var fixture = new Fixture();
        fixture.Write(path);

        var model = fixture.Discover();

        Assert.Empty(model.OwnerQuestions);
        var framework = Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-test-framework").Value;
        Assert.Equal("busted", framework.Value);
        Assert.Equal(path, framework.Source.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discover_NoMatchingSpecs_AsksExactlyOneBustedQuestion(bool hasHelper)
    {
        using var fixture = new Fixture();
        fixture.Write("scripts/a.lua");
        fixture.Write("scripts/b.lua");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "spec"));
        if (hasHelper)
            fixture.Write("spec/helper.lua");

        var model = fixture.Discover();

        var question = Assert.Single(model.OwnerQuestions);
        Assert.Equal("environment/lua-test-framework", question.FactKey);
        Assert.Equal("environment/lua-test-framework: No busted specs were found. Use busted through crosswalk as the test framework?", question.Question);
        var framework = Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-test-framework").Value;
        Assert.Equal("undetermined", framework.Value);
        Assert.Equal(FactConfidence.Low, framework.Confidence);
        Assert.Null(framework.Source.Path);
        Assert.Null(framework.Source.Line);
        Assert.False(string.IsNullOrWhiteSpace(framework.Source.MeasurementReference));
        Assert.Equal(framework.Source, question.Source);
        Assert.Equal("crosswalk-test .", Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-test-command").Value.Value);
        Assert.DoesNotContain(model.EnvironmentNeeds, need => need.Name == "lua-install-command");
    }

    [Theory]
    [InlineData("x-1.0-1.rockspec")]
    [InlineData("rockspecs/x-1.0-1.rockspec")]
    public void Discover_Rockspec_ProposesGatedInstall(string path)
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        fixture.Write(path);

        var model = fixture.Discover();

        var install = Assert.Single(model.EnvironmentNeeds, need => need.Name == "lua-install-command").Value;
        Assert.Equal("luarocks make --local", install.Value);
        Assert.Equal(FactConfidence.Low, install.Confidence);
        Assert.Equal(path, install.Source.Path);
        var question = Assert.Single(model.OwnerQuestions);
        Assert.Equal("environment/lua-luarocks-install", question.FactKey);
        Assert.Contains("luarocks make --local", question.Question, StringComparison.Ordinal);
        Assert.Contains("host's LuaRocks tree", question.Question, StringComparison.Ordinal);
        Assert.Empty(model.Commands);
        Assert.Empty(model.Measurements);
    }

    [Fact]
    public void Discover_RockspecWithoutSpecs_AsksIndependentQuestions()
    {
        using var fixture = new Fixture();
        fixture.Write("x.rockspec");

        var model = fixture.Discover();

        Assert.Equal(new[] { "environment/lua-luarocks-install", "environment/lua-test-framework" },
            model.OwnerQuestions.Select(question => question.FactKey));
    }

    [Fact]
    public void Discover_ExcludedSpecs_DoNotDetermineFramework()
    {
        using var fixture = new Fixture();
        fixture.Write("spec/generated/a_spec.lua");

        var model = new LuaProjectDiscoveryAdapter().Discover(fixture.Root, ["generated"]);

        Assert.Equal("environment/lua-test-framework", Assert.Single(model.OwnerQuestions).FactKey);
    }

    private sealed class RejectingMeasurer : IUnitCommandMeasurer
    {
        public UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command) =>
            throw new InvalidOperationException("Lua discovery must never execute commands.");
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lua-discovery-");
        internal string Root => _root.FullName;

        internal Fixture() => Write(".git/refs/remotes/origin/HEAD", "ref: refs/remotes/origin/main\n");

        internal void Write(string relativePath, string content = "")
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        internal ProjectModel Discover() => new LuaProjectDiscoveryAdapter().Discover(Root);

        internal static OwnerAnswer[] Answers(params OwnerAnswer[] additional) =>
        [
            new("engine/timeouts/defaultMinutes", "5", "fixture", DateTimeOffset.UnixEpoch),
            new("engine/timeouts/buildServerShutdownMinutes", "3", "fixture", DateTimeOffset.UnixEpoch),
            new("engine/timeouts/discoveryMinutes", "4", "fixture", DateTimeOffset.UnixEpoch),
            .. additional
        ];

        public void Dispose() => _root.Delete(recursive: true);
    }
}
