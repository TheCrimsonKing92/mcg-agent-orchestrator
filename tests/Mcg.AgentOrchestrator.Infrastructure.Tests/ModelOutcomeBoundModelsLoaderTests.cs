using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static InfrastructureTestSupport;

// Parallel-safe: each test owns and cleans a private fixture directory.
public sealed class ModelOutcomeBoundModelsLoaderTests
{
    [Fact]
    public void LoadsBothCatalogsWithoutWritingEitherFile()
    {
        using var fixture = new CatalogFixture();
        var agentBytes = SHA256.HashData(File.ReadAllBytes(fixture.AgentsPath));
        var functionBytes = SHA256.HashData(File.ReadAllBytes(fixture.FunctionsPath));

        var bound = ModelOutcomeBoundModels.Load(fixture.AgentsPath, fixture.FunctionsPath);

        Assert.True(bound.IsAvailable);
        Assert.True(bound.ModelNames.SetEquals(new[] { "routine", "complex", "function", "alias" }));
        Assert.Equal(agentBytes, SHA256.HashData(File.ReadAllBytes(fixture.AgentsPath)));
        Assert.Equal(functionBytes, SHA256.HashData(File.ReadAllBytes(fixture.FunctionsPath)));
        Assert.Equal(new[] { "agents.json", "model-functions.json" },
            Directory.GetFiles(fixture.Root).Select(Path.GetFileName).OrderBy(name => name).ToArray());
    }

    [Theory]
    [InlineData("agents.json")]
    [InlineData("model-functions.json")]
    public void MissingEitherCatalogMakesEntireSetUnavailable(string fileName)
    {
        using var fixture = new CatalogFixture();
        File.Delete(Path.Combine(fixture.Root, fileName));

        var bound = ModelOutcomeBoundModels.Load(fixture.AgentsPath, fixture.FunctionsPath);

        Assert.False(bound.IsAvailable);
        Assert.Empty(bound.ModelNames);
        Assert.Contains(fileName + " not found at ", bound.UnavailableReason);
    }

    [Theory]
    [InlineData("agents.json", "{broken")]
    [InlineData("model-functions.json", "{broken")]
    [InlineData("agents.json", "{}")]
    [InlineData("model-functions.json", "{}")]
    [InlineData("agents.json", "{\"Agents\":[null]}")]
    [InlineData("model-functions.json", "{\"Bindings\":[null]}")]
    public void InvalidPrimaryIsUnavailableEvenWhenBackupIsValid(string fileName, string invalidJson)
    {
        using var fixture = new CatalogFixture();
        var path = Path.Combine(fixture.Root, fileName);
        File.Copy(path, path + ".bak");
        File.WriteAllText(path, invalidJson);

        var bound = ModelOutcomeBoundModels.Load(fixture.AgentsPath, fixture.FunctionsPath);

        Assert.False(bound.IsAvailable);
        Assert.Empty(bound.ModelNames);
        Assert.Contains(fileName + " unreadable at ", bound.UnavailableReason);
        Assert.Equal(invalidJson, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("agents.json")]
    [InlineData("model-functions.json")]
    public void UnreadableCatalogMakesEntireSetUnavailable(string fileName)
    {
        using var fixture = new CatalogFixture();
        using var exclusiveFile = new FileStream(Path.Combine(fixture.Root, fileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var bound = ModelOutcomeBoundModels.Load(fixture.AgentsPath, fixture.FunctionsPath);

        Assert.False(bound.IsAvailable);
        Assert.Empty(bound.ModelNames);
        Assert.Contains(fileName + " unreadable at ", bound.UnavailableReason);
    }

    [Fact]
    public void ValidEmptyAgentCatalogDoesNotBindDefaultsOrBackupModels()
    {
        using var fixture = new CatalogFixture();
        File.Copy(fixture.AgentsPath, fixture.AgentsPath + ".bak");
        File.WriteAllText(fixture.AgentsPath, "{\"Agents\":[]}");

        var bound = ModelOutcomeBoundModels.Load(fixture.AgentsPath, fixture.FunctionsPath);

        Assert.True(bound.IsAvailable);
        Assert.True(bound.ModelNames.SetEquals(new[] { "function", "alias" }));
        Assert.False(bound.Contains("routine"));
    }

    [Fact]
    public void TwoValidEmptyCatalogsAreAvailableAndEmpty()
    {
        using var fixture = new CatalogFixture();
        File.WriteAllText(fixture.AgentsPath, "{\"Agents\":[]}");
        File.WriteAllText(fixture.FunctionsPath, "{\"Bindings\":[]}");

        var bound = ModelOutcomeBoundModels.Load(fixture.AgentsPath, fixture.FunctionsPath);

        Assert.True(bound.IsAvailable);
        Assert.Empty(bound.ModelNames);
        Assert.Null(bound.UnavailableReason);
    }

    private sealed class CatalogFixture : IDisposable
    {
        public string Root { get; } = CreateTempDirectory();
        public string AgentsPath => Path.Combine(Root, "agents.json");
        public string FunctionsPath => Path.Combine(Root, "model-functions.json");

        public CatalogFixture()
        {
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
            var agents = new AgentCatalog(
            [
                new(new AgentId("fixture"), "fixture", AgentRole.Developer, Model("routine"),
                    ComplexModel: Model("complex"), IsProviderRoutingConstrained: true)
            ]);
            var functions = new ModelFunctionCatalog(
            [
                new("purpose", ModelLane.Local, Model("function"), Subscription: new("cli", "alias"))
            ]);
            File.WriteAllText(AgentsPath, JsonSerializer.Serialize(agents, options));
            File.WriteAllText(FunctionsPath, JsonSerializer.Serialize(functions, options));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static ModelProfile Model(string name) => new("fixture-provider", name,
            ModelCapability.Text, SubscriptionMode.ApiKey);
    }
}
