using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private roots and kernels, injected home lookup, and no worker launches.
public sealed class GoalDispatchOperationsTargetHomeTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void DispatchComputesHomeForArtifactsAndSkillAvailability(bool targetIsHome, bool installHasSkills)
    {
        using var homeFixture = new OrchestratorHomeRootSeparationTests.Fixture();
        using var targetFixture = new OrchestratorHomeRootSeparationTests.Fixture();
        homeFixture.WriteFile("src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        homeFixture.WriteFile("scripts", "Update-AppDllGitHeadMarker.ps1");
        homeFixture.WriteFile(".agents", "skills", "verification-before-completion", "SKILL.md");
        if (installHasSkills)
        {
            homeFixture.WriteFile(".agents", "skills", "dotnet-windows-build-hygiene", "SKILL.md");
            homeFixture.WriteFile(".agents", "skills", "orchestrator-dogfood", "SKILL.md");
        }
        var home = OrchestratorHome.ResolveForLaunch(homeFixture.Root, _ => null);
        var workspace = OrchestratorWorkspace.ForDirectory(targetIsHome ? homeFixture.Root : targetFixture.Root);
        Assert.Equal(targetIsHome, home.IsHome(workspace));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Run dogfood-log for the parser.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Improve the parser", [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Improve the parser",
            ["Parser handles the input."], VerificationClass.TestVerifiable, [], []));
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias,
                ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli",
                AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var homeLookupCount = 0;
        var operations = new GoalDispatchOperations(resolveHome: actualWorkspace =>
        {
            Assert.Same(workspace, actualWorkspace);
            homeLookupCount++;
            return home;
        });
        var disabledSandbox = new WorkerSandboxOptions(false,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

        // Echo is intentionally rejected by preflight. This exposes the real availability
        // findings through the App path without depending on an installed provider CLI.
        var profiles = new WorkerProfileCatalog([new WorkerProfile("codex-cli", "echo {promptPath}")]);
        var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() => operations.SubscriptionDispatchTask(
            kernel, workspace, goal, task, [agent], profiles,
            modelOverride: new DispatchModelOverride("codex-cli", AgentCatalog.OpenAiGpt61SolSubscriptionModelAlias, "low")));
        Assert.Equal(1, homeLookupCount);

        var dispatch = operations.ProfileDispatchTask(kernel, workspace, goal, task,
            new WorkerProfile("fixture", "worker --cd {workingDirectory}"), sandboxOptions: disabledSandbox);

        Assert.True(File.Exists(dispatch.PromptPath));
        Assert.Equal(2, homeLookupCount);
        var context = Path.Combine(workspace.ExecutionDirectory, ".orchestrator-context", goal.Id.Value);
        var brokers = File.ReadAllText(Path.Combine(context, "workflow-brokers.md"));
        var skills = File.ReadAllText(Path.Combine(context, "selected-skills.md"));
        Assert.Equal(targetIsHome, brokers.Contains("- backlog-log-evidence", StringComparison.Ordinal));
        Assert.Equal(targetIsHome, brokers.Contains(".orchestrator/dogfood-log.db", StringComparison.Ordinal));
        Assert.NotNull(task.LastDispatch);
        foreach (var name in new[] { "dotnet-windows-build-hygiene", "orchestrator-dogfood" })
        {
            Assert.Equal(targetIsHome, skills.Contains("- " + name, StringComparison.Ordinal));
            Assert.Equal(targetIsHome, task.LastDispatch.SelectedSkills!.Contains(name));
        }

        if (targetIsHome && !installHasSkills)
        {
            var missing = Assert.Single(failure.Findings, finding =>
                finding.StartsWith("blocked: missing required local skill(s):", StringComparison.Ordinal));
            Assert.Contains("dotnet-windows-build-hygiene", missing, StringComparison.Ordinal);
            Assert.Contains("orchestrator-dogfood", missing, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(failure.Findings, finding =>
                finding.StartsWith("blocked: missing required local skill(s):", StringComparison.Ordinal));
            var available = Assert.Single(failure.Findings, finding =>
                finding.StartsWith("ok: selected skill manifest available (", StringComparison.Ordinal));
            foreach (var name in new[] { "dotnet-windows-build-hygiene", "orchestrator-dogfood" })
                Assert.Equal(targetIsHome, available.Contains(name, StringComparison.Ordinal));
            Assert.Contains("verification-before-completion", available, StringComparison.Ordinal);
        }
    }
}
