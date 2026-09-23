using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSubscriptionModelResolverTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void ResolveEffectiveSubscriptionModelSelectionUsesInjectedProbesForLightRole()
    {
        var task = new TaskSpec(TaskId.New(), "Plan the focused implementation.", AgentRole.Planner);
        var goal = new AgentOrchestratorKernel().CreateGoal("Keep the implementation focused.", [task]);
        var sandboxProbeCalls = 0;
        var authProbeCalls = 0;

        var selection = WorkerSubscriptionModelResolver.ResolveEffectiveSubscriptionModelSelection(
            SubscriptionPlannerAgent("planner", "Planner"),
            goal,
            task,
            WorkerProviderCatalog.Default(),
            sandboxProbe: () =>
            {
                sandboxProbeCalls++;
                return new WorkerSandboxOptions(
                    true,
                    WorkerSandboxOptions.DefaultAccount,
                    WorkerSandboxOptions.DefaultCredentialTarget);
            },
            claudeAuthProbe: () =>
            {
                authProbeCalls++;
                return new ClaudeCliAuthState(
                    HasAnthropicApiKey: false,
                    HasCliCredentialArtifact: true,
                    CredentialArtifactPath: null);
            },
            profiles: DispatchTestProfiles(),
            commandExists: RealClaudeLauncherExists);

        Assert.Equal("light-role: Planner uses claude-cli/claude-haiku-4-5", selection.Reason);
        Assert.Equal("claude-haiku-4-5", selection.Model.ModelName);
        Assert.Equal(1, sandboxProbeCalls);
        Assert.Equal(1, authProbeCalls);
    }
}
