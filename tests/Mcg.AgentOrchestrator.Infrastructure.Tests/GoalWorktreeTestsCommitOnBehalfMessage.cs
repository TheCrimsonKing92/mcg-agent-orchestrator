using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsCommitOnBehalfMessage : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void CommitOnBehalfWritesSubjectAndIdentityTrailersToGit()
    {
        const string workerOutput = """
            WORKER_RESULT:
            files: seed.txt
            summary: Appended the worker edit to seed.txt
            commands: none
            tests: deferred - GoalWorktreeTestsCommitOnBehalfMessage
            commit: none
            blockers: none
            assigned_scope_complete: true
            model_fit: fixture/static - adequate - commit-on-behalf test fixture
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        RunCompletedWorkerScenario(workerOutput, (goal, task, worktree, seedCommit) =>
        {
            AssertTaskStatus(goal, task, WorkTaskStatus.Completed);
            Assert.Contains("Orchestrator committed the worker's verified worktree edits",
                task.LastVerification!.StandardError, StringComparison.Ordinal);
            Assert.Equal(seedCommit, RunGitOutput(worktree, "rev-parse", "HEAD^"));
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--porcelain"));
            Assert.Equal(
                $"Developer({goal.Id.Value[..8]}): Readable commit subjects",
                RunGitOutput(worktree, "log", "-1", "--format=%s"));
            var body = RunGitOutput(worktree, "log", "-1", "--format=%B");
            Assert.Contains($"Goal: {goal.Id.Value}", body, StringComparison.Ordinal);
            Assert.Contains($"Task-Id: {task.Id.Value}", body, StringComparison.Ordinal);
            Assert.Contains(
                $"Dispatch: {BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, task.LastDispatch!)}",
                body, StringComparison.Ordinal);
        });
    }

    [Xunit.Fact]
    public void CommitOnBehalfRefusesUnverifiedDirtyEditsWithPastDatedSeed()
    {
        RunCompletedWorkerScenario("Completed implementation.", (goal, task, worktree, seedCommit) =>
        {
            AssertTaskStatus(goal, task, WorkTaskStatus.Failed);
            Assert.Equal(1, task.LastVerification!.ExitCode);
            Assert.Contains("Developer/Tester dispatch exited 0 but left the worktree dirty.",
                task.LastVerification.StandardError, StringComparison.Ordinal);
            Assert.Equal("M seed.txt", RunGitOutput(worktree, "status", "--porcelain"));
            Assert.Equal(seedCommit, RunGitOutput(worktree, "rev-parse", "HEAD"));
        });
    }

    // Each scenario owns its repository and state database; neither uses the shared seed template.
    private static void RunCompletedWorkerScenario(
        string workerOutput,
        Action<Goal, TaskSpec, string, string> assertOutcome)
    {
        var repo = PastDatedSeedRepository.Create();
        try
        {
            _ = CreateMigratedStateRepository(OrchestratorWorkspace.ForDirectory(repo).SqliteStatePath);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var kernel = new AgentOrchestratorKernel();
            var taskSpec = new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer);
            var goal = kernel.CreateGoal("# Readable commit subjects\nDetails", [taskSpec]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var dispatchedAt = clock.UtcNow.AddMinutes(-1);
            var seedCommit = RunGitOutput(worktree, "rev-parse", "HEAD");
            Assert.Equal("1", RunGitOutput(worktree, "rev-list", "--count", "HEAD"));
            Assert.Equal(PastDatedSeedRepository.SeedCommitTime,
                DateTimeOffset.Parse(RunGitOutput(worktree, "log", "-1", "--format=%cI")));
            Assert.Equal(PastDatedSeedRepository.SeedCommitTime,
                DateTimeOffset.Parse(RunGitOutput(worktree, "log", "-1", "--format=%aI")));
            Assert.Equal(string.Empty,
                RunGitOutput(worktree, "log", "--format=%H", $"--since={dispatchedAt:O}"));
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "codex exec prompt", worktree, dispatchedAt));
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "worker edit");
            var logs = Path.Combine(repo, "logs");
            Directory.CreateDirectory(logs);
            var stdout = Path.Combine(logs, "developer.out.log");
            var stderr = Path.Combine(logs, "developer.err.log");
            var exit = Path.Combine(logs, "developer.exit.txt");
            File.WriteAllText(stdout, workerOutput);
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
                999999, "codex exec prompt", worktree, stdout, stderr, exit, dispatchedAt, null, null));

            new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
                .RefreshLatestProcess(kernel, goal.Id, task.Id);

            assertOutcome(goal, task, worktree, seedCommit);
        }
        finally
        {
            PastDatedSeedRepository.Delete(repo);
        }
    }

    private static void AssertTaskStatus(Goal goal, TaskSpec task, WorkTaskStatus expected) =>
        Assert.True(task.Status == expected,
            $"Expected status: {expected}; actual status: {task.Status}; " +
            $"exit code: {task.LastVerification?.ExitCode}; " +
            $"verification: {task.LastVerification?.StandardError ?? "missing verification"}; " +
            $"last task event: {goal.Timeline.LastOrDefault(evt => evt.TaskId == task.Id)?.Message}");

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
