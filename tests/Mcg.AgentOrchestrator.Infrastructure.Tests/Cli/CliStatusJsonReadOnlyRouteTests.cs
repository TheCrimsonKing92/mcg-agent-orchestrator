using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private roots and probe repositories; console capture is async-local.
public sealed class CliStatusJsonReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("status")]
    [InlineData("STATUS")]
    [InlineData("sTaTuS")]
    public void ExactForm_IsQueryOnlyAndReadOnly(string verb)
    {
        string[] args = [verb, "abc12345", "--json"];
        Assert.True(CliStatusJsonQueryCommand.IsStatusJsonCommand(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
    }

    [Theory]
    [InlineData("status", "--json")]
    [InlineData("status", "abc12345", "--jsonx")]
    [InlineData("status", "abc12345", "--JSON")]
    [InlineData("status", "abc12345", "--json", "extra")]
    [InlineData("status", "--json", "abc12345")]
    [InlineData("status", "abc12345", "--tasks-only", "--json")]
    [InlineData("monitor", "abc12345", "--json")]
    [InlineData("status", "", "--json")]
    [InlineData("status", " ", "--json")]
    [InlineData("status", "--help", "--json")]
    [InlineData("status", "-h", "--json")]
    public void OtherForms_DeclineJsonAndReadOnlyRoutes(params string[] args)
    {
        Assert.False(CliStatusJsonQueryCommand.IsStatusJsonCommand(args));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Theory]
    [InlineData("status", "abc12345")]
    [InlineData("status", "abc12345", "--tasks-only")]
    public void HumanForms_KeepTheirReadOnlyRoute(params string[] args)
    {
        Assert.False(CliStatusJsonQueryCommand.IsStatusJsonCommand(args));
        Assert.True(CliStatusQueryCommand.IsStatusQueryCommand(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupFailure_MatchesHumanExceptionAndMessage(bool ambiguous)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId("abc12345aaaaaaaaaaaaaaaaaaaaaaaa"), "First goal");
            kernel.CreateGoal(new GoalId("abc12345bbbbbbbbbbbbbbbbbbbbbbbb"), "Second goal");
            var prefix = ambiguous ? "abc12345" : "missing";
            var repository = new QueryOnlyStateRepository(kernel);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var human = Record.Exception(() =>
            {
                CliStatusQueryCommand.Execute(["status", prefix], repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal);
            });
            var json = Record.Exception(() =>
            {
                CliStatusJsonQueryCommand.Execute(["status", prefix, "--json"], repository, workspace);
            });
            Assert.NotNull(human);
            Assert.NotNull(json);
            Assert.Equal(ambiguous ? typeof(InvalidOperationException) : typeof(KeyNotFoundException), json.GetType());
            Assert.Equal(human.GetType(), json.GetType());
            Assert.Equal(ambiguous ? "Goal prefix 'abc12345' is ambiguous." : "Goal 'missing' was not found.", json.Message);
            Assert.Equal(human.Message, json.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ServedForm_LoadsOnlyTargetAndHasNoWriterEffects()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var target = kernel.CreateGoal(new GoalId("abc12345aaaaaaaaaaaaaaaaaaaaaaaa"), "Target");
            kernel.CreateGoal(new GoalId("def12345aaaaaaaaaaaaaaaaaaaaaaaa"), "Bystander");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var output = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(["status", "abc12345", "--json"], repository,
                    OrchestratorWorkspace.ForDirectory(root), new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out var changed));
                Assert.False(changed);
            });
            Assert.Contains("mcg.status", output);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal(new[] { target.Id.Value }, repository.LoadedGoalIds);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
            Assert.Null(currentGoal);
        }
        finally { Directory.Delete(root, true); }
    }
}
