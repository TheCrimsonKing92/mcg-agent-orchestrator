using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// This class carried the same dangling GoalWorktreeCleanupHooks literal, which named a
// collection with no definition and therefore no behavior. It mutates no process-wide
// environment, so unlike the CliProcessEnvironment members it needs no exclusion: cadence
// is owned by the supplied context, and CliCommandTestBase already bounds its host cost.
public sealed class CliCommandTestsCleanupCadence : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("conduct", "recent", true)]
    [Xunit.InlineData("conduct", "due", false)]
    [Xunit.InlineData("conduct", "independent", false)]
    [Xunit.InlineData("reconcile", "recent", true)]
    [Xunit.InlineData("reconcile", "due", false)]
    [Xunit.InlineData("reconcile", "independent", false)]
    public void RunnerPreservesSuppliedCleanupSweepCadence(string command, string cadence, bool retained)
    {
        var root = CreateShortAcceptanceRepository();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            var providers = new InMemoryModelProviderRegistry([]);
            Goal? currentGoal = null;
            var now = DateTimeOffset.UtcNow;
            var hooks = GoalWorktreeCleanupHooks.ForConfiguration(new GoalWorktreeCleanupOptions(
                TimeSpan.FromHours(1), 3, TimeSpan.FromHours(1)), workspace.OrchestratorDirectory) with { CleanupUtcNow = () => now };
            var context = new WorktreeCleanupContext(hooks);
            context.Scheduler.SweepNow(root, kernel);
            var orphan = Path.Combine(root, GoalWorktrees.DirectoryName, "cadence-orphan");
            Directory.CreateDirectory(Path.Combine(orphan, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphan, ".mcg-sandbox", "marker.txt"), "Created after the owner's sweep");
            if (cadence == "due") now = now.AddHours(1);
            if (cadence == "independent") context = new WorktreeCleanupContext(hooks);
            string[] commandArgs = command == "conduct"
                ? ["conduct", "--loop", "--max-iterations", "1", "--policy", "Conservative"]
                : ["reconcile"];
            _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                commandArgs,
                repository, workspace, ref agents, providers, ref profiles, ref currentGoal,
                acceptanceCleanupContext: context));
            Xunit.Assert.Equal(retained, Directory.Exists(orphan));
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }
}


