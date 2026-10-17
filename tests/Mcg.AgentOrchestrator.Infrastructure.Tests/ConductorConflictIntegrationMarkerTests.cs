using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorConflictIntegrationRecordTests;

// Parallel-safe: preflight only inspects this fixture's repository; providers are stubbed.
public sealed class ConductorConflictIntegrationMarkerTests
{
    [Theory]
    [InlineData("matching", true)]
    [InlineData("removed", false)]
    [InlineData("other task", false)]
    [InlineData("malformed", false)]
    public void Preflight_AdmitsOnlyMatchingConductorMerge(string kind, bool allowed)
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var worktree = fixture.GitFixture.Worktree;
        Assert.True(ConductorConflictIntegrationMarker.Admits(fixture.Developer, worktree));
        if (kind == "removed") ConductorConflictIntegrationMarker.Remove(worktree);
        if (kind == "malformed") File.WriteAllText(ConductorConflictIntegrationValidateTests.MarkerPath(worktree), "{");
        if (kind == "other task")
        {
            var path = ConductorConflictIntegrationValidateTests.MarkerPath(worktree);
            File.WriteAllText(path, File.ReadAllText(path).Replace(fixture.Developer.Id.Value, TaskId.New().Value));
        }
        // Pin the launcher, model, auth and sandbox inputs independently of the host's providers.
        var agent = new AgentDefinition(fixture.Developer.AssignedAgentId!, "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        // Keep required skill availability independent of the host and the conflicted worktree.
        var skillDirectory = Path.Combine(fixture.GitFixture.Repository, ".orchestrator", "skills");
        var skills = WorkerContextArtifacts.SelectSkillRequirements(fixture.Goal, fixture.Developer, worktree, skillDirectory);
        Assert.NotEmpty(skills);
        foreach (var skill in skills)
        {
            var path = Assert.IsType<string>(skill.OrchestratorCandidatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"# {skill.Name}\nFixture skill for preflight availability.\n");
        }
        var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(fixture.Goal, fixture.Developer, [agent],
            WorkerProfileCatalog.Default(), worktree, DateTimeOffset.Parse("2026-10-17T12:00:00Z"), allowGitReference: true,
            sandboxOptions: new WorkerSandboxOptions(OperatingSystem.IsWindows(), WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent, orchestratorSkillDirectory: skillDirectory);
        var findings = string.Join("\n", preflight.Findings);
        Assert.DoesNotContain("missing required local skill", findings);
        if (allowed)
        {
            Assert.True(preflight.Allowed, findings);
            Assert.Contains("ok: worktree holds the conductor-admitted integration merge", findings);
        }
        else
        {
            Assert.False(preflight.Allowed, findings);
            Assert.Contains("blocked: worktree has 1 uncommitted change(s) before dispatch;", findings);
        }
    }
}
