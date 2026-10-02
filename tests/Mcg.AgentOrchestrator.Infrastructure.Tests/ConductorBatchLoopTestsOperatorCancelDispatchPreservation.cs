using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsOperatorCancelDispatchPreservation : ConductorBatchLoopTests
{
    private const string DispositionPrefix =
        "CANCEL_DISPOSITION task_status=Cancelled redispatch=awaits-operator-retry preservation=";

    public ConductorBatchLoopTestsOperatorCancelDispatchPreservation(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public async Task DirtyWorktreeIsStashedAndDispositionRecordedAfterEvidence()
    {
        using var repository = new TemporaryRepository();
        var (kernel, goal, task) = RunningDeveloper(repository.Path);
        repository.WriteWorkerEdits();
        Assert.Contains("tracked.txt", Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));
        Assert.Contains("untracked.txt", Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));

        await ApplyCancelTick(kernel, goal, task);

        Assert.Empty(Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));
        var note = Disposition(goal, task);
        var match = Regex.Match(note, "^" + Regex.Escape(DispositionPrefix) + "preserved=([0-9a-f]{40})$");
        Assert.True(match.Success, note);
        var sha = match.Groups[1].Value;
        var message = $"operator-cancel-dispatch-{goal.Id.Value[..8]}-{task.Id.Value[..8]}-{task.LastProcess!.ProcessId}";
        Assert.Equal($"{sha}\tOn goal/{goal.Id.Value[..8]}: {message}",
            Git(repository.Path, "stash", "list", "--format=%H%x09%gs"));
        var stashedFiles = Git(repository.Path, "stash", "show", "--include-untracked", "--name-only", sha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("tracked.txt", stashedFiles);
        Assert.Contains("untracked.txt", stashedFiles);
        Assert.Equal("modified tracked content", Git(repository.Path, "show", $"{sha}:tracked.txt"));
        Assert.Equal("untracked worker content", Git(repository.Path, "show", $"{sha}^3:untracked.txt"));
        var timeline = goal.Timeline.ToList();
        var evidence = Assert.Single(timeline, evt => evt.TaskId == task.Id &&
            evt.Message.StartsWith("CANCELLATION_CANDIDATE_EVIDENCE", StringComparison.Ordinal));
        Assert.Contains("kind=Dirty", evidence.Message);
        Assert.Contains("tracked.txt", evidence.Message);
        Assert.True(timeline.IndexOf(evidence) < timeline.FindIndex(evt => evt.Message == note));
    }

    [Xunit.Fact]
    public async Task CleanWorktreeRecordsCleanNoEditsAndCreatesNoStash()
    {
        using var repository = new TemporaryRepository();
        var (kernel, goal, task) = RunningDeveloper(repository.Path);
        Assert.Empty(Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));

        await ApplyCancelTick(kernel, goal, task);

        Assert.Equal(DispositionPrefix + "clean-no-edits", Disposition(goal, task));
        Assert.Empty(Git(repository.Path, "stash", "list"));
        Assert.Empty(Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));
    }

    [Xunit.Fact]
    public async Task UnpreservableWorktreeRecordsFailedAndStillCancels()
    {
        var missingPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"missing-cancel-repo-{Guid.NewGuid():N}");
        var (kernel, goal, task) = RunningDeveloper(missingPath, captureCandidate: false);
        Assert.False(Directory.Exists(missingPath));

        await ApplyCancelTick(kernel, goal, task);

        Assert.StartsWith(DispositionPrefix + "failed=status:", Disposition(goal, task));
        Assert.DoesNotContain('\n', Disposition(goal, task));
        Assert.DoesNotContain('\r', Disposition(goal, task));
        Assert.False(Directory.Exists(missingPath));
    }

    [Xunit.Fact]
    public async Task FailedStashLeavesTrackedAndUntrackedEditsUntouched()
    {
        using var repository = new TemporaryRepository();
        var (kernel, goal, task) = RunningDeveloper(repository.Path);
        repository.WriteWorkerEdits();
        var before = Git(repository.Path, "status", "--porcelain", "--untracked-files=all");
        var indexLock = Git(repository.Path, "rev-parse", "--git-path", "index.lock");
        File.WriteAllText(indexLock, "test owns this lock");

        await ApplyCancelTick(kernel, goal, task);

        Assert.StartsWith(DispositionPrefix + "failed=stash:", Disposition(goal, task));
        Assert.Equal(before, Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));
        Assert.Empty(Git(repository.Path, "stash", "list"));
        Assert.Equal("modified tracked content", File.ReadAllText(System.IO.Path.Combine(repository.Path, "tracked.txt")));
        Assert.Equal("untracked worker content", File.ReadAllText(System.IO.Path.Combine(repository.Path, "untracked.txt")));
        Assert.Equal("test owns this lock", File.ReadAllText(indexLock));
    }

    [Xunit.Fact]
    public async Task AlreadyConductorCancelledTaskIsNotPreservedOrNoted()
    {
        using var repository = new TemporaryRepository();
        var (kernel, goal, task) = RunningDeveloper(repository.Path);
        repository.WriteWorkerEdits();
        var before = Git(repository.Path, "status", "--porcelain", "--untracked-files=all");
        kernel.RecordTaskProcessCancelled(goal.Id, task.Id, task.LastProcess! with
        {
            CompletedAt = task.LastProcess!.StartedAt.AddSeconds(1),
            WasCancelled = true,
            WasCancelledByConductor = true
        });
        Assert.True(task.WasCancelledByConductor);

        var cancelCalls = await ApplyCancelTick(kernel, goal, task);

        Assert.Equal(0, cancelCalls);
        Assert.Empty(Git(repository.Path, "stash", "list"));
        Assert.Equal(before, Git(repository.Path, "status", "--porcelain", "--untracked-files=all"));
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.StartsWith("CANCEL_DISPOSITION", StringComparison.Ordinal));
    }

    private static string Disposition(Goal goal, TaskSpec task) =>
        Assert.Single(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith("CANCEL_DISPOSITION", StringComparison.Ordinal)).Message;

    private static async Task<int> ApplyCancelTick(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        var store = new CancelDispatchIntentTestStore();
        var intent = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.Intent(goal, task);
        await store.EnqueueAsync(intent);
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, tryKillOwnedProcess: _ => false)
        {
            OperatorIntents = store,
            RequeueRefusalLog = _ => { }
        };
        var cancelCalls = 0;
        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (wk, goalId, taskId) =>
            {
                cancelCalls++;
                return runner.CancelLatestProcess(wk, goalId, taskId);
            }
        };
        var dispatchStarts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { dispatchStarts++; return DispatchStartOutcome.Started(); });

        new ConductorBatchLoop(
            recoverInterruptedDispatches: wk => runner.RequeueInterruptedDispatches(wk),
            operatorIntents: coordinator).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, persistGoalTick: (_, _) => { });

        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        Assert.True(task.LastProcess!.WasCancelled);
        Assert.False(task.WasCancelledByConductor);
        Assert.Equal(0, dispatchStarts);
        return cancelCalls;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningDeveloper(
        string workingDirectory, bool captureCandidate = true)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve operator-cancelled edits.",
            [new TaskSpec(TaskId.New(), "Developer", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var now = DateTimeOffset.UtcNow;
        string? head = null;
        if (captureCandidate)
        {
            Git(workingDirectory, "checkout", "-b", $"goal/{goal.Id.Value[..8]}");
            head = Git(workingDirectory, "rev-parse", "HEAD");
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("worker", "prior", workingDirectory, now, BaseCommit: head, ResultCommit: head));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Prior candidate completed.");
            kernel.RetryTask(goal.Id, task.Id, "Inspect candidate.");
        }
        var dispatchedAt = now.AddMinutes(1);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "worker command", workingDirectory, dispatchedAt,
                BaseCommit: head, WorktreeHeadSha: head,
                DirtyStateHash: captureCandidate ? Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant() : null));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4242, "worker command", workingDirectory,
                System.IO.Path.Combine(workingDirectory, "out.log"), System.IO.Path.Combine(workingDirectory, "err.log"),
                System.IO.Path.Combine(workingDirectory, "missing-exit.txt"), dispatchedAt, null, null));
        return (kernel, goal, task);
    }

    private static string Git(string path, params string[] arguments)
    {
        var result = GitCli.Run(path, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output.Trim();
    }

    private sealed class TemporaryRepository : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cancel-preservation-{Guid.NewGuid():N}");
        public string Path { get; }

        public TemporaryRepository()
        {
            var repository = System.IO.Path.Combine(_root, "repository");
            Path = System.IO.Path.Combine(_root, "worktree");
            Directory.CreateDirectory(repository);
            Git(repository, "init");
            Git(repository, "config", "user.email", "test@example.test");
            Git(repository, "config", "user.name", "Test User");
            File.WriteAllText(System.IO.Path.Combine(repository, "tracked.txt"), "original tracked content");
            Git(repository, "add", "tracked.txt");
            Git(repository, "-c", "commit.gpgsign=false", "commit", "-m", "initial");
            // A linked worktree matches the dispatch inspector's .git-file contract.
            Git(repository, "worktree", "add", "-b", "worker", Path);
        }

        public void WriteWorkerEdits()
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "tracked.txt"), "modified tracked content");
            File.WriteAllText(System.IO.Path.Combine(Path, "untracked.txt"), "untracked worker content");
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
