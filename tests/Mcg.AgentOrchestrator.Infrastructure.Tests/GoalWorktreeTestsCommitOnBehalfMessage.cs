using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsCommitOnBehalfMessage : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void CommitOnBehalfWritesSubjectAndIdentityTrailersToGit()
    {
        var repo = CreateSeededRepository();
        try
        {
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var kernel = new AgentOrchestratorKernel();
            var taskSpec = new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer);
            var goal = kernel.CreateGoal("# Readable commit subjects\nDetails", [taskSpec]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "codex exec prompt", worktree, dispatchedAt));
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "worker edit");
            var logs = Path.Combine(repo, "logs");
            Directory.CreateDirectory(logs);
            var stdout = Path.Combine(logs, "developer.out.log");
            var stderr = Path.Combine(logs, "developer.err.log");
            var exit = Path.Combine(logs, "developer.exit.txt");
            File.WriteAllText(stdout, "Completed implementation.");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
                999999, "codex exec prompt", worktree, stdout, stderr, exit, dispatchedAt, null, null));

            new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
                .RefreshLatestProcess(kernel, goal.Id, task.Id);

            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(
                $"Developer({goal.Id.Value[..8]}): Readable commit subjects",
                RunGitOutput(worktree, "log", "-1", "--format=%s"));
            var body = RunGitOutput(worktree, "log", "-1", "--format=%B");
            Assert.Contains($"Goal: {goal.Id.Value}", body, StringComparison.Ordinal);
            Assert.Contains($"Task-Id: {task.Id.Value}", body, StringComparison.Ordinal);
            Assert.Contains(
                $"Dispatch: {BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, task.LastDispatch!)}",
                body, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
