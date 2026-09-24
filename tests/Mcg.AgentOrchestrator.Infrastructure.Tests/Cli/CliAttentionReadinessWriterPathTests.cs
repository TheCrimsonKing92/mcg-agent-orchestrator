using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionReadinessWriterPathTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("attention", "answer", "abc10000", "id", "answer")]
    [Xunit.InlineData("attention", "dismiss", "abc10000")]
    [Xunit.InlineData("attention", "dismiss", "--item", "item-id")]
    [Xunit.InlineData("readiness", "abc10000")]
    public async Task MutatingFormsStayOnWriterPath(params string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
        var error = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository));
        Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(1, repository.FullLoadAttempts);
    }

    [Xunit.Fact]
    public async Task NextRetainsItsExistingQueryOnlyCompositionWhileUsingTheWriterPath()
    {
        var args = new[] { "next", "abc10000" };
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
        var error = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository));
        Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(1, repository.FullLoadAttempts);
    }

    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "list")]
    [Xunit.InlineData("attention", "show")]
    public void AttentionReadFormsRetainExecutionCompositionForDeclinedCalls(params string[] args) =>
        Xunit.Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));

    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "show")]
    public void ParkedGoalDeclinesReadRouteAndRehydratesBeforeFallback(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Parked goal");
            kernel.ParkGoal(goal.Id, "Operator wait");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var output = CaptureConsole(() => Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out _)));
            Xunit.Assert.Equal(string.Empty, output);

            var declined = 0;
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliReadOnlyStartupHydration.ExecuteStartupCommand(args, repository, workspace,
                    ref agents, providers, ref profiles, ref currentGoal, hydrated: false,
                    onReadOnlyDeclined: () => declined++));
            Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, declined);
            Xunit.Assert.Equal(1, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task NextFullWithStaleSweepAttentionDeclinesToWriterPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Stale sweep item");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                "Stale sweep blocker", "Would be resolved by next --full",
                $"terminal-sweep-blocker:{goal.Id.Value}:stale");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var args = new[] { "next", "--full", goal.Id.Value[..8] };
            var output = CaptureConsole(() => Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out _)));
            Xunit.Assert.Equal(string.Empty, output);
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliReadOnlyStartupHydration.ExecuteStartupCommand(args, repository, workspace,
                    ref agents, providers, ref profiles, ref currentGoal, hydrated: false));
            Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
