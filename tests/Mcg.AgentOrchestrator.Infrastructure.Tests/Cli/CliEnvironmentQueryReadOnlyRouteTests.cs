using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its workspace and probe; console capture is async-local.
public sealed class CliEnvironmentQueryReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("doctor", "Ready:")]
    [InlineData("tenant", "Tenant: acme")]
    public void BareCommand_MatchesDispatcherWithoutReadingState(string verb, string marker)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "acme");
            var kernel = new AgentOrchestratorKernel();
            var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            var providers = new InMemoryModelProviderRegistry([]);
            Goal? currentGoal = null;
            string[] args = [verb];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var actual = CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(args, probe, workspace, providers,
                    null, ref agents, ref profiles, ref currentGoal, out var changed));
                Assert.False(changed);
            });
            var expected = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal)));

            Assert.Equal(expected, actual);
            Assert.Contains(marker, actual);
            Assert.Equal(0, probe.ListGoalMetadataCount);
            Assert.Equal(0, probe.LoadGoalsCount);
            Assert.Equal(0, probe.LoadGoalCount);
            Assert.Equal(0, probe.FullLoadAttempts);
            Assert.Equal(0, probe.ListOutboxMessagesCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("tenant")]
    [InlineData("logs", "abc10000", "1")]
    public void QueryVerb_ClassifiesAsQueryOnly(params string[] args) =>
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));

    [Theory]
    [InlineData("doctor", "extra")]
    [InlineData("doctor", "--help")]
    [InlineData("tenant", "extra")]
    [InlineData("tenant", "--help")]
    [InlineData("epic-suggestions")]
    [InlineData("operator-inbox-ack", "item-1")]
    public void UnsupportedOrWritingForm_DeclinesReadOnlyRoute(params string[] args) =>
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Theory]
    [InlineData("epic-suggestions")]
    [InlineData("operator-inbox-ack", "item-1")]
    public void WritingVerb_RetainsExecutionClassification(params string[] args) =>
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
}
