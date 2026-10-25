using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its SQLite stores, registry and routed console capture.
public sealed class ProjectPolicySeedingStartupTests : HostCapacityBoundTestBase
{
    [Fact]
    public void Create_CommittedTemplate_LoopResolvesProjectPermissiveFromFile()
    {
        using var fixture = new Fixture();
        var output = InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, fixture.Create()));
        var workspace = fixture.Registry.GetRequiredProject("alpha").ResolveWorkspace();

        Assert.Contains($"Conductor policy: seeded ProjectPermissive from {fixture.Template}", output);
        Assert.True(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json")));
        using var error = new StringWriter();
        var resolution = CliConductLoopStartup.PrepareConductLoopPolicy(
            workspace.OrchestratorDirectory, null, error);

        Assert.NotNull(resolution);
        Assert.Equal("ProjectPermissive", resolution.Policy.Name);
        Assert.StartsWith("file:", resolution.Source);
        Assert.Equal(ConductorTransitionDecision.Auto,
            resolution.Policy.GetTransitionDecision(GoalLifecycleState.AcceptanceFailed));
    }

    [Fact]
    public void Prepare_MissingPolicy_SeedsOnceAndPreservesBytesOnSecondCall()
    {
        using var fixture = new Fixture();
        using var error = new StringWriter();
        using var output = new StringWriter();
        var directory = Path.Combine(fixture.Root, "existing-project");
        Directory.CreateDirectory(directory);
        var policyPath = Path.Combine(directory, "conductor-policy.json");
        Assert.False(File.Exists(policyPath));

        var first = CliConductLoopStartup.PrepareConductLoopPolicy(directory, null, error,
            storeSetup: _ => [], projectPolicyTemplatePath: fixture.Template, seedOutput: output);

        Assert.NotNull(first);
        Assert.Equal("ProjectPermissive", first.Policy.Name);
        Assert.Equal($"file:{policyPath}", first.Source);
        Assert.Equal($"[conduct] POLICY_SEEDED policy=ProjectPermissive template={fixture.Template} " +
            $"path={policyPath} dropped=none{Environment.NewLine}", output.ToString());
        var bytes = File.ReadAllBytes(policyPath);
        output.GetStringBuilder().Clear();
        File.Delete(fixture.Template);

        var second = CliConductLoopStartup.PrepareConductLoopPolicy(directory, null, error,
            storeSetup: _ => [], projectPolicyTemplatePath: fixture.Template, seedOutput: output);

        Assert.NotNull(second);
        Assert.Equal(first.Source, second.Source);
        Assert.Equal("ProjectPermissive", second.Policy.Name);
        Assert.Equal("", output.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(policyPath));
        Assert.Equal([policyPath], Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void Prepare_NullTemplate_PreservesDefaultResolutionAndWritesNothing()
    {
        using var fixture = new Fixture();
        using var error = new StringWriter();
        using var output = new StringWriter();
        var directory = Path.Combine(fixture.Root, "without-template-path");
        Directory.CreateDirectory(directory);
        var expected = CliConductLoopStartup.ResolveConductorPolicy(null, directory);

        var actual = CliConductLoopStartup.PrepareConductLoopPolicy(directory, null, error,
            storeSetup: _ => [], projectPolicyTemplatePath: null, seedOutput: output);

        Assert.NotNull(actual);
        Assert.Same(expected.Policy, actual.Policy);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prepare_UnavailableTemplate_LogsOneSkipLineAndUsesDefault(bool missing)
    {
        using var fixture = new Fixture();
        if (missing)
            File.Delete(fixture.Template);
        else
        {
            var template = JsonNode.Parse(File.ReadAllText(fixture.Template))!.AsObject();
            template["acceptanceWidth"] = 0;
            File.WriteAllText(fixture.Template, template.ToJsonString());
        }
        using var error = new StringWriter();
        using var output = new StringWriter();
        var directory = Path.Combine(fixture.Root, "unseeded-project");
        Directory.CreateDirectory(directory);

        var result = CliConductLoopStartup.PrepareConductLoopPolicy(directory, null, error,
            storeSetup: _ => [], projectPolicyTemplatePath: fixture.Template, seedOutput: output);

        Assert.NotNull(result);
        Assert.Same(ConductorAutonomyPolicy.Conservative, result.Policy);
        Assert.Equal("default", result.Source);
        var line = Assert.Single(output.ToString().Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries));
        var reason = missing ? "template-missing" : "template-invalid";
        Assert.StartsWith($"[conduct] POLICY_SEED_SKIPPED reason={reason} " +
            $"template={fixture.Template} detail=", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_UnavailableTemplate_SucceedsWithoutPolicy(bool missing)
    {
        using var fixture = new Fixture();
        if (missing)
            File.Delete(fixture.Template);
        else
            File.WriteAllText(fixture.Template, "{ invalid");

        var output = InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, fixture.Create()));

        var workspace = fixture.Registry.GetRequiredProject("alpha").ResolveWorkspace();
        var outcome = missing ? "TemplateMissing" : "TemplateInvalid";
        Assert.Contains($"Conductor policy: not seeded ({outcome}:", output);
        Assert.Contains("); built-in Conservative applies", output);
        Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json")));
        Assert.Equal("default", CliConductLoopStartup.ResolveConductorPolicy(
            null, workspace.OrchestratorDirectory).Source);
    }

    [Fact]
    public void Create_AlreadyRegistered_ReturnsBeforePolicySeeding()
    {
        using var fixture = new Fixture();
        InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, fixture.Create()));
        var workspace = fixture.Registry.GetRequiredProject("alpha").ResolveWorkspace();
        var path = Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json");
        Assert.True(File.Exists(path));
        File.Delete(path);

        var output = InfrastructureTestSupport.CaptureConsole(() => Assert.Equal(0, fixture.Create()));

        Assert.Contains("Project already exists: alpha", output);
        Assert.DoesNotContain("Conductor policy:", output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Prepare_ExplicitPreset_SeedsFileAndPreservesPresetPrecedence()
    {
        using var fixture = new Fixture();
        using var error = new StringWriter();
        using var output = new StringWriter();
        var directory = Path.Combine(fixture.Root, "explicit-preset");

        var result = CliConductLoopStartup.PrepareConductLoopPolicy(directory, "Manual", error,
            storeSetup: _ => [], projectPolicyTemplatePath: fixture.Template, seedOutput: output);

        Assert.NotNull(result);
        Assert.Same(ConductorAutonomyPolicy.Manual, result.Policy);
        Assert.Equal("preset", result.Source);
        Assert.Contains("[conduct] POLICY_SEEDED policy=ProjectPermissive", output.ToString());
        Assert.Equal("ProjectPermissive", ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(directory)).Name);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Home => Path.Combine(Root, "home");
        public string Target => Path.Combine(Root, "target");
        public string Template => ProjectConductorPolicySeeder.TemplatePath(Home);
        public OrchestratorProjectRegistry Registry { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Target);
            Registry = new OrchestratorProjectRegistry(Path.Combine(Root, "registry"),
                Path.Combine(Root, "data"));
            var source = OrchestratorWorkspace.ForDirectory(Home);
            ModelFunctionCatalogStore.Save(source.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                    new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                    Name: ModelFunctionPurposes.SpecRefiner)
            ]));
            AgentCatalogStore.Save(source.AgentCatalogPath, AgentCatalog.Default());
            WorkerProfileStore.Save(source.WorkerProfilePath, WorkerProfileCatalog.Default());
            Directory.CreateDirectory(Path.GetDirectoryName(Template)!);
            var committed = ProjectConductorPolicySeeder.TemplatePath(
                InfrastructureTestSupport.FindRepositoryRoot());
            Assert.True(File.Exists(committed), $"Committed template missing: {committed}");
            File.Copy(committed, Template);
        }

        public int Create() => ProjectCliCommand.Execute(
            ["project", "create", "alpha", "--root", Target], Registry, Home, null);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
