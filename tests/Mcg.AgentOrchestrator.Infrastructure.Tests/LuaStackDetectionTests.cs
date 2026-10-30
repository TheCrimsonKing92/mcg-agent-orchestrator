using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: all filesystem evidence belongs to a unique fixture, without processes.
public sealed class LuaStackDetectionTests
{
    [Theory]
    [InlineData(".busted|spec/a_spec.lua", ".busted")]
    [InlineData("x-1.0-1.rockspec|src/x.lua", "x-1.0-1.rockspec")]
    [InlineData("scripts/a.lua|scripts/b.lua", "lua-majority")]
    [InlineData("spec/unit/a_spec.lua", "spec/unit/a_spec.lua")]
    [InlineData("rockspecs/x-1.0-1.rockspec", "rockspecs/x-1.0-1.rockspec")]
    [InlineData(".luarc.json", ".luarc.json")]
    [InlineData(".luarc.jsonc", ".luarc.jsonc")]
    [InlineData(".luacheckrc", ".luacheckrc")]
    [InlineData("stylua.toml", "stylua.toml")]
    [InlineData(".stylua.toml", ".stylua.toml")]
    [InlineData("selene.toml", "selene.toml")]
    public void Detect_LuaFixtures_ReturnsMarkerAndAdapter(string paths, string marker)
    {
        using var fixture = new Fixture();
        foreach (var path in paths.Split('|'))
            fixture.Write(path);

        var result = ProjectDiscoveryAdapterSelector.Detect(fixture.Root);

        Assert.Equal(ProjectStack.Lua, result.Stack);
        Assert.Equal(marker, result.Marker);
        Assert.Null(result.Refusal);
        Assert.IsType<LuaProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(fixture.Root));
    }

    [Theory]
    [InlineData(2, 3, false)]
    [InlineData(3, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(0, 0, false)]
    public void Detect_Majority_RequiresStrictNonzeroMajority(int lua, int go, bool expected)
    {
        using var fixture = new Fixture();
        for (var index = 0; index < lua; index++)
            fixture.Write($"src/l{index}.lua");
        for (var index = 0; index < go; index++)
            fixture.Write($"src/g{index}.go");
        fixture.Write("README.md");
        fixture.Write("LICENSE");

        var result = ProjectDiscoveryAdapterSelector.Detect(fixture.Root);

        Assert.Equal(expected ? ProjectStack.Lua : (ProjectStack?)null, result.Stack);
        Assert.Equal(expected ? "lua-majority" : null, result.Marker);
        if (!expected)
            Assert.Equal("No stack marker found; looked for root go.mod, package.json, *.sln, *.slnx, and *.csproj, *.fsproj, *.vbproj below the root.", result.Refusal);
    }

    [Theory]
    [InlineData("go.mod")]
    [InlineData("Root.sln")]
    [InlineData("Root.slnx")]
    [InlineData("src/App/App.csproj")]
    [InlineData("src/App/App.fsproj")]
    [InlineData("src/App/App.vbproj")]
    [InlineData("package.json")]
    [InlineData("go.mod|Root.sln")]
    [InlineData("go.mod|package.json")]
    [InlineData("package.json|Root.sln")]
    [InlineData("package.json|src/App/App.csproj")]
    [InlineData("package.json|src/App/App.fsproj")]
    [InlineData("package.json|src/App/App.vbproj")]
    public void Detect_ExistingStackOrRefusal_WithLuaEvidenceIsUnchanged(string paths)
    {
        using var fixture = new Fixture();
        foreach (var path in paths.Split('|'))
            fixture.Write(path);
        var before = ProjectDiscoveryAdapterSelector.Detect(fixture.Root);
        Assert.True(before.Stack is not null || before.Refusal!.StartsWith("Ambiguous stack markers", StringComparison.Ordinal));
        fixture.Write(".busted");
        fixture.Write("spec/a_spec.lua");
        fixture.Write("x-1.0-1.rockspec");
        fixture.Write("scripts/a.lua");

        Assert.Equal(before, ProjectDiscoveryAdapterSelector.Detect(fixture.Root));
    }

    [Theory]
    [InlineData(".busted|spec/a_spec.lua|x.rockspec", ".busted")]
    [InlineData("spec/z_spec.lua|spec/a_spec.lua|x.rockspec", "spec/a_spec.lua")]
    [InlineData("z.rockspec|a.rockspec|rockspecs/a.rockspec|.luarc.json", "a.rockspec")]
    [InlineData("rockspecs/z.rockspec|rockspecs/a.rockspec|.luarc.json", "rockspecs/a.rockspec")]
    [InlineData(".luarc.json|.luarc.jsonc|selene.toml", ".luarc.json")]
    public void Detect_MultipleMarkers_UsesStablePrecedence(string paths, string expected)
    {
        using var fixture = new Fixture();
        foreach (var path in paths.Split('|'))
            fixture.Write(path);

        Assert.Equal(expected, ProjectDiscoveryAdapterSelector.Detect(fixture.Root).Marker);
    }

    [Theory]
    [InlineData("lua_modules")]
    [InlineData(".luarocks")]
    [InlineData("node_modules")]
    [InlineData("vendor")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("CVS")]
    [InlineData(".hidden")]
    [InlineData("generated")]
    public void Detect_ExcludedFiles_DoNotSupplyLuaEvidence(string directory)
    {
        using var fixture = new Fixture();
        fixture.Write($"{directory}/a.lua");
        fixture.Write($"spec/{directory}/a_spec.lua");
        fixture.Write($"{directory}/.busted");

        var result = ProjectDiscoveryAdapterSelector.Detect(fixture.Root, ["generated"]);

        Assert.Null(result.Stack);
        Assert.StartsWith("No stack marker found", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_UntrackedCaseInsensitiveSources_CountOnDisk()
    {
        using var fixture = new Fixture();
        fixture.Write("a.LUA");
        fixture.Write("b.lua");
        fixture.Write("c.PS1");
        // No Git tracking or even Git metadata is needed for the fallback.
        var root = Path.Combine(fixture.Root, "non-git");
        fixture.Write("non-git/a.lua");

        Assert.Equal("lua-majority", ProjectDiscoveryAdapterSelector.Detect(fixture.Root).Marker);
        Assert.Equal("lua-majority", ProjectDiscoveryAdapterSelector.Detect(root).Marker);
    }

    [Fact]
    public void Serialize_Lua_PreservesStackIdentityAndDispatchMapping()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        var model = fixture.Discover();

        Assert.Equal(3, (int)ProjectStack.Lua);
        Assert.Equal("\"lua\"", JsonSerializer.Serialize(ProjectStack.Lua));
        var json = ProjectModelJson.Serialize(model);
        Assert.Equal(ProjectStack.Lua, ProjectModelJson.Deserialize(json).Stack);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("lua", document.RootElement.GetProperty("stack").GetString());
        fixture.Write("home/project-model.json", json);
        Assert.Equal(Toolchain.Unknown, ProjectStackResolver.Resolve(fixture.Root, Path.Combine(fixture.Root, "home")));
    }
}
