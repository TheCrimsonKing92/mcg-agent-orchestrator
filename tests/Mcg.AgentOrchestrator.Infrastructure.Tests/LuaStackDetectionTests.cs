using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: all filesystem evidence belongs to a unique fixture, without processes.
public sealed class LuaStackDetectionTests
{
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
