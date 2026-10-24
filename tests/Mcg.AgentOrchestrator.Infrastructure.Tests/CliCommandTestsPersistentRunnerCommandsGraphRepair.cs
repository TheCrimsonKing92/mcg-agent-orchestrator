using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its Git repository and SQLite store. Collection isolates console/environment.
// The verifier's entry/release gates define the interleaving; timeouts only detect hangs.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsGraphRepair : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(true, false, false)]
    [Xunit.InlineData(false, false, false)]
    [Xunit.InlineData(true, true, false)]
    [Xunit.InlineData(false, true, false)]
    [Xunit.InlineData(true, true, true)]
    public async Task Acceptance_GateInterleaving_PreservesGraphAndJournal(
        bool passed, bool append, bool predecessor)
    {
        var root = CreateShortAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Acceptance checkpoint fixture", [task]);
        var otherTask = new TaskSpec(TaskId.New(), "Independent work", AgentRole.Developer);
        var other = kernel.CreateGoal("Independent goal", [otherTask]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskVerification(goal.Id, task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", root,
                DateTimeOffset.Parse("2026-10-21T12:00:00Z")));
        var worktree = CommitGoalWork(root, goal.Id, "feature.txt", "acceptance work");
        await repository.SaveAsync(kernel);
        var verifier = new GatedVerifier(passed);
        Goal? currentGoal = goal;
        Exception? failure = null;
        bool? result = null;
        var command = Task.Run(() =>
        {
            try
            {
                result = AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
                    ["acceptance", goal.Id.Value[..8], "--keep-workspace"],
                    repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                    ref profiles, ref currentGoal, acceptanceVerifier: verifier,
                    acceptanceCleanupContext: CreateIsolatedCleanupContext(workspace));
            }
            catch (Exception ex) { failure = ex; }
        });

        TaskId? inserted = null;
        string? otherState = null;
        try
        {
            await verifier.Entered.Task.WaitAsync(TimeSpan.FromMinutes(2));
            await repository.TransactAsync((stored, _) =>
            {
                if (append)
                    inserted = stored.AddTask(goal.Id, AgentRole.Reviewer, "Concurrent review",
                        beforeRole: predecessor ? AgentRole.Developer : null).Id;
                else
                    stored.RecordTaskNote(goal.Id, task.Id, "independent-target-progress");
                stored.RequestHumanInput(other.Id, otherTask.Id, "Independent operator request");
                otherState = StateJson(stored, other.Id);
                return Task.FromResult((true, true));
            });
            verifier.Release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromMinutes(2));

            var restarted = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
            var durable = restarted.GetGoal(goal.Id);
            if (inserted is { } id)
            {
                // Check the preservation invariant BEFORE checking errors: the raw-save control
                // must fail this assertion, rather than an API format or expected-exception check.
                Assert.True(durable.Tasks.Any(candidate => candidate.Id == id),
                    "Compatible appended task must survive the actual acceptance checkpoint and reload.");
                Assert.Equal(predecessor ? new[] { id, task.Id } : new[] { task.Id, id },
                    durable.Tasks.Select(candidate => candidate.Id));
                Assert.NotEqual(GoalStatus.Completed, durable.Status);
            }
            else
            {
                Assert.Contains(durable.Timeline, entry => entry.Message == "independent-target-progress");
                Assert.Equal(passed ? GoalStatus.Completed : GoalStatus.Verified, durable.Status);
                if (!passed)
                {
                    // Standalone acceptance retains the failed gate without conductor routing.
                    var verdict = Assert.IsType<AcceptanceFailureSummary>(durable.LatestAcceptanceFailure);
                    Assert.Equal(new[] { "controlled-gate" }, verdict.FailedChecks);
                    Assert.False(string.IsNullOrWhiteSpace(verdict.BranchHeadSha));
                    Assert.False(string.IsNullOrWhiteSpace(verdict.MainHeadSha));
                }
                Assert.Null(failure);
                Assert.False(result); // acceptance owns its checkpoints; dispatcher returns no final raw save.
            }
            Assert.Equal(otherState, StateJson(restarted, other.Id));
            Assert.Equal(1, verifier.RunCount);
            var entries = GoalOperationJournal.Read(root, goal.Id).Entries;
            Assert.Contains(entries, entry => entry.HasCandidatePair &&
                entry.AcceptanceOutcome == (passed ? "gate-passed" : "failed"));
            if (passed && !append)
                Assert.Contains(entries, entry => entry.AcceptanceOutcome == "passed");
            if (append && !passed)
            {
                Assert.NotNull(failure);
                Assert.Contains("COMMAND_CHECKPOINT_REJECTED", failure.Message);
                Assert.Contains("Concurrent review", (string)failure.Data["StoredGoalState"]!);
                Assert.NotNull(failure.Data["SubmittedGoalState"]);
            }
            if (append && passed)
            {
                Assert.Null(failure);
                Assert.False(result);
                Assert.DoesNotContain(entries, entry => entry.AcceptanceOutcome == "passed");
                Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
                Assert.False(File.Exists(Path.Combine(root, "feature.txt")));
                Assert.True(File.Exists(Path.Combine(worktree, "feature.txt")));
            }
        }
        finally
        {
            verifier.Release.TrySetResult();
            // Always join before removing this case's worktree/database.
            await command.WaitAsync(TimeSpan.FromMinutes(2));
            _ = GoalWorktrees.Remove(root, goal.Id);
            CleanupAcceptanceRepository(root, null);
        }
    }

    [Xunit.Fact]
    public async Task Checkpoint_OwnProgress_AllowsFollowingStrictCheckpoint()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Shared checkpoint baseline", [task]);
        await repository.SaveAsync(kernel);
        var checkpoints = new CliAcceptanceCheckpoint(repository, workspace, kernel, goal.Id,
            kernel.ExportGoalSnapshot(goal.Id));
        kernel.RecordTaskNote(goal.Id, task.Id, "own-progress-checkpoint");
        checkpoints.Persist(kernel);
        var question = kernel.RequestHumanInput(goal.Id, task.Id, "Target checkpoint request");
        kernel.RecordTaskNote(goal.Id, task.Id, "critical-progress-checkpoint");
        // The exact owner used by the runner must advance its critical baseline on its own save.
        // A separate unchanged baseline would throw DispatchCheckpointConflictException here.
        checkpoints.PersistCritical(kernel, [goal.Id]);
        checkpoints.Persist(kernel);
        var restarted = await repository.LoadAsync();
        Assert.Contains(restarted.HumanInputRequests, request => request.Id == question.Id);
        Assert.Contains(restarted.GetGoal(goal.Id).Timeline, entry => entry.Message == "own-progress-checkpoint");
        Assert.Contains(restarted.GetGoal(goal.Id).Timeline, entry => entry.Message == "critical-progress-checkpoint");
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Checkpoint_NonTargetMutation_RejectsBeforeAnyWrite(bool requestOnly)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Target", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var other = kernel.CreateGoal("Other", [new TaskSpec(TaskId.New(), "Other work", AgentRole.Developer)]);
        await repository.SaveAsync(kernel);
        var beforeTarget = StateJson(kernel, target.Id);
        var beforeOther = StateJson(kernel, other.Id);
        var scoped = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [kernel.ExportGoalSnapshot(target.Id)], []));
        var checkpoints = new CliAcceptanceCheckpoint(repository, workspace, scoped, target.Id,
            scoped.ExportGoalSnapshot(target.Id));
        var reloaded = checkpoints.Reload();
        reloaded.RecordTaskNote(target.Id, target.Tasks[0].Id, "Must not save target first");
        if (requestOnly)
        {
            reloaded.RequestHumanInput(other.Id, other.Tasks[0].Id, "Unowned request");
            // Challenge the request comparison while leaving the other goal's graph/status intact.
            reloaded.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(other.Id));
        }
        else
            reloaded.RecordTaskNote(other.Id, other.Tasks[0].Id, "Unowned progress");

        var failure = Assert.Throws<InvalidOperationException>(() => checkpoints.Persist(reloaded));

        Assert.Contains("non-target goal", failure.Message);
        var restarted = await repository.LoadAsync();
        Assert.Equal(beforeTarget, StateJson(restarted, target.Id));
        Assert.Equal(beforeOther, StateJson(restarted, other.Id));
        Assert.Equal(2, restarted.Goals.Count);
    }

    private static string StateJson(AgentOrchestratorKernel kernel, GoalId id)
    {
        var snapshot = kernel.ExportSnapshot();
        return JsonSerializer.Serialize(new GoalStateSnapshot(kernel.ExportGoalSnapshot(id),
            snapshot.HumanInputRequests.Where(request => request.GoalId == id.Value).ToArray()));
    }

    private sealed class GatedVerifier(bool passed) : IGoalAcceptanceVerifier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RunCount { get; private set; }

        public async Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease, IAcceptanceAttemptExecutionOwner executionOwner)
        {
            RunCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromMinutes(2));
            return new AcceptanceVerificationResult(passed, false, passed ? 0 : 1,
                passed ? "Passed." : "Deliberate gate failure.", ArtifactsPath: Path.Combine(worktreePath, "artifacts"),
                Checks: [new AcceptanceCheckResult("controlled-gate", passed, passed ? 0 : 1, "controlled verdict")]);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath,
            GoalId? goalId, string request, IAcceptanceFocusedVerificationOwner executionOwner,
            int? stableSlotIndex = null, DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false) => throw new InvalidOperationException("Unexpected focused-evidence execution.");
    }
}
