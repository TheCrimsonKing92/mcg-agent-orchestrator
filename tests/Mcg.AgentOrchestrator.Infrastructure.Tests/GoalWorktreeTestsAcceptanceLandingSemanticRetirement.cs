using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe through the existing host-capacity budget, isolated repositories and task-local console capture.
public sealed class GoalWorktreeTestsAcceptanceLandingSemanticRetirement : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "Conduct_inline_landing_skips_semantic_judges_and_receipts")]
    public async Task ConductInlineLandingSkipsSemanticJudgesAndReceipts()
    {
        var repo = CreateSeededRepository();
        try
        {
            RunGit(repo, "branch", "-M", "main");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog(
                [new ModelFunctionBinding(ModelFunctionPurposes.AcceptanceJudge, ModelLane.CheapApi,
                    new ModelProfile("Fake", "judge", ModelCapability.Text, SubscriptionMode.ApiKey))]));
            var receiptLinesBefore = File.Exists(workspace.SemanticAcceptanceLogPath)
                ? File.ReadLines(workspace.SemanticAcceptanceLogPath).Count()
                : 0;

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Retire semantic judges during inline landing", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "semantic-retirement.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Semantic retirement goal");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            var provider = new ConductorDriverTests.CountingModelProvider("Fake", """
                ```json
                {"criteria_met":true,"confidence":"high","reasons":["ok"],"unmet_criteria":[]}
                ```
                """);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([provider]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() =>
            {
                // The persistent runner constructs the public driver; the injected verifier selects inline landing.
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    ["conduct", goal.Id.Value[..8], "--policy", "Permissive"],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    acceptanceVerifier: FakeAcceptanceVerifier.Passed(),
                    acceptanceCleanupContext: CreateIsolatedCleanupContext(workspace.ExecutionDirectory));
                Assert.True(changed);
            });

            Assert.Contains("Landed:", output);
            Assert.True(File.Exists(Path.Combine(repo, "semantic-retirement.txt")));
            Assert.Equal(0, provider.Calls);
            var receiptLinesAfter = File.Exists(workspace.SemanticAcceptanceLogPath)
                ? File.ReadLines(workspace.SemanticAcceptanceLogPath).Count()
                : 0;
            Assert.Equal(receiptLinesBefore, receiptLinesAfter);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
