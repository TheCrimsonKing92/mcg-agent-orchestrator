using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSkillResolverTests : WorkerDispatchTestSupport, IDisposable
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(false,
        WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public void PlannerPreflightUsesOrchestratorFallback()
    {
        var (goal, task, agents, workingDirectory, skillDirectory) = CreatePlanner();
        SeedSkills(skillDirectory);

        var requirements = WorkerContextArtifacts.SelectSkillRequirements(
            goal, task, workingDirectory, skillDirectory);
        var preflight = Preflight(goal, task, agents, workingDirectory, skillDirectory);

        Assert.True(preflight.Allowed, string.Join(Environment.NewLine, preflight.Findings));
        Assert.Equal(3, requirements.Count);
        Assert.All(requirements, skill =>
        {
            Assert.True(skill.Available);
            Assert.Equal(WorkerSkillSource.Orchestrator, skill.ResolvedSource);
            Assert.Equal(Path.Combine(skillDirectory, skill.Name, "SKILL.md"), skill.ResolvedPath);
        });
        Assert.False(Directory.Exists(Path.Combine(workingDirectory, ".agents")));
    }

    [Xunit.Fact]
    public void MissingSkillBlocksAndNamesBothLocations()
    {
        var (goal, task, agents, workingDirectory, skillDirectory) = CreatePlanner();
        SeedSkills(skillDirectory);
        File.Delete(Path.Combine(skillDirectory, "criterion-ownership-planning", "SKILL.md"));

        var preflight = Preflight(goal, task, agents, workingDirectory, skillDirectory);
        Assert.False(preflight.Allowed);
        var finding = Assert.Single(preflight.Findings, line =>
            line.StartsWith("blocked: missing required local skill(s):", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(workingDirectory, ".agents", "skills",
            "criterion-ownership-planning", "SKILL.md"), finding, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(skillDirectory, "criterion-ownership-planning", "SKILL.md"),
            finding, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-windows-build-hygiene", finding, StringComparison.Ordinal);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkerProfileDispatcher.PrepareSubscriptionTask(new AgentOrchestratorKernel(), goal, task,
                agents, DispatchTestProfiles(), Path.Combine(root, "prompts"), workingDirectory,
                DateTimeOffset.Parse("2026-10-09T12:00:00Z"), sandboxOptions: DisabledSandbox,
                commandExists: _ => true, claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
                orchestratorSkillDirectory: skillDirectory));
        Assert.Contains(finding, exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void LocalSkillWinsInMixedSelection()
    {
        var (goal, task, _, workingDirectory, skillDirectory) = CreatePlanner();
        SeedSkills(skillDirectory);
        WriteSkill(workingDirectory, "criterion-ownership-planning", "local skill");

        var requirements = WorkerContextArtifacts.SelectSkillRequirements(
            goal, task, workingDirectory, skillDirectory);
        var local = Assert.Single(requirements, skill => skill.Name == "criterion-ownership-planning");
        Assert.Equal(WorkerSkillSource.WorkingDirectory, local.ResolvedSource);
        Assert.Equal(Path.Combine(workingDirectory, local.RelativePath), local.ResolvedPath);
        Assert.Equal("local skill", File.ReadAllText(local.ResolvedPath!));
        Assert.All(requirements.Where(skill => skill != local), skill =>
            Assert.Equal(WorkerSkillSource.Orchestrator, skill.ResolvedSource));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("absent")]
    public void UnconfiguredOrAbsentFallbackKeepsMissingSkills(string? directory)
    {
        var (goal, task, agents, workingDirectory, _) = CreatePlanner();
        var fallback = directory == "absent" ? Path.Combine(root, directory) : directory;
        var preflight = Preflight(goal, task, agents, workingDirectory, fallback);
        Assert.False(preflight.Allowed);
        var finding = Assert.Single(preflight.Findings, line =>
            line.StartsWith("blocked: missing required local skill(s):", StringComparison.Ordinal));
        Assert.Contains(Path.Combine(workingDirectory, ".agents", "skills"), finding, StringComparison.Ordinal);
        Assert.Contains(fallback is { Length: > 0 } ? fallback : "no orchestrator skill directory was configured",
            finding, StringComparison.Ordinal);
    }

    private (Goal Goal, TaskSpec Task, AgentDefinition[] Agents, string WorkingDirectory,
        string SkillDirectory) CreatePlanner()
    {
        var workingDirectory = Path.Combine(root, "managed-repository");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan dotnet build changes.", AgentRole.Planner, "Verify inputs.");
        var goal = kernel.CreateGoal("Map bounded inputs", [task]);
        var agent = new AgentDefinition(new AgentId("planner"), "Planner", AgentRole.Planner,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text,
                SubscriptionMode.ApiKey, "medium"), ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        return (goal, task, [agent], workingDirectory, Path.Combine(root, "orchestrator", ".agents", "skills"));
    }

    private static WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task,
        AgentDefinition[] agents, string workingDirectory, string? skillDirectory) =>
        WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, agents, DispatchTestProfiles(),
            workingDirectory, DateTimeOffset.Parse("2026-10-09T12:00:00Z"), sandboxOptions: DisabledSandbox,
            commandExists: _ => true, claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            orchestratorSkillDirectory: skillDirectory);

    private static void SeedSkills(string skillDirectory)
    {
        foreach (var name in new[] { "dotnet-windows-build-hygiene", "orchestrator-worker-verification",
            "criterion-ownership-planning" })
        {
            var directory = Path.Combine(skillDirectory, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# {name}\n");
        }
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
