using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class GoalWorktreeTestsAcceptanceLandingEnvironment : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "Cli_acceptance_lands_when_discord_token_is_invalid")]
    public void CliAcceptanceLandsWhenDiscordTokenIsInvalid()
    {
        var repo = CreateSeededRepository();
        var previousToken = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", "invalid-token-for-acceptance-test");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate ignores Discord auth", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            OperatorChannelStore.Save(
                workspace.OperatorChannelPath,
                new OperatorChannelCatalog("discord", ForumChannelId: "42"));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var channel = OperatorChannelComposition.Create(
                OperatorChannelStore.Load(workspace.OperatorChannelPath),
                OperatorChannelFactory.ResolveBotToken(),
                workspace.OrchestratorDirectory);
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal, channel)
            {
                CleanupContext = CreateIsolatedCleanupContext(workspace.ExecutionDirectory),
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification: passed (exit 0)", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(HasCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, goal.Id), "remove:acceptance-deferred"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", previousToken);
            DeleteDirectory(repo);
        }
    }

}
