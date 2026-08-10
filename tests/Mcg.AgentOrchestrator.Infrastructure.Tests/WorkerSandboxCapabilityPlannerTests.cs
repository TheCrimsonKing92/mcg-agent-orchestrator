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

    [Xunit.Theory(DisplayName = "WorkerSandboxCapabilityPlanner_allows_patch_capable_Codex_file_roles_for_OS_confined_repo_skills")]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void WorkerSandboxCapabilityPlannerAllowsPatchCapableCodexFileRolesForOsConfinedRepoSkills(AgentRole role)
    {
        var (goal, task, profile, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md",
            role);

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

        Assert.True(result.Allowed);
        Assert.Equal("repo-skill-write", result.Status);
        Assert.Contains("OS-confined goal worktree", result.Detail, StringComparison.Ordinal);
        Assert.Contains("orchestrator retains Git metadata and commit authority", result.Detail, StringComparison.Ordinal);
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
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

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
            sandboxOptions: new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

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

    [Xunit.Fact(DisplayName = "WorkerSandboxCapabilityPlanner_preserves_Claude_repo_skill_behavior")]
    public void WorkerSandboxCapabilityPlannerPreservesClaudeRepoSkillBehavior()
    {
        var (goal, task, _, workingDirectory) = CreateEvaluateFixture(
            "Update .agents/skills/systematic-debugging/SKILL.md");
        var profile = WorkerProfileCatalog.Default().GetRequired("claude-cli");

        var result = WorkerSandboxCapabilityPlanner.Evaluate(
            goal,
            task,
            profile,
            workingDirectory,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

        Assert.True(result.Allowed);
        Assert.Equal("repo-skill-write", result.Status);
        Assert.Contains("Claude-compatible", result.Detail, StringComparison.Ordinal);
    }

    private static (Goal goal, TaskSpec task, WorkerProfile profile, string workingDirectory)
        CreateEvaluateFixture(string description, AgentRole role = AgentRole.Developer)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), description, role);
        var goal = kernel.CreateGoal(description, [taskSpec]);
        var task = goal.Tasks.Single();
        var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");
        return (goal, task, profile, workingDirectory);
    }
}
