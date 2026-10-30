using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: each test owns its registry/data roots and uses AsyncLocal console capture.
public sealed class ProjectDeliveryRegistrationTests
{
    [Fact]
    public void LegacyRegistration_LoadsAndShowsDefaultsWithoutAddingDeliveryFieldsOnSave()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        Directory.CreateDirectory(registry.RegistryDirectory);
        File.WriteAllText(registry.RegistryPath, JsonSerializer.Serialize(new
        {
            Projects = new[] { new { Name = "legacy", RootDirectory = source.Root } }
        }));
        var before = File.ReadAllBytes(registry.RegistryPath);

        var project = registry.GetRequiredProject("legacy");

        Assert.Equal("direct", project.Delivery);
        Assert.Null(project.DeliveryDecider);
        Assert.Null(project.DeliveryAnswer);
        Assert.Null(project.DeliveryDeciderAnswer);
        var output = CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "legacy"], registry, home.Root, null)));
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("Delivery: direct (source: default)", lines);
        Assert.Contains("Delivery decider: unset (repository owner)", lines);
        Assert.Contains("Delivery probe: not recorded", lines);
        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));

        // Force a registry save through an unrelated setting; defaults must remain implicit.
        registry.SetUseLearnedIntegrationBranch("legacy", true);
        using var saved = JsonDocument.Parse(File.ReadAllText(registry.RegistryPath));
        var entry = saved.RootElement.GetProperty("Projects")[0];
        Assert.True(entry.GetProperty("UseLearnedIntegrationBranch").GetBoolean());
        Assert.DoesNotContain(entry.EnumerateObject(), property => property.Name.StartsWith("Delivery", StringComparison.Ordinal));
        var reopened = new OrchestratorProjectRegistry(registry.RegistryDirectory, registry.DataRootDirectory)
            .GetRequiredProject("legacy");
        Assert.Equal("direct", reopened.Delivery);
        Assert.Null(reopened.DeliveryDecider);
    }
}
