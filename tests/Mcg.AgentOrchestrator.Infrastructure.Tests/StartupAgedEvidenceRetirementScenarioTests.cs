using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Real-git fixtures own unique roots and hooks; startup uses the CLI environment collection.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class StartupAgedEvidenceRetirementScenarioTests : CliCommandTestBase
{
    [Fact]
    public void StartupSweep_TwoPasses_RetiresReclaimedEvidenceBranchAndSettledBackoffAndKeepsUnprovenGoals()
    {
        var root = CreateAcceptanceRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".mcg-sandbox/\n.orchestrator/\n.orchestrator-worktrees/\n");
            RunGit(root, "add", ".gitignore");
            RunGit(root, "commit", "-m", "Ignore test runtime state");
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var ids = new[] { "A", "U", "R", "S", "K" }.Select(name =>
            {
                var goal = kernel.CreateGoal(name, [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                kernel.ReportTaskProgress(goal.Id, goal.Tasks[0].Id, WorkTaskStatus.Completed, "done");
                return goal.Id;
            }).ToArray();
            foreach (var id in ids)
                kernel = WithGoalStatus(kernel, id, id == ids[4] ? GoalStatus.Cancelled : GoalStatus.Completed);
            var trainStore = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var cohortStore = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var tips = new Dictionary<GoalId, string>();
            var landings = new Dictionary<GoalId, string>();
            var receipts = new Dictionary<GoalId, MergeTrainReceipt>();
            foreach (var id in ids.Take(3))
            {
                var name = kernel.GetGoal(id).Objective;
                var file = name + ".txt";
                var path = CommitGoalWork(root, id, file, "first change");
                File.AppendAllText(Path.Combine(path, file), "\nsecond change");
                RunGit(path, "add", file);
                RunGit(path, "commit", "-m", "Second goal change");
                var tip = RunGitOutput(path, "rev-parse", "HEAD").Trim();
                string landing;
                if (name == "R")
                {
                    RunGit(root, "checkout", "-b", "side-landing", tip);
                    RunGit(root, "commit", "--allow-empty", "-m", "Landing outside main");
                    landing = RunGitOutput(root, "rev-parse", "HEAD").Trim();
                    RunGit(root, "checkout", "main");
                    Assert.Equal(1, GitCli.Run(root, "merge-base", "--is-ancestor", landing, "main").ExitCode);
                }
                else
                {
                    RunGit(root, "merge", "--squash", GoalWorktrees.BranchName(id));
                    RunGit(root, "commit", "-m", $"Squash {name}");
                    landing = RunGitOutput(root, "rev-parse", "HEAD").Trim();
                }
                Assert.Contains(RunGitOutput(root, "cherry", "main", GoalWorktrees.BranchName(id)).Split('\n'),
                    line => line.StartsWith("+ ", StringComparison.Ordinal));
                GoalOperationJournal.RecordLandingIntent(root, kernel.GetGoal(id), GoalWorktrees.BranchName(id), "main", landing, "test");
                var receipt = SaveFinalizedReceipt(root, trainStore, id, tip, landing);
                receipts[id] = receipt;
                tips[id] = tip;
                landings[id] = landing;
                if (name == "U")
                {
                    File.WriteAllText(Path.Combine(path, "extra.txt"), "unlanded work");
                    RunGit(path, "add", "extra.txt");
                    RunGit(path, "commit", "-m", "Advance after receipt");
                    tips[id] = RunGitOutput(path, "rev-parse", "HEAD").Trim();
                    Assert.NotEqual(tip, tips[id]);
                }
                if (name != "R")
                {
                    Assert.NotEmpty(receipt.GateTestResultPaths);
                    foreach (var capturedTrx in receipt.GateTestResultPaths)
                    {
                        Assert.True(File.Exists(capturedTrx));
                        File.Delete(capturedTrx);
                        Assert.False(File.Exists(capturedTrx));
                    }
                    Assert.Empty(trainStore.ReadPassedReceiptsForGoal(id));
                }
                else
                {
                    Assert.All(receipt.GateTestResultPaths, trx => Assert.True(File.Exists(trx)));
                }
                RunGit(root, "worktree", "remove", "--force", path);
                Assert.False(Directory.Exists(path));
                Assert.Null(GoalWorktrees.TryResolve(root, id));
                Assert.Empty(cohortStore.ReadFinalizedLandingsForGoal(id));
            }

            var aged = ids[0];
            var advanced = ids[1];
            var offMain = ids[2];
            var settled = ids[3];
            var cancelled = ids[4];
            var cancelledTip = RunGitOutput(root, "rev-parse", "main").Trim();
            RunGit(root, "branch", GoalWorktrees.BranchName(cancelled), cancelledTip);
            foreach (var id in new[] { settled, cancelled })
                GoalOperationJournal.RecordTerminalDisposition(root, kernel.GetGoal(id),
                    new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "retired"));
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var hooks = CreateIsolatedCleanupContext(workspace, new GoalWorktreeCleanupHooks
            {
                CleanupUtcNow = () => now,
                ResetSandboxAcl = (_, _) => { },
                FindLockHoldersForCleanup = _ => [],
                ProcessCommandLineSnapshot = _ => new ProcessCommandLineSnapshot(new Dictionary<int, string>())
            }).Hooks;
            foreach (var id in new[] { settled, cancelled })
            {
                Assert.False(Directory.Exists(GoalWorktrees.WorktreePath(root, id)));
                Assert.NotNull(GoalWorktrees.RecordGoalCleanupNeeded(root, id, "remove:goal-mark-landed-deferred", hooks));
            }
            now = now.AddYears(1);
            Assert.Equal(TimeSpan.Zero, GoalWorktrees.TryGetCleanupBackoff(root, settled, hooks)!.RemainingWait);
            Assert.Equal(TimeSpan.Zero, GoalWorktrees.TryGetCleanupBackoff(root, cancelled, hooks)!.RemainingWait);
            var cancelledBackoff = GoalWorktrees.TryGetCleanupBackoff(root, cancelled, hooks);
            var firstLines = new List<string>();

            var first = StartupTerminalSweep.Run(Candidates(kernel, workspace, hooks), workspace, null, hooks, writeLine: firstLines.Add);

            Assert.NotEqual(0, GitCli.Run(root, "show-ref", "--verify", $"refs/heads/{GoalWorktrees.BranchName(aged)}").ExitCode);
            var retirement = Assert.Single(first.Goals.Single(goal => goal.GoalId == aged).Repairs,
                repair => repair.Kind == "completed-branch-landed-retired");
            Assert.Contains($"receipt=merge-train-receipt:{receipts[aged].ReceiptId}", retirement.Evidence, StringComparison.Ordinal);
            Assert.Contains($"candidateRevision={tips[aged]}", retirement.Evidence, StringComparison.Ordinal);
            Assert.Contains($"landingCommit={landings[aged]}", retirement.Evidence, StringComparison.Ordinal);
            foreach (var id in new[] { advanced, offMain })
            {
                Assert.Equal(tips[id], RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(id)).Trim());
                Assert.Contains(first.Goals.Single(goal => goal.GoalId == id).Blockers, blocker => blocker.Kind == "completed-branch-unmerged");
            }
            Assert.Null(GoalWorktrees.TryGetCleanupBackoff(root, settled, hooks));
            Assert.Single(first.Goals.Single(goal => goal.GoalId == settled).Repairs,
                repair => repair.Kind == "settled-cleanup-backoff-cleared");
            Assert.Equal(cancelledTip, RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(cancelled)).Trim());
            Assert.Equal(cancelledBackoff, GoalWorktrees.TryGetCleanupBackoff(root, cancelled, hooks));
            Assert.Contains(" landed_branches_retired=1 ", Assert.Single(firstLines), StringComparison.Ordinal);
            Assert.StartsWith(StartupSweepStepsLine.Prefix, firstLines[0], StringComparison.Ordinal);

            var secondLines = new List<string>();
            var second = StartupTerminalSweep.Run(Candidates(kernel, workspace, hooks), workspace, null, hooks, writeLine: secondLines.Add);

            Assert.DoesNotContain(aged, second.ExplicitlySweptGoalIds);
            Assert.DoesNotContain(settled, second.ExplicitlySweptGoalIds);
            foreach (var id in new[] { advanced, offMain, cancelled })
                Assert.Contains(id, second.ExplicitlySweptGoalIds);
            Assert.Contains(" landed_branches_retired=0 ", Assert.Single(secondLines), StringComparison.Ordinal);
            Assert.Equal(cancelledBackoff, GoalWorktrees.TryGetCleanupBackoff(root, cancelled, hooks));
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }

    private static AgentOrchestratorKernel Candidates(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, GoalWorktreeCleanupHooks hooks)
    {
        var index = GoalGitFactIndex.Build(workspace.ExecutionDirectory, workspace.IntegrationBranch);
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Where(goal => TerminalGoalSweep.IsTerminalSweepStatus(goal.Status) &&
                (index.HasGoalBranch(GoalWorktrees.BranchName(new GoalId(goal.Id))) ||
                 GoalWorktrees.TryResolve(workspace.ExecutionDirectory, new GoalId(goal.Id)) is not null ||
                 GoalWorktrees.TryGetCleanupBackoff(workspace.ExecutionDirectory, new GoalId(goal.Id), hooks) is not null)).ToArray()
        });
    }

    private MergeTrainReceipt SaveFinalizedReceipt(string root, MergeTrainAcceptanceStore store, GoalId id, string tip, string landing)
    {
        MergeTrainMemberBinding Member(GoalId memberId, string file) => new MergeTrainMemberBinding(
            memberId, tip, tip, [file], [file], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString()).WithRebasedHead(tip);
        var tree = RunGitOutput(root, "rev-parse", $"{landing}^{{tree}}").Trim();
        var identity = MergeTrainIdentity.Create([Member(id, "src/A.cs"), Member(GoalId.New(), "src/B.cs")], landing, tree, "manifest-v1");
        var trx = Path.Combine(root, ".orchestrator", $"{id.Value}.trx");
        Directory.CreateDirectory(Path.GetDirectoryName(trx)!);
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        var receipt = store.SaveGateReceipt(new MergeTrainReceipt($"receipt-{id.Value}", identity, MergeTrainGateOutcome.Passed,
            DateTimeOffset.UtcNow, 10, [], 0, [trx], ValidForLanding: true));
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.DoesNotContain(trx, receipt.GateTestResultPaths);
        store.PrepareLanding(receipt, landing, null);
        Assert.Equal(2, store.FinalizeLanding(identity.Value, receipt.ReceiptId).Count);
        store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
        Assert.Contains(identity.Value, store.ReadFinalizedLandingTrainIds());
        Assert.Contains(store.ReadPassedReceiptsForGoal(id), saved => saved.ReceiptId == receipt.ReceiptId);
        return receipt;
    }
}
