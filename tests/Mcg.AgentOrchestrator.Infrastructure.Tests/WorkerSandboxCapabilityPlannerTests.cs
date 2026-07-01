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
        Assert.Contains(result.Detail, text => text.Contains(".git internals", StringComparison.Ordinal));
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

    private static (Goal goal, TaskSpec task, WorkerProfile profile, string workingDirectory)
        CreateEvaluateFixture(string description)
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), description, AgentRole.Developer);
        var goal = kernel.CreateGoal(description, [taskSpec]);
        var task = goal.Tasks.Single();
        var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");
        return (goal, task, profile, workingDirectory);
    }
}
