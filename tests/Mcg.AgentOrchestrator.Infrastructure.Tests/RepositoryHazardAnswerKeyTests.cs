using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reads the verified checkout and manifest without modifying either.
public sealed class RepositoryHazardAnswerKeyTests
{
    [Fact]
    public void InfrastructureCollections_DiscoveryKeys_AppearInManifestAnswerKey()
    {
        var root = VerifiedRepositoryRoot.Find();
        var model = new DotnetProjectDiscoveryAdapter().Discover(root, RepositorySourceInventory.ExcludedDirectoryNames);
        const string unit = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        var hazards = model.Hazards.Where(hazard => hazard.UnitId == unit).ToArray();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config/acceptance-manifest.json")));
        var resourceKeys = ResourceKeys(manifest.RootElement).ToHashSet(StringComparer.Ordinal);
        foreach (var key in new[] { "xunit:EnvMutation", "xunit:DotnetBuildSlots" })
        {
            var hazard = Assert.Single(hazards.Where(hazard => hazard.IsolationKey.Value == key));
            Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
            Assert.Contains(key, resourceKeys);
            Assert.Equal("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AssemblyInfo.cs", hazard.IsolationKey.Source.Path);
            var source = hazard.IsolationKey.Source;
            Assert.Contains("CollectionDefinition", File.ReadAllLines(Path.Combine(root, source.Path!))[source.Line!.Value - 1]);
        }
        Assert.DoesNotContain(hazards, hazard => hazard.IsolationKey.Source.Path!.Contains("/ProviderEnvironment/", StringComparison.Ordinal));
    }

    private static IEnumerable<string> ResourceKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
                if (property.Name == "exclusiveResourceKeys" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var key in property.Value.EnumerateArray())
                        yield return key.GetString()!;
                }
                else
                {
                    foreach (var key in ResourceKeys(property.Value))
                        yield return key;
                }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (var key in ResourceKeys(item))
                    yield return key;
    }
}
