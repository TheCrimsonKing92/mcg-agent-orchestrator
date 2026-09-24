using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementRunArgumentValidationTests
{
    [Xunit.Theory]
    [Xunit.InlineData("1234567890abcdef1234567890abcdef 20260924125201945")]
    [Xunit.InlineData("1234567890abcdef 1234567890abcdef")]
    [Xunit.InlineData(" 1234567890abcdef1234567890abcdef")]
    [Xunit.InlineData("1234567890abcdef1234567890abcdef\t")]
    [Xunit.InlineData("   ")]
    [Xunit.InlineData("\t")]
    public void WhitespaceInGoalIdIsUsageErrorBeforeClaim(string invalidGoalId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-refinement-usage-{Guid.NewGuid():N}");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliPersistentStateRunner.ExecuteCommand(
                [GoalRefinementWorkCoordinator.CommandName, invalidGoalId, "stamp"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("<goal-id>", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"'{invalidGoalId}'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("whitespace", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("claim-miss", exception.Message, StringComparison.Ordinal);
    }
}
