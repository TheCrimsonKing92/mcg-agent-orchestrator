using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The command test captures console/environment; real-git fixtures own unique roots and hooks.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class StartupLandedBranchCleanupScenarioTests : CliCommandTestBase
{
    [Fact]
    public void StartupSweep_TwoPasses_RetiresLandedBranchAndLongPathWorktreeOnceAndKeepsUnlandedAndDirtyGoals()
    {
        var root = CreateAcceptanceRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".mcg-sandbox/\n.orchestrator/\n.orchestrator-worktrees/\n");
            RunGit(root, "add", ".gitignore");
            RunGit(root, "commit", "-m", "Ignore test runtime state");
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var ids = new[] { "L", "U", "D" }.Select(name =>
            {
                var goal = kernel.CreateGoal(name, [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                kernel.ReportTaskProgress(goal.Id, goal.Tasks[0].Id, WorkTaskStatus.Completed, "done");
                return goal.Id;
            }).ToArray();
            foreach (var id in ids) kernel = WithGoalStatus(kernel, id, GoalStatus.Completed);
            var trainStore = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var worktrees = new Dictionary<GoalId, string>();
            var tips = new Dictionary<GoalId, string>();
            var landingCommits = new Dictionary<GoalId, string>();
            foreach (var id in ids)
            {
                var name = kernel.GetGoal(id).Objective;
                var file = name + ".txt";
                var path = CommitGoalWork(root, id, file, "first change");
                File.AppendAllText(Path.Combine(path, file), "\nsecond change");
                RunGit(path, "add", file);
                RunGit(path, "commit", "-m", "Second goal change");
                var tip = RunGitOutput(path, "rev-parse", "HEAD").Trim();
                RunGit(root, "merge", "--squash", GoalWorktrees.BranchName(id));
                RunGit(root, "commit", "-m", $"Squash {name}");
                var landing = RunGitOutput(root, "rev-parse", "HEAD").Trim();
                Assert.Contains(RunGitOutput(root, "cherry", "main", GoalWorktrees.BranchName(id)).Split('\n'),
                    line => line.StartsWith("+ ", StringComparison.Ordinal));
                GoalOperationJournal.RecordLandingIntent(root, kernel.GetGoal(id), GoalWorktrees.BranchName(id), "main", landing, "test");
                SaveFinalizedReceipt(root, trainStore, id, tip, landing);
                worktrees[id] = path;
                tips[id] = tip;
                landingCommits[id] = landing;
            }

            var landed = ids[0];
            var unlanded = ids[1];
            var dirty = ids[2];
            File.WriteAllText(Path.Combine(worktrees[unlanded], "extra.txt"), "unlanded work");
            RunGit(worktrees[unlanded], "add", "extra.txt");
            RunGit(worktrees[unlanded], "commit", "-m", "Advance after receipt");
            var extraCommit = RunGitOutput(worktrees[unlanded], "rev-parse", "HEAD").Trim();
            Assert.NotEqual(tips[unlanded], extraCommit);
            File.WriteAllText(Path.Combine(worktrees[dirty], "D.txt"), "modified tracked content");
            var longDirectory = Path.Combine(worktrees[landed], ".mcg-sandbox");
            while (longDirectory.Length < 280) longDirectory = Path.Combine(longDirectory, new string('x', 40));
            var longFile = Path.Combine(longDirectory, "long.txt");
            static string Extended(string path) => OperatingSystem.IsWindows() ? @"\\?\" + path : path;
            Directory.CreateDirectory(Extended(longDirectory));
            File.WriteAllText(Extended(longFile), "ignored long path");
            Assert.True(longFile.Length > 260);
            Assert.True(File.Exists(Extended(longFile)));
            Assert.Empty(RunGitOutput(worktrees[landed], "status", "--porcelain").Trim());
            var initialRegistration = RunGitOutput(root, "worktree", "list", "--porcelain").Replace('\\', '/');
            foreach (var path in worktrees.Values)
                Assert.Contains(path.Replace('\\', '/'), initialRegistration, StringComparison.OrdinalIgnoreCase);
            var hooks = CreateIsolatedCleanupContext(workspace, new GoalWorktreeCleanupHooks
            {
                ResetSandboxAcl = (_, _) => { },
                FindLockHoldersForCleanup = _ => [],
                ProcessCommandLineSnapshot = _ => new ProcessCommandLineSnapshot(new Dictionary<int, string>())
            }).Hooks;
            var firstLines = new List<string>();

            var first = StartupTerminalSweep.Run(Candidates(kernel, workspace, hooks), workspace, null, hooks, writeLine: firstLines.Add);

            Assert.NotEqual(0, GitCli.Run(root, "show-ref", "--verify", $"refs/heads/{GoalWorktrees.BranchName(landed)}").ExitCode);
            Assert.False(Directory.Exists(worktrees[landed]));
            Assert.False(File.Exists(Extended(longFile)));
            var registrations = RunGitOutput(root, "worktree", "list", "--porcelain").Replace('\\', '/');
            Assert.DoesNotContain(worktrees[landed].Replace('\\', '/'), registrations, StringComparison.OrdinalIgnoreCase);
            var repair = Assert.Single(first.Goals.Single(goal => goal.GoalId == landed).Repairs,
                item => item.Kind == "completed-branch-landed-retired");
            Assert.Contains($"receipt=merge-train-receipt:receipt-{landed.Value}", repair.Evidence, StringComparison.Ordinal);
            Assert.Contains($"candidateRevision={tips[landed]}", repair.Evidence, StringComparison.Ordinal);
            Assert.Contains($"landingCommit={landingCommits[landed]}", repair.Evidence, StringComparison.Ordinal);
            Assert.Contains(first.Goals.Single(goal => goal.GoalId == unlanded).Blockers, blocker => blocker.Kind == "completed-branch-unmerged");
            Assert.NotEmpty(first.Goals.Single(goal => goal.GoalId == dirty).Blockers);
            AssertKept(root, unlanded, worktrees[unlanded], extraCommit, registrations);
            AssertKept(root, dirty, worktrees[dirty], tips[dirty], registrations);
            Assert.Equal("unlanded work", File.ReadAllText(Path.Combine(worktrees[unlanded], "extra.txt")));
            Assert.Equal("modified tracked content", File.ReadAllText(Path.Combine(worktrees[dirty], "D.txt")));
            Assert.Contains(" landed_branches_retired=1 ", Assert.Single(firstLines), StringComparison.Ordinal);
            Assert.StartsWith(StartupSweepStepsLine.Prefix, firstLines[0], StringComparison.Ordinal);

            var secondLines = new List<string>();
            var second = StartupTerminalSweep.Run(Candidates(kernel, workspace, hooks), workspace, null, hooks, writeLine: secondLines.Add);

            Assert.DoesNotContain(landed, second.ExplicitlySweptGoalIds);
            Assert.Contains(unlanded, second.ExplicitlySweptGoalIds);
            Assert.Contains(dirty, second.ExplicitlySweptGoalIds);
            Assert.StartsWith(StartupSweepStepsLine.Prefix, Assert.Single(secondLines), StringComparison.Ordinal);
            Assert.Contains(" landed_branches_retired=0 ", secondLines[0], StringComparison.Ordinal);
            var secondRegistrations = RunGitOutput(root, "worktree", "list", "--porcelain").Replace('\\', '/');
            AssertKept(root, unlanded, worktrees[unlanded], extraCommit, secondRegistrations);
            AssertKept(root, dirty, worktrees[dirty], tips[dirty], secondRegistrations);
            Assert.Equal("modified tracked content", File.ReadAllText(Path.Combine(worktrees[dirty], "D.txt")));
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }

    [Fact]
    public async Task ConductLoop_StartupPrintsOneStepLinePerPass()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(new AgentOrchestratorKernel());
            for (var pass = 0; pass < 2; pass++)
            {
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? current = null;
                var output = CaptureConsole(() => CaptureConsoleError(() => CliPersistentStateRunner.ExecuteCommand(
                    ["conduct", "--loop", "--max-iterations", "1"], repository, workspace, ref agents,
                    new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
                Assert.Single(output.Split('\n').Where(line => line.StartsWith("STARTUP_SWEEP_STEPS total_ms=", StringComparison.Ordinal)));
                Assert.Single(output.Split('\n').Where(line => line.StartsWith("STARTUP_FIRST_TICK elapsed_ms=", StringComparison.Ordinal)));
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static AgentOrchestratorKernel Candidates(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, GoalWorktreeCleanupHooks hooks)
    {
        var index = GoalGitFactIndex.Build(workspace.ExecutionDirectory, workspace.IntegrationBranch);
        var snapshot = kernel.ExportSnapshot();
        // Mirror startup's presence predicate; retired goals must leave this population on pass two.
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Where(goal => goal.Status == GoalStatus.Completed &&
                (index.HasGoalBranch(GoalWorktrees.BranchName(new GoalId(goal.Id))) ||
                 GoalWorktrees.TryResolve(workspace.ExecutionDirectory, new GoalId(goal.Id)) is not null ||
                 GoalWorktrees.TryGetCleanupBackoff(workspace.ExecutionDirectory, new GoalId(goal.Id), hooks) is not null)).ToArray()
        });
    }

    private void AssertKept(string root, GoalId id, string path, string tip, string registrations)
    {
        Assert.True(Directory.Exists(path));
        Assert.Contains(path.Replace('\\', '/'), registrations, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tip, RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(id)).Trim());
    }

    private void SaveFinalizedReceipt(string root, MergeTrainAcceptanceStore store, GoalId id, string tip, string landing)
    {
        MergeTrainMemberBinding Member(GoalId memberId, string file) => new MergeTrainMemberBinding(
            memberId, tip, tip, [file], [file], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString()).WithRebasedHead(tip);
        var tree = RunGitOutput(root, "rev-parse", "main^{tree}").Trim();
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
        store.PrepareLanding(receipt, landing, null);
        Assert.NotEmpty(store.FinalizeLanding(identity.Value, receipt.ReceiptId));
        store.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
        Assert.Contains(identity.Value, store.ReadFinalizedLandingTrainIds());
        Assert.Contains(store.ReadPassedReceiptsForGoal(id), saved => saved.ReceiptId == receipt.ReceiptId);
    }
}
