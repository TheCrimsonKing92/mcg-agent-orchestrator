using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;

// Parallel-safe: each case owns its repository, skill tree, prompts and kernel; no workers launch.
public sealed class WorkerSkillResolverTests : WorkerDispatchTestSupport, IDisposable
{
    private static readonly WorkerSandboxOptions DisabledSandbox = new(false,
        WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Theory]
    [Xunit.InlineData("codex-cli")]
    [Xunit.InlineData("claude-cli")]
    public void PlannerDispatchCopiesAndAttestsFallbackSkills(string profile)
    {
        var (goal, task, agents, workingDirectory, skillDirectory, kernel) = CreatePlanner(profile);
        SeedSkills(skillDirectory);
        var before = Directory.GetFiles(workingDirectory, "*", SearchOption.AllDirectories);
        Assert.False(Directory.Exists(Path.Combine(workingDirectory, ".agents")));

        var dispatch = WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, goal, task,
            agents, DispatchTestProfiles(), Path.Combine(root, "prompts"), workingDirectory,
            DateTimeOffset.Parse("2026-10-09T12:00:00Z"), sandboxOptions: DisabledSandbox,
            commandExists: _ => true, claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli,
            orchestratorSkillDirectory: skillDirectory);

        Assert.True(File.Exists(dispatch.PromptPath));
        Assert.NotNull(task.LastDispatch);
        Assert.Equal(profile, task.LastDispatch.WorkerName);
        var context = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
        var instructions = File.ReadAllText(Path.Combine(context, "selected-skills.md"));
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "artifact-registry.json")));
        var requirements = WorkerContextArtifacts.SelectSkillRequirements(goal, task, workingDirectory, skillDirectory);
        Assert.Equal(3, requirements.Count);
        Assert.All(requirements, skill =>
        {
            var deliveredPath = Path.Combine(context, skill.RelativePath);
            var expectedBytes = File.ReadAllBytes(skill.ResolvedPath!);
            Assert.Equal(expectedBytes, File.ReadAllBytes(deliveredPath));
            Assert.Contains($"Path: {deliveredPath}", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain(skill.ResolvedPath!, instructions, StringComparison.Ordinal);
            var entry = Assert.Single(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
                artifact => artifact.GetProperty("path").GetString() == skill.RelativePath.Replace('\\', '/'));
            Assert.True(entry.GetProperty("exists").GetBoolean());
            Assert.True(entry.GetProperty("hashVerified").GetBoolean());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant(),
                entry.GetProperty("sha256").GetString());
            Assert.Equal(expectedBytes, File.ReadAllBytes(Path.Combine(context, "packages", task.Id.Value, skill.RelativePath)));
            var packaged = Assert.Single(task.LastDispatch.ContextPackageReceipt!.Sections,
                section => section.LogicalIdentity == "context/" + skill.RelativePath.Replace('\\', '/'));
            Assert.Equal(entry.GetProperty("sha256").GetString(), packaged.ContentHash);
        });
        Assert.All(Directory.GetFiles(workingDirectory, "*", SearchOption.AllDirectories).Except(before),
            path => Assert.StartsWith(".orchestrator-context" + Path.DirectorySeparatorChar,
                Path.GetRelativePath(workingDirectory, path), StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(workingDirectory, ".agents")));
    }

    [Xunit.Fact]
    public void PlannerPreflightUsesOrchestratorFallback()
    {
        var (goal, task, agents, workingDirectory, skillDirectory, _) = CreatePlanner();
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
        var (goal, task, agents, workingDirectory, skillDirectory, _) = CreatePlanner();
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
        var (goal, task, _, workingDirectory, skillDirectory, _) = CreatePlanner();
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

        var context = WorkerContextArtifacts.Write(goal, task, workingDirectory,
            orchestratorSkillDirectory: skillDirectory);
        var instructions = File.ReadAllText(Path.Combine(context, "selected-skills.md"));
        Assert.Contains($"Path: {local.RelativePath}", instructions, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(context, local.RelativePath)));
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(context, "artifact-registry.json")));
        Assert.DoesNotContain(registry.RootElement.GetProperty("artifacts").EnumerateArray(),
            artifact => artifact.GetProperty("path").GetString() == local.RelativePath.Replace('\\', '/'));
        Assert.All(requirements.Where(skill => skill != local), skill =>
            Assert.Equal(File.ReadAllBytes(skill.ResolvedPath!), File.ReadAllBytes(Path.Combine(context, skill.RelativePath))));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("absent")]
    public void UnconfiguredOrAbsentFallbackKeepsMissingSkills(string? directory)
    {
        var (goal, task, agents, workingDirectory, _, _) = CreatePlanner();
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
        string SkillDirectory, AgentOrchestratorKernel Kernel) CreatePlanner(string profile = "codex-cli")
    {
        var workingDirectory = Path.Combine(root, "managed-repository");
        Directory.CreateDirectory(workingDirectory);
        RunGit(workingDirectory, ["init", "--quiet"], DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan dotnet build changes.", AgentRole.Planner, "Verify inputs.");
        var goal = kernel.CreateGoal("Map bounded inputs", [task]);
        var provider = profile == "claude-cli" ? "Anthropic" : "OpenAI";
        var model = profile == "claude-cli" ? AgentCatalog.AnthropicComplexModelName : AgentCatalog.OpenAiSubscriptionModelAlias;
        var agent = new AgentDefinition(new AgentId("planner"), "Planner", AgentRole.Planner,
            new ModelProfile(provider, model, ModelCapability.Text,
                SubscriptionMode.ApiKey, "medium"), ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profile, model, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        return (goal, task, [agent], workingDirectory, Path.Combine(root, "orchestrator", ".agents", "skills"), kernel);
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
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# {name}\r\n" + new string('x', 40_000) + "\r\nFull skill tail: Ω\r\n");
        }
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
