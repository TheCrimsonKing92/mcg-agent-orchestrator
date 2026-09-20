using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class InterruptedWorkCheckpointAuthorizerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T12:00:00Z");

    [Xunit.Fact]
    public void TypedConnectivityWithQuiescentOwnedTreeCreatesOneNonSuccessCheckpoint()
    {
        using var fixture = Fixture.Create();

        var result = fixture.Authorize();

        Xunit.Assert.True(
            result.Kind == InterruptedWorkCheckpointDispositionKind.Checkpointed,
            $"{result.Token}: {result.Message}");
        Xunit.Assert.NotNull(result.Checkpoint);
        Xunit.Assert.Equal(1, fixture.CommitCountAfterBase());
        Xunit.Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Xunit.Assert.Null(fixture.Task.LastVerification);
        Xunit.Assert.Equal("implementation", File.ReadAllText(Path.Combine(fixture.Root, "src", "Feature.cs")));
        Xunit.Assert.DoesNotContain(".scratch", Git(fixture.Root, "show", "--name-only", "--format=", "HEAD").Output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CrashReplayReconcilesSameCheckpointWithoutDuplicateCommit()
    {
        using var fixture = Fixture.Create();
        var first = fixture.Authorize();
        fixture.RefreshInspection();

        var replay = fixture.Authorize();

        Xunit.Assert.True(
            replay.Kind == InterruptedWorkCheckpointDispositionKind.Reconciled,
            $"{replay.Token}: {replay.Message}");
        Xunit.Assert.Equal(first.Checkpoint!.CheckpointSha, replay.Checkpoint!.CheckpointSha);
        Xunit.Assert.Equal(1, fixture.CommitCountAfterBase());
    }

    [Xunit.Fact]
    public void MalformedMarkerBesideValidCheckpointHoldsInsteadOfReconciling()
    {
        using var fixture = Fixture.Create();
        _ = fixture.Authorize();
        Xunit.Assert.True(Git(
            fixture.Root,
            "commit",
            "--allow-empty",
            "-m",
            "orchestrator malformed checkpoint\n\nOrchestrator-Checkpoint: interrupted-work").Succeeded);
        fixture.RefreshInspection();

        var replay = fixture.Authorize();

        Xunit.Assert.Equal(InterruptedWorkCheckpointDispositionKind.Hold, replay.Kind);
        Xunit.Assert.Equal("checkpoint-hold-provenance-conflict", replay.Token);
        Xunit.Assert.Equal(2, fixture.CommitCountAfterBase());
    }

    [Xunit.Theory]
    [Xunit.InlineData("untyped-cause", "checkpoint-hold-untyped-cause")]
    [Xunit.InlineData("live-descendant", "checkpoint-hold-live-descendant")]
    [Xunit.InlineData("owned-job-unavailable", "checkpoint-hold-owned-job-unavailable")]
    [Xunit.InlineData("identity-missing", "checkpoint-hold-unproven-quiescence")]
    [Xunit.InlineData("stale-heartbeat", "checkpoint-hold-heartbeat-stale")]
    [Xunit.InlineData("content-failure", "checkpoint-hold-content-failure")]
    public void UnsafeOrUnprovenProcessEvidenceHoldsWithoutChangingSource(string scenario, string expectedToken) =>
        AssertUnsafeOrUnprovenCase(scenario, expectedToken);

    [Xunit.Theory]
    [Xunit.InlineData("competing-attempt", "checkpoint-hold-competing-attempt")]
    [Xunit.InlineData("wrong-branch", "checkpoint-hold-branch-mismatch")]
    [Xunit.InlineData("wrong-worktree", "checkpoint-hold-worktree-mismatch")]
    [Xunit.InlineData("unregistered-worktree", "checkpoint-hold-worktree-unowned")]
    [Xunit.InlineData("wrong-role", "checkpoint-hold-role-ineligible")]
    public void UnsafeOrUnprovenOwnershipHoldsWithoutChangingSource(string scenario, string expectedToken) =>
        AssertUnsafeOrUnprovenCase(scenario, expectedToken);

    private static void AssertUnsafeOrUnprovenCase(string scenario, string expectedToken)
    {
        using var fixture = Fixture.Create(scenario);
        var statusBefore = Git(fixture.Root, "status", "--short", "--untracked-files=all").Output;

        var result = fixture.Authorize();

        Xunit.Assert.Equal(InterruptedWorkCheckpointDispositionKind.Hold, result.Kind);
        Xunit.Assert.Equal(expectedToken, result.Token);
        Xunit.Assert.Equal(0, fixture.CommitCountAfterBase());
        Xunit.Assert.Equal(statusBefore, Git(fixture.Root, "status", "--short", "--untracked-files=all").Output);
    }

    [Xunit.Theory]
    [Xunit.InlineData("clean-tree")]
    [Xunit.InlineData("generated-only")]
    public void TreesWithoutSourceEvidenceDoNotCreateCheckpoint(string scenario)
    {
        using var fixture = Fixture.Create(scenario);

        var result = fixture.Authorize();

        Xunit.Assert.Equal(InterruptedWorkCheckpointDispositionKind.Skipped, result.Kind);
        Xunit.Assert.Equal("checkpoint-skip-no-source-evidence", result.Token);
        Xunit.Assert.Equal(0, fixture.CommitCountAfterBase());
    }

    [Xunit.Fact]
    public void CommitFailureRetainsDirtyEditsAndReturnsRecoverableDisposition()
    {
        using var fixture = Fixture.Create(commitFails: true);

        var result = fixture.Authorize();

        Xunit.Assert.Equal(InterruptedWorkCheckpointDispositionKind.CommitFailed, result.Kind);
        Xunit.Assert.Equal("checkpoint-commit-failed-recoverable", result.Token);
        Xunit.Assert.Equal(0, fixture.CommitCountAfterBase());
        Xunit.Assert.Contains("Feature.cs", Git(fixture.Root, "status", "--short").Output, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InterruptedWorkCheckpointAuthorizer _authorizer;
        private readonly InterruptedWorkCheckpointRequest _request;
        private readonly string _cleanupRoot;
        private GoalWorktreeInspectionResult _inspection;

        private Fixture(
            string root,
            TaskSpec task,
            string baseCommit,
            string cleanupRoot,
            InterruptedWorkCheckpointAuthorizer authorizer,
            InterruptedWorkCheckpointRequest request,
            DispatchWorktreeCommitter committer)
        {
            Root = root;
            Task = task;
            BaseCommit = baseCommit;
            _cleanupRoot = cleanupRoot;
            _authorizer = authorizer;
            _request = request;
            Committer = committer;
            _inspection = request.WorktreeInspection!;
        }

        internal string Root { get; }
        internal TaskSpec Task { get; }
        internal string BaseCommit { get; }
        internal DispatchWorktreeCommitter Committer { get; }

        internal static Fixture Create(string scenario = "", bool commitFails = false)
        {
            var cleanupRoot = Path.Combine(Path.GetTempPath(), "mcg-checkpoint-" + Guid.NewGuid().ToString("N"));
            var repositoryRoot = Path.Combine(cleanupRoot, "repository");
            Directory.CreateDirectory(repositoryRoot);
            Xunit.Assert.True(Git(repositoryRoot, "init").Succeeded);
            Xunit.Assert.True(Git(repositoryRoot, "config", "user.email", "checkpoint@example.test").Succeeded);
            Xunit.Assert.True(Git(repositoryRoot, "config", "user.name", "Checkpoint Test").Succeeded);
            var goalId = GoalId.New();
            var root = repositoryRoot;
            if (scenario == "unregistered-worktree")
            {
                Xunit.Assert.True(Git(root, "checkout", "-b", GoalWorktrees.BranchName(goalId)).Succeeded);
            }
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Feature.cs"), "seed");
            Xunit.Assert.True(Git(root, "add", "src/Feature.cs").Succeeded);
            Xunit.Assert.True(Git(root, "commit", "-m", "seed").Succeeded);
            if (scenario != "unregistered-worktree")
            {
                root = GoalWorktrees.WorktreePath(repositoryRoot, goalId);
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                Xunit.Assert.True(Git(
                    repositoryRoot,
                    "worktree",
                    "add",
                    "-b",
                    GoalWorktrees.BranchName(goalId),
                    root).Succeeded);
            }
            var baseCommit = Git(root, "rev-parse", "HEAD").Output.Trim();
            File.WriteAllText(Path.Combine(root, "src", "Feature.cs"), "implementation");
            Directory.CreateDirectory(Path.Combine(root, ".scratch"));
            File.WriteAllText(Path.Combine(root, ".scratch", "noise.txt"), "noise");
            if (scenario is "clean-tree" or "generated-only")
            {
                File.WriteAllText(Path.Combine(root, "src", "Feature.cs"), "seed");
                if (scenario == "clean-tree")
                {
                    Directory.Delete(Path.Combine(root, ".scratch"), recursive: true);
                }
            }

            if (scenario == "wrong-branch")
            {
                Xunit.Assert.True(Git(root, "checkout", "-b", "unrelated-checkpoint-branch").Succeeded);
            }

            var kernel = new AgentOrchestratorKernel(new TestClock(Now));
            var role = scenario == "wrong-role" ? AgentRole.Tester : AgentRole.Developer;
            var goal = kernel.CreateGoal("Checkpoint fixture", [new TaskSpec(TaskId.New(), "Implement", role)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var dispatch = new TaskDispatchRecord(
                "codex-cli",
                "codex exec prompt",
                root,
                Now.AddMinutes(-1),
                WorkerProviderKind: ProviderKind.OpenAICodexCli,
                BaseCommit: baseCommit);
            kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            var processWorkingDirectory = scenario == "wrong-worktree"
                ? Path.Combine(root, "other-worktree")
                : root;
            Directory.CreateDirectory(processWorkingDirectory);
            var processArtifactDirectory = Path.Combine(root, ".scratch", "dispatch");
            Directory.CreateDirectory(processArtifactDirectory);
            var process = new TaskProcessRecord(
                424242,
                dispatch.Command,
                processWorkingDirectory,
                Path.Combine(processArtifactDirectory, "out.log"),
                Path.Combine(processArtifactDirectory, "err.log"),
                Path.Combine(processArtifactDirectory, "worker.exit.txt"),
                dispatch.DispatchedAt,
                null,
                null,
                OwnedProcessIds: [424242]);
            WriteHeartbeat(
                process,
                scenario == "stale-heartbeat" ? Now.AddMinutes(-10) : Now,
                includeIdentity: scenario != "identity-missing");
            var committer = new DispatchWorktreeCommitter(
                runGit: commitFails
                    ? (workingDirectory, args) => args[0] == "commit"
                        ? new GitCli.GitResult(42, string.Empty, "injected checkpoint commit failure")
                        : GitCli.Run(workingDirectory, args)
                    : null,
                fileExists: path => File.Exists(path) || Directory.Exists(path));
            var inspection = committer.InspectGoalWorktree(root, goalId, dispatch.DispatchedAt);
            var authorizer = new InterruptedWorkCheckpointAuthorizer(
                committer,
                new TestClock(Now),
                isProcessRunning: _ => scenario == "live-descendant",
                readCurrentIdentity: processId => new SpawnProcessIdentity(
                    processId,
                    dispatch.DispatchedAt.AddMinutes(1),
                    "worker.exe"),
                readOwnedJob: _ => scenario switch
                {
                    "owned-job-unavailable" => (false, []),
                    "live-descendant" => (true, [424242]),
                    _ => (true, [])
                });
            var request = new InterruptedWorkCheckpointRequest(
                goalId,
                task,
                process,
                new DispatchRecoveryDecision(
                    DispatchRecoveryAction.PreserveInterruptedWork,
                    "preserve-interrupted-work",
                    process.ExitCodePath,
                    "typed provider interruption"),
                scenario == "untyped-cause" ? ProviderFailureKind.Unknown : ProviderFailureKind.Connectivity,
                inspection,
                scenario == "competing-attempt"
                    ? "different-attempt"
                    : BackgroundDispatchRunner.BuildDispatchId(goalId, task.Id, dispatch),
                scenario == "content-failure");
            return new Fixture(root, task, baseCommit, cleanupRoot, authorizer, request, committer);
        }

        internal InterruptedWorkCheckpointDisposition Authorize() =>
            _authorizer.AuthorizeAndCheckpoint(_request with { WorktreeInspection = _inspection });

        internal void RefreshInspection() =>
            _inspection = Committer.InspectGoalWorktree(
                _request.Process.WorkingDirectory,
                _request.GoalId,
                _request.Task.LastDispatch!.DispatchedAt,
                forceRefresh: true);

        internal int CommitCountAfterBase() =>
            int.Parse(Git(Root, "rev-list", "--count", $"{BaseCommit}..HEAD").Output.Trim());

        public void Dispose()
        {
            try { Directory.Delete(_cleanupRoot, recursive: true); } catch { }
        }
    }

    private static void WriteHeartbeat(TaskProcessRecord process, DateTimeOffset observedAt, bool includeIdentity)
    {
        var payload = new
        {
            pid = process.ProcessId,
            childPid = (int?)null,
            ownedPids = new[] { process.ProcessId },
            state = "exited",
            lastObservedAt = observedAt,
            lastProgressAt = observedAt,
            stdoutBytes = 0,
            stderrBytes = 128,
            ownedCpuMs = 100,
            ownedProcessIdentities = includeIdentity
                ? new[] { new { processId = process.ProcessId, startedAt = process.StartedAt, imagePath = "worker.exe" } }
                : []
        };
        File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), JsonSerializer.Serialize(payload));
    }

    private static GitCli.GitResult Git(string root, params string[] args) => GitCli.Run(root, args);

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
