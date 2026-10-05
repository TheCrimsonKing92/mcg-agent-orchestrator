using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its repository, kernel and temporary workspace.
public sealed class CliInspectionReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("goals")]
    [Xunit.InlineData("model-outcomes")]
    [Xunit.InlineData("architecture")]
    [Xunit.InlineData("config", "agents")]
    [Xunit.InlineData("config", "profiles")]
    [Xunit.InlineData("config", "policy")]
    [Xunit.InlineData("backlog-view")]
    [Xunit.InlineData("GOALS")]
    [Xunit.InlineData("MODEL-OUTCOMES")]
    [Xunit.InlineData("ARCHITECTURE")]
    [Xunit.InlineData("CONFIG", "AGENTS")]
    [Xunit.InlineData("CONFIG", "PROFILES")]
    [Xunit.InlineData("CONFIG", "POLICY")]
    [Xunit.InlineData("BACKLOG-VIEW")]
    public void ExactForms_ClassifyAndNeverLoadGoalsOrWrite(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var kernel = CreateInspectionSeed();
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = kernel.Goals.First();
            var originalGoal = currentGoal;
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    args, repository, OrchestratorWorkspace.ForDirectory(root),
                    new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out var changed));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.NotEmpty(output);
            Xunit.Assert.Same(originalGoal, currentGoal);
            Xunit.Assert.Equal(args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                repository.ListGoalMetadataCount);
            AssertNoGoalReadsOrWrites(repository);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("goals", "subscribe")]
    [Xunit.InlineData("goals", "--unknown-flag")]
    [Xunit.InlineData("goals", "extra")]
    [Xunit.InlineData("model-outcomes", "extra")]
    [Xunit.InlineData("architecture", "extra")]
    [Xunit.InlineData("config")]
    [Xunit.InlineData("config", "doctor")]
    [Xunit.InlineData("config", "bogus")]
    [Xunit.InlineData("backlog-view", "extra")]
    public void OtherShapes_KeepTheirExistingRoute(params string[] args) => AssertDeclined(args);

    [Xunit.Fact]
    public void HelpAndExtraTokens_AreDeclinedForEveryExactForm()
    {
        AssertDeclined([]);
        foreach (var args in ExactForms())
        foreach (var extra in new[] { "--help", "-h", "help", "extra", "--unknown-flag" })
            AssertDeclined([.. args, extra]);

        Xunit.Assert.False(CliInspectionQueryCommand.IsInspectionQueryCommand(["goals", "--board"]));
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["goals", "--board"]));
    }

    private static void AssertDeclined(string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
            args, repository, OrchestratorWorkspace.ForDirectory(Path.GetTempPath()),
            new InMemoryModelProviderRegistry([]), null,
            ref agents, ref profiles, ref currentGoal, out var changed));
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
        AssertNoGoalReadsOrWrites(repository);
    }

    private static void AssertNoGoalReadsOrWrites(ProbeStateRepository repository)
    {
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        Xunit.Assert.Empty(repository.LoadedGoalIds);
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
        Xunit.Assert.Equal(0, repository.MutationAttempts);
        Xunit.Assert.Equal(0, repository.SaveAttempts);
        Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
    }

    internal static string[][] ExactForms() =>
    [
        ["goals"], ["model-outcomes"], ["architecture"],
        ["config", "agents"], ["config", "profiles"], ["config", "policy"], ["backlog-view"]
    ];

    internal static AgentOrchestratorKernel CreateInspectionSeed()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First inspection goal");
        kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Second inspection goal");
        return kernel;
    }
}
