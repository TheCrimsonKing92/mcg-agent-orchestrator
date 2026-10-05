using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every repository, worktree and process artifact is owned by this fixture;
// process liveness/termination and time are injected, and no worker is spawned.
internal sealed class FailedRoundCheckpointFixture : WorkerDispatchTestSupport, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"failed-round-{Guid.NewGuid():N}");
    internal string Repository { get; }
    internal string Worktree { get; }
    internal string StateDatabase => Path.Combine(_root, "state.db");
    internal AgentOrchestratorKernel Kernel { get; }
    internal Goal Goal { get; }
    internal TaskSpec Worker { get; }
    internal TaskProcessRecord Process { get; }
    internal DateTimeOffset DispatchedAt { get; }
    internal BackgroundDispatchRunner Runner { get; }
    internal IClock Clock { get; }

    internal FailedRoundCheckpointFixture(AgentRole role = AgentRole.Developer, bool lowIntegrity = false)
    {
        Repository = Path.Combine(_root, "repository");
        Directory.CreateDirectory(Repository);
        Git(Repository, "init", "-b", "main");
        Git(Repository, "config", "user.email", "test@example.test");
        Git(Repository, "config", "user.name", "Checkpoint Test");
        Git(Repository, "config", "commit.gpgsign", "false");
        Git(Repository, "config", "core.hooksPath", Path.Combine(_root, "no-hooks"));
        File.WriteAllText(Path.Combine(Repository, "seed.txt"), "seed");
        File.WriteAllText(Path.Combine(Repository, ".gitignore"), "ignored.txt\n");
        Git(Repository, "add", ".");
        var initial = InfrastructureTestSupport.RunGitProbe(Repository, ["commit", "-m", "Seed"],
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z"
            });
        Assert.Equal(0, initial.ExitCode);
        DispatchedAt = DateTimeOffset.Parse(Git(Repository, "log", "-1", "--format=%cI")).AddSeconds(1);
        var clock = new TestClock(DispatchedAt.AddMinutes(1));
        Clock = clock;
        Kernel = new AgentOrchestratorKernel(clock);
        Worker = new TaskSpec(TaskId.New(), "Preserve failed edits", role);
        Goal = Kernel.CreateGoal("Failed-round checkpoint", [Worker]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Worktree = GoalWorktrees.Ensure(Repository, Goal.Id);
        var head = Git(Worktree, "rev-parse", "HEAD");
        Kernel.RecordTaskDispatch(Goal.Id, Worker.Id,
            new TaskDispatchRecord("codex-cli", "worker command", Worktree, DispatchedAt,
                BaseCommit: head, WorktreeHeadSha: head, SandboxLowIntegrity: lowIntegrity));
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        Process = new TaskProcessRecord(999999, "worker command", Worktree,
            Path.Combine(logs, "out.log"), Path.Combine(logs, "err.log"), Path.Combine(logs, "exit.txt"),
            DispatchedAt, null, null);
        Kernel.RecordTaskProcessStarted(Goal.Id, Worker.Id, Process);
        Runner = new BackgroundDispatchRunner(clock, isStillRunning: _ => false,
            tryKillOwnedProcess: _ => false, findBuildDaemons: _ => [], tryKillBuildDaemon: _ => false,
            resolveWorkerBuildReceiptPath: _ => Path.Combine(_root, "absent-build-receipt.json"));
    }

    internal string DispatchId => BackgroundDispatchRunner.BuildDispatchId(
        Goal.Id, Worker.Id, Worker.DispatchHistory[^1]);

    internal void WriteEdits()
    {
        File.WriteAllText(Path.Combine(Worktree, "seed.txt"), "modified tracked content");
        File.WriteAllText(Path.Combine(Worktree, "new-untracked.txt"), "new untracked content");
    }

    internal void Complete(int exitCode = 1, string output = "", string error = "worker failed")
    {
        File.WriteAllText(Process.StandardOutputPath, output);
        File.WriteAllText(Process.StandardErrorPath, error);
        File.WriteAllText(Process.ExitCodePath, exitCode.ToString());
        WriteHeartbeat(Process, DispatchedAt.AddMinutes(1), DispatchedAt.AddMinutes(1), "completed",
            new FileInfo(Process.StandardOutputPath).Length, new FileInfo(Process.StandardErrorPath).Length,
            childPid: null, exitFileExists: true);
        Runner.RefreshLatestProcess(Kernel, Goal.Id, Worker.Id);
    }

    internal void Retry() => Kernel.RetryTask(Goal.Id, Worker.Id, "Resume preserved edits.",
        RetryCause.EnvironmentApparatusFailure);

    internal ConductorDriver Driver(Action<Goal> onDispatch, Action<string>? onEscalation = null) => new(
        getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningPaidWorkerCount: () => 0,
        createWorkspace: _ => "unused",
        dispatchAndStart: goal => { onDispatch(goal); return DispatchStartOutcome.Started(); },
        startRecordedDispatches: null, buildServerShutdown: null,
        runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
        runAdvisorySemanticAcceptance: null, retryTask: null, recordTaskNote: null,
        recordCriterionRetryFeedback: null, clearCriterionRetryFeedback: null,
        rebaseOntoMain: _ => new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.AlreadyFastForwardable,
            GoalWorktrees.BranchName(Goal.Id), "current", [], null),
        land: (goal, _) => new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(),
            LandingExecutor.IntegrationBranchName, MainAdvanced: true, "landed"),
        afterSuccessfulLanding: null, record: _ => { },
        cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
        writeEscalation: (_, _, reason) => onEscalation?.Invoke(reason), classifyChangeRisk: _ => null,
        integrateMainBeforeDeveloperDispatch:
            new FailedRoundCheckpointPreDispatch(Kernel, Repository).IntegrateMainBeforeDeveloperDispatch);

    internal static string Git(string path, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(path, arguments);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput.Trim();
    }

    public void Dispose()
    {
        Git(Repository, "worktree", "remove", "--force", Worktree);
        // _root is the exact unique temporary directory allocated by this fixture.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
