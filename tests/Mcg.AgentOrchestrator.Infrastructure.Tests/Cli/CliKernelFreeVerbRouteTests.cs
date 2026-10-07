using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its catalog and probe; console capture is async-local.
public sealed class CliKernelFreeVerbRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("help")]
    [InlineData("HELP")]
    public void Help_UnknownVerb_RejectsWithoutStateAccess(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var error = Assert.Throws<ArgumentException>(() => CaptureConsole(() =>
                Execute(repository, workspace, [verb])));

            Assert.Contains($"Unknown command '{verb}'.", error.Message);
            Assert.Contains("Did you mean: --help", error.Message);
            AssertNoStateAccess(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("model-functions")]
    [InlineData("MODEL-FUNCTIONS")]
    public void ModelFunctions_ConfiguredBinding_PrintsWithoutStateAccess(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding("route-test", ModelLane.CheapApi,
                    new ModelProfile("test-provider", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]));

            var output = CaptureConsole(() => Assert.False(Execute(repository, workspace, [verb])));

            Assert.Contains("Model functions (orchestrator-internal", output);
            Assert.Contains("route-test [CheapApi]: test-provider/test-model", output);
            AssertNoStateAccess(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("model-function-add")]
    [InlineData("MODEL-FUNCTION-ADD")]
    public void ModelFunctionAdd_NewBinding_SavesCatalogWithoutStateAccess(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
            var workspace = OrchestratorWorkspace.ForDirectory(root);

            _ = CaptureConsole(() => Execute(repository, workspace,
                [verb, "route-test", "cheap-api", "test-provider", "test-model"]));

            Assert.True(File.Exists(workspace.ModelFunctionCatalogPath));
            var binding = Assert.Single(ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath).Bindings,
                binding => binding.Purpose == "route-test");
            Assert.Equal(ModelLane.CheapApi, binding.Lane);
            Assert.Equal("test-provider", binding.Model.ProviderName);
            Assert.Equal("test-model", binding.Model.ModelName);
            AssertNoStateAccess(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool Execute(ProbeStateRepository repository, OrchestratorWorkspace workspace,
        IReadOnlyList<string> args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CliPersistentStateRunner.ExecuteCommand(args, repository, workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal);
    }

    private static void AssertNoStateAccess(ProbeStateRepository repository)
    {
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.MergeSaveAttempts);
        Assert.Equal(0, repository.OutboxClaimAttempts);
    }
}
