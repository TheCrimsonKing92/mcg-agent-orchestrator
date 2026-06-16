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
        var profile = new WorkerProfile("codex-cli", "codex exec --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})");
        return (goal, task, profile, workingDirectory);
    }
}
