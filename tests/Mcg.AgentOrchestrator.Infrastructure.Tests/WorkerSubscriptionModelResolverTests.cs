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

    [Xunit.Fact]
    public void ResolveEffectiveSubscriptionModelSelectionKeepsFullProfileWhenInjectedAuthProbeReportsClaudeSignedOut()
    {
        var task = new TaskSpec(TaskId.New(), "Plan the focused implementation.", AgentRole.Planner);
        var goal = new AgentOrchestratorKernel().CreateGoal("Keep the implementation focused.", [task]);
        var agent = SubscriptionPlannerAgent("planner", "Planner");
        var sandboxProbeCalls = 0;
        var authProbeCalls = 0;

        var selection = WorkerSubscriptionModelResolver.ResolveEffectiveSubscriptionModelSelection(
            agent,
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
                    HasCliCredentialArtifact: false,
                    CredentialArtifactPath: null);
            },
            profiles: DispatchTestProfiles(),
            commandExists: RealClaudeLauncherExists);

        Assert.StartsWith("full-profile: light-role profile unavailable (Claude CLI ", selection.Reason, StringComparison.Ordinal);
        Assert.Contains("no ANTHROPIC_API_KEY", selection.Reason, StringComparison.Ordinal);
        Assert.Equal("codex-cli", WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, selection));
        Assert.Equal(1, sandboxProbeCalls);
        Assert.Equal(1, authProbeCalls);
    }
}
