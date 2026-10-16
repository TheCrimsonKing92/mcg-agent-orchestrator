using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerSandboxCapabilityPlannerTests
{
    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_allows_brief_mentioning_gitignore")]
    public void WorkerSandboxCapabilityPlannerAllowsBriefMentioningGitignore()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update the .gitignore file to exclude build artifacts");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory);

        Assert.True(result.Allowed);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_allows_brief_mentioning_gitattributes")]
    public void WorkerSandboxCapabilityPlannerAllowsBriefMentioningGitattributes()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Add line-ending rules to .gitattributes for cross-platform builds");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory);

        Assert.True(result.Allowed);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_brief_mentioning_git_directory_slash")]
    public void WorkerSandboxCapabilityPlannerBlocksBriefMentioningGitDirectorySlash()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Read object hashes from .git/objects to verify pack integrity");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
        Assert.Contains(".git internals", result.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_brief_mentioning_dot_git_bare")]
    public void WorkerSandboxCapabilityPlannerBlocksBriefMentioningDotGitBare()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Inspect the bare .git repository metadata");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_allowGitReference_override_permits_repo_root_task")]
    public void WorkerSandboxCapabilityPlannerAllowGitReferenceOverridePermitsRepoRootTask()
    {
        // A repo-root-resolution task legitimately references .git read-only; the default policy
        // false-positive-blocks it, and the operator override permits the vetted case.
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Resolve the repo root by locating the .git directory instead of a hardcoded .sln");

        var blockedByDefault = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory);
        Assert.False(blockedByDefault.Allowed);

        var permittedByOverride = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory, allowGitReference: true);
        Assert.True(permittedByOverride.Allowed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, "codex-cli")]
    [Xunit.InlineData(AgentRole.Tester, "codex-cli")]
    [Xunit.InlineData(AgentRole.Developer, "codex-spark")]
    [Xunit.InlineData(AgentRole.Developer, "codex-oss-cli")]
    public void AllowsCodexRepoSkillWritesWithOsConfinement(
        AgentRole role,
        string profileName)
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md",
            role,
            profileName);

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            commandExists: _ => true);

        Assert.True(result.Allowed, result.Detail);
        Assert.Equal("repo-skill-write", result.Status);
        Assert.Contains("orchestrator OS worker sandbox", result.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_Codex_repo_skills_without_OS_confinement")]
    public void WorkerSandboxCapabilityPlannerBlocksCodexRepoSkillsWithoutOsConfinement()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            commandExists: _ => true);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_non_patch_capable_Codex_repo_skills")]
    public void WorkerSandboxCapabilityPlannerBlocksNonPatchCapableCodexRepoSkills()
    {
        var (goal, task, _, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md");
        var profile = new WorkerProfile(
            "codex-cli",
            "codex exec --sandbox read-only --cd {workingDirectory}");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            commandExists: _ => true);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_keeps_read_only_roles_read_only_for_repo_skills")]
    public void WorkerSandboxCapabilityPlannerKeepsReadOnlyRolesReadOnlyForRepoSkills()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Review .agents/skills/systematic-debugging/SKILL.md",
            AgentRole.Reviewer);

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

        Assert.True(result.Allowed);
        Assert.Equal("read-only", result.Status);
        Assert.DoesNotContain("repo-skill-write", result.Status, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, true, "repo-skill-write")]
    [Xunit.InlineData(false, false, "blocked")]
    public void RequiresOsConfinementForClaudeRepoSkills(
        bool sandboxEnabled,
        bool expectedAllowed,
        string expectedStatus)
    {
        var (goal, task, _, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md");
        var profile = WorkerProfileCatalog.Default().GetRequired("claude-cli");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(sandboxEnabled, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget),
            commandExists: _ => true);

        Assert.Equal(expectedAllowed, result.Allowed);
        Assert.Equal(expectedStatus, result.Status);
    }

    [Xunit.Fact]
    public void BlocksUnknownProviderForRepoSkillWrites()
    {
        var (goal, task, _, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md");
        var profile = new WorkerProfile(
            "custom-worker",
            "claude -p --model test --permission-mode bypassPermissions");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: EnabledSandbox(),
            commandExists: _ => true);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
        Assert.Contains("typed Codex or Claude", result.Detail, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_non_real_Codex_launchers")]
    [Xunit.InlineData("Write-Output {promptPath}", true)]
    [Xunit.InlineData("not-codex exec --sandbox {sandboxMode} --cd {workingDirectory}", true)]
    [Xunit.InlineData("codex exec --sandbox {sandboxMode} --cd {workingDirectory}", false)]
    public void WorkerSandboxCapabilityPlannerBlocksNonRealCodexLaunchers(
        string commandTemplate,
        bool commandExists)
    {
        var (goal, task, _, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md");
        var profile = new WorkerProfile("codex-cli", commandTemplate);

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: EnabledSandbox(),
            commandExists: _ => commandExists);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_combined_repo_skill_and_git_metadata_target")]
    public void WorkerSandboxCapabilityPlannerBlocksCombinedRepoSkillAndGitMetadataTarget()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/example/SKILL.md and write .git/config.");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            allowGitReference: true,
            sandboxOptions: EnabledSandbox(),
            commandExists: _ => true);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
        Assert.Contains(".git internals", result.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_keeps_generic_SKILL_md_target_blocked")]
    public void WorkerSandboxCapabilityPlannerKeepsGenericSkillMdTargetBlocked()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update SKILL.md with the new workflow.");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: EnabledSandbox(),
            commandExists: _ => true);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
        Assert.Contains("include the exact .agents/skills path", result.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_blocks_repo_root_instead_of_linked_worktree")]
    public void WorkerSandboxCapabilityPlannerBlocksRepoRootInsteadOfLinkedWorktree()
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/example/SKILL.md");
        File.Delete(Path.Combine(workingDirectory, ".git"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".git"));

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: EnabledSandbox(),
            commandExists: _ => true);

        Assert.False(result.Allowed);
        Assert.Equal("blocked", result.Status);
        Assert.Contains("goal workspace is required", result.Detail, StringComparison.Ordinal);
    }

    private static (Goal goal, TaskSpec task, WorkerProfile profile, string workingDirectory)
        CreateEvaluateFixture(
            string description,
            AgentRole role = AgentRole.Developer,
            string profileName = "codex-cli")
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal(description, [taskSpec]);
        var task = goal.Tasks.Single();
        var profile = WorkerProfileCatalog.Default().GetRequired(profileName);
        return (goal, task, profile, workingDirectory);
    }

    private static WorkerSandboxOptions EnabledSandbox() =>
        new(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
}
