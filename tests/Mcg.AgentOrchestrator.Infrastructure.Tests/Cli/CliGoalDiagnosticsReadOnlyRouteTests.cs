using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Console capture is scoped by AsyncLocal; each test owns its workspace and stores.
public sealed class CliGoalDiagnosticsReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(true, "goal-diagnostics", "abc10000")]
    [Xunit.InlineData(true, "GOAL-DIAGNOSTICS", "ABC10000")]
    [Xunit.InlineData(false, "goal-diagnostics")]
    [Xunit.InlineData(false, "goal-diagnostics", "--help")]
    [Xunit.InlineData(false, "goal-diagnostics", "-h")]
    [Xunit.InlineData(false, "help", "goal-diagnostics")]
    [Xunit.InlineData(false, "goal-diagnostics", "-x")]
    [Xunit.InlineData(false, "goal-diagnostics", "")]
    [Xunit.InlineData(false, "goal-diagnostics", " ")]
    [Xunit.InlineData(false, "goal-diagnostics", "abc10000", "extra")]
    [Xunit.InlineData(false, "failure-triage", "abc10000")]
    [Xunit.InlineData(false, "goal-timing", "abc10000", "extra")]
    [Xunit.InlineData(false, "dogfood-eval", "abc10000")]
    [Xunit.InlineData(false, "readiness", "abc10000")]
    [Xunit.InlineData(false, "next", "abc10000")]
    [Xunit.InlineData(false, "goal-recovery", "abc10000")]
    public void ExplicitPrefix_ClassifiesOnlySupportedForms(bool expected, params string[] args) =>
        Xunit.Assert.Equal(expected, CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Xunit.Theory]
    [Xunit.InlineData("goal-diagnostics", "abc10000")]
    [Xunit.InlineData("GOAL-DIAGNOSTICS", "ABC10000")]
    public void NoSweepAttention_LoadsOnlyTargetWithoutWrites(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Diagnostics target");
            var bystander = kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Bystander");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = bystander;
            var providers = new InMemoryModelProviderRegistry([]);
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref agents, ref profiles, ref currentGoal, out var changed));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Equal(goal.Id, currentGoal!.Id);
            Xunit.Assert.Contains(output.Split(Environment.NewLine),
                line => line.StartsWith("Goal diagnostics abc10000", StringComparison.Ordinal));
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal(new[] { goal.Id.Value }, repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task StaleSweepAttention_DeclinesAndHydratesBeforeFallback()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Stale sweep item");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                "Stale sweep blocker", "Would be resolved by goal-diagnostics",
                $"terminal-sweep-blocker:{goal.Id.Value}:stale");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var args = new[] { "goal-diagnostics", "abc10000" };
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref agents, ref profiles, ref currentGoal, out var changed));
                Xunit.Assert.False(changed);
            });
            Xunit.Assert.Equal(string.Empty, output);
            // The decline must follow the attention check, not rejection of the command form.
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);

            var fallbackRepository = new ProbeStateRepository(kernel);
            var declined = 0;
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliReadOnlyStartupHydration.ExecuteStartupCommand(args, fallbackRepository, workspace,
                    ref agents, providers, ref profiles, ref currentGoal, hydrated: false,
                    onReadOnlyDeclined: () => declined++));
            Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, declined);
            Xunit.Assert.Equal(1, fallbackRepository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MissingItemStore_DeclinesBeforeMetadataListing()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Xunit.Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")));
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var args = new[] { "goal-diagnostics", "abc10000" };
            Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var output = CaptureConsole(() => Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out _)));
            Xunit.Assert.Equal(string.Empty, output);
            Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
