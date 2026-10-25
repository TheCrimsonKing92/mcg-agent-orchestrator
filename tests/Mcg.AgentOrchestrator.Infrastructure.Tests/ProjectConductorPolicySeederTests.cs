using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its template, destination and registry directories.
public sealed class ProjectConductorPolicySeederTests
{
    [Fact]
    public void Seed_CommittedTemplate_WritesPortablePolicy()
    {
        using var fixture = new Fixture();
        var result = ProjectConductorPolicySeeder.Seed(fixture.Template, fixture.Destination);

        Assert.Equal(ProjectConductorPolicySeeder.Outcome.Seeded, result.Outcome);
        Assert.Equal("ProjectPermissive", result.PolicyName);
        Assert.Equal(fixture.PolicyPath, result.WrittenPath);
        Assert.Empty(result.DroppedKeys);
        var policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(fixture.Destination));
        Assert.Equal("ProjectPermissive", policy.Name);
        Assert.Equal(ConductorTransitionDecision.Auto,
            policy.GetTransitionDecision(GoalLifecycleState.AcceptanceFailed));
        Assert.False(policy.CascadeTesterCheapFirst);
        Assert.Equal(1, policy.AcceptanceWidth);
        Assert.Equal(2, policy.MaxCriterionRetries);
        Assert.Equal(1, policy.PlannerSampleCount);
        Assert.Equal(ChangeRiskTier.Broad, policy.AutoPromoteRiskThreshold);
        Assert.Equal([fixture.PolicyPath], Directory.GetFileSystemEntries(fixture.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Seed_ExistingPolicy_PreservesBytesWithoutReadingTemplate(bool invalid)
    {
        using var fixture = new Fixture();
        var content = invalid ? "{ invalid JSON\r\n" : ConductorAutonomyPolicy.Manual.ToJson();
        File.WriteAllText(fixture.PolicyPath, content);
        var before = File.ReadAllBytes(fixture.PolicyPath);
        File.Delete(fixture.Template);

        var result = ProjectConductorPolicySeeder.Seed(fixture.Template, fixture.Destination);

        Assert.Equal(ProjectConductorPolicySeeder.Outcome.AlreadyPresent, result.Outcome);
        Assert.Null(result.PolicyName);
        Assert.Null(result.WrittenPath);
        Assert.Empty(result.DroppedKeys);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
        Assert.Equal([fixture.PolicyPath], Directory.GetFileSystemEntries(fixture.Destination));
    }

    [Fact]
    public void Seed_HostSettings_DropsKeysAndUsesConservativeWorkerCount()
    {
        using var fixture = new Fixture();
        var template = JsonNode.Parse(File.ReadAllText(fixture.Template))!.AsObject();
        template["maxConcurrentPaidWorkers"] = 9;
        template["maxTotalBudget"] = 50;
        template["acceptanceAttemptBelowNormalPriority"] = false;
        template["boardFillMaxDraftsPerDay"] = 6;
        File.WriteAllText(fixture.Template, template.ToJsonString());

        var result = ProjectConductorPolicySeeder.Seed(fixture.Template, fixture.Destination);

        Assert.Equal(ProjectConductorPolicySeeder.Outcome.Seeded, result.Outcome);
        Assert.Equal(new[] { "acceptanceAttemptBelowNormalPriority", "boardFillMaxDraftsPerDay",
            "maxConcurrentPaidWorkers", "maxTotalBudget" }, result.DroppedKeys);
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.PolicyPath));
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "acceptanceWidth", "autoPromoteRiskThreshold", "cascadeTesterCheapFirst",
            "maxConcurrentPaidWorkers", "maxCriterionRetries", "name", "plannerSampleCount",
            "transitionMap" }, keys);
        Assert.Equal(ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers,
            document.RootElement.GetProperty("maxConcurrentPaidWorkers").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Seed_MissingTemplate_WritesNothingAndPreservesDefault(bool missingDirectory)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Template);
        if (missingDirectory)
            Directory.Delete(Path.GetDirectoryName(fixture.Template)!);
        var untouched = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(Path.Combine(fixture.Root, "untouched")));

        var result = ProjectConductorPolicySeeder.Seed(fixture.Template, fixture.Destination);

        Assert.Equal(ProjectConductorPolicySeeder.Outcome.TemplateMissing, result.Outcome);
        Assert.Null(result.WrittenPath);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Destination));
        Assert.Same(untouched, ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(fixture.Destination)));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"bad\",\"transitionMap\":{\"Created\":42}}")]
    public void Seed_InvalidTemplate_WritesNothingAndPreservesDefault(string template)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Template, template);
        var untouched = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(Path.Combine(fixture.Root, "untouched")));

        var result = ProjectConductorPolicySeeder.Seed(fixture.Template, fixture.Destination);

        Assert.Equal(ProjectConductorPolicySeeder.Outcome.TemplateInvalid, result.Outcome);
        Assert.Null(result.WrittenPath);
        Assert.NotEmpty(result.Detail);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Destination));
        Assert.Same(untouched, ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(fixture.Destination)));
    }

    [Fact]
    public void Seed_ValidationFailure_DoesNotCreateDestinationDirectory()
    {
        using var fixture = new Fixture();
        var template = JsonNode.Parse(File.ReadAllText(fixture.Template))!.AsObject();
        template["acceptanceWidth"] = 0;
        File.WriteAllText(fixture.Template, template.ToJsonString());
        var absent = Path.Combine(fixture.Root, "absent");

        var result = ProjectConductorPolicySeeder.Seed(fixture.Template, absent);

        Assert.Equal(ProjectConductorPolicySeeder.Outcome.TemplateInvalid, result.Outcome);
        Assert.False(Directory.Exists(absent));
    }

    [Fact]
    public void Seed_PublicationFailure_PropagatesAndRemovesTemporaryFile()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PolicyPath);

        var failure = Record.Exception(() =>
            ProjectConductorPolicySeeder.Seed(fixture.Template, fixture.Destination));
        Assert.True(failure is IOException or UnauthorizedAccessException,
            $"Expected a publication failure, got {failure?.GetType().Name ?? "no exception"}.");

        Assert.True(Directory.Exists(fixture.PolicyPath));
        Assert.Empty(Directory.GetFiles(fixture.Destination));
    }

    [Fact]
    public void TemplatePathFor_ProjectScope_UsesHomeInstallRootOnly()
    {
        using var fixture = new Fixture();
        var home = OrchestratorHome.ForInstallRoot(Path.Combine(fixture.Root, "install"));
        var target = Path.Combine(fixture.Root, "target");
        var data = Path.Combine(fixture.Root, "data");
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "registry"), data);
        var workspace = OrchestratorWorkspace.ForDirectory(target, null, null, registry);

        Assert.Null(ProjectConductorPolicySeeder.TemplatePathFor(workspace, home));
        var project = OrchestratorWorkspace.ForProject("alpha", target, dataRootDirectory: data);
        Assert.Equal(Path.Combine(home.InstallRootDirectory, "config",
            "project-conductor-policy.template.json"),
            ProjectConductorPolicySeeder.TemplatePathFor(project, home));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Template => ProjectConductorPolicySeeder.TemplatePath(Root);
        public string Destination => Path.Combine(Root, "destination");
        public string PolicyPath => Path.Combine(Destination, "conductor-policy.json");

        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Template)!);
            Directory.CreateDirectory(Destination);
            var committed = ProjectConductorPolicySeeder.TemplatePath(
                InfrastructureTestSupport.FindRepositoryRoot());
            Assert.True(File.Exists(committed), $"Committed template missing: {committed}");
            File.Copy(committed, Template);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
