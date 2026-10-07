using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its databases and console capture.
public sealed class CliCommandTestsPortfolioFastRoute : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("epic-add", "Title")]
    [Xunit.InlineData("epic-assign", "goal", "epic")]
    [Xunit.InlineData("epic-assign-many", "epic", "goal")]
    [Xunit.InlineData("epic-assign-many", "epic", "--ids-file", "ids.txt", "--dry-run")]
    public void StoreWriteForms_SkipKernelState(params string[] args) =>
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(args));

    [Xunit.Fact]
    public void GoalPrefix_AssignsFromMetadataWithoutHydrationOrStateMutation()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = Seed(root, "aaaaaaaa111111111111111111111111");
            var store = new PortfolioStore(workspace.PortfolioStorePath);
            var epic = store.AddEpicAsync("Board").GetAwaiter().GetResult();
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var output = Execute(["epic-assign", "AAAAAAAA", "Board"], workspace, probe);

            Xunit.Assert.Equal($"Assigned goal aaaaaaaa to epic {epic.Id[..8]}{Environment.NewLine}", output);
            Xunit.Assert.Equal(epic.Id, store.GetGoalMembershipAsync(
                "aaaaaaaa111111111111111111111111").GetAwaiter().GetResult()!.EpicId);
            Xunit.Assert.Equal(0, probe.FullLoadAttempts);
            Xunit.Assert.Equal(0, probe.LoadGoalsCount);
            Xunit.Assert.Equal(0, probe.MutationAttempts);
            Xunit.Assert.Equal(0, probe.SaveAttempts);
            Xunit.Assert.Equal(0, probe.MergeSaveAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void AmbiguousGoalPrefix_PreservesExactError()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = Seed(root, "aaaaaaaa111111111111111111111111",
                "aaaaaaaa222222222222222222222222");
            new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Board").GetAwaiter().GetResult();
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                Execute(["epic-assign", "aaaaaaaa", "Board"], workspace, probe));
            Xunit.Assert.Equal("Goal prefix 'aaaaaaaa' is ambiguous.", error.Message);
            Xunit.Assert.Equal(0, probe.FullLoadAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void GoalAndBacklogPrefix_PreservesExactBothMatchError()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var backlog = new BacklogStore(workspace.BacklogStorePath).AddAsync("Backlog").GetAwaiter().GetResult();
            Seed(root, backlog.Id);
            var store = new PortfolioStore(workspace.PortfolioStorePath);
            store.AddEpicAsync("Board").GetAwaiter().GetResult();
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var prefix = backlog.Id[..8];
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                Execute(["epic-assign", prefix, "Board"], workspace, probe));
            Xunit.Assert.Equal($"Portfolio assignment target '{prefix}' matches both a goal and a backlog item.", error.Message);
            Xunit.Assert.Null(store.GetGoalMembershipAsync(backlog.Id).GetAwaiter().GetResult());
            Xunit.Assert.Null(store.GetBacklogMembershipAsync(backlog.Id).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static OrchestratorWorkspace Seed(string root, params string[] ids)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        foreach (var id in ids)
            kernel.CreateGoal(new GoalId(id), "Metadata assignment goal");
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        return workspace;
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, probe, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }
}
