using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsGoalBoard : CliCommandTestBase
{
    private readonly ITestOutputHelper _output;

    public CliCommandTestsGoalBoard(ITestOutputHelper output)
    {
        _output = output;
    }

    [Xunit.Fact]
    public void GoalsBoardPrintsOperationalRowsAndSummary()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Operational board title");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goals", "--board", "--all"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("shown=1 omitted=0", output, StringComparison.Ordinal);
            Xunit.Assert.Contains($"Operational board title [{goal.Id.Value[..8]}]", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(0, repository.SaveAsyncCount);
            Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")));
            Xunit.Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalsBoardExternalArgvPreservesSelectorAndFlags()
    {
        string[] args = ["goals", "--board", "--all"];

        Xunit.Assert.Equal(args, CliArgumentParser.NormalizeArgs(args));
    }

    [Xunit.Fact]
    public void GoalBoardArgumentsEnforceLimitsAndMutualExclusion()
    {
        Xunit.Assert.Equal(50, GoalBoardOptions.Parse(["goals", "--board"]).Limit);
        Xunit.Assert.Equal(1, GoalBoardOptions.Parse(["goals", "--board", "--limit", "1"]).Limit);
        Xunit.Assert.Equal(500, GoalBoardOptions.Parse(["goals", "--board", "--limit", "500"]).Limit);
        Xunit.Assert.Null(GoalBoardOptions.Parse(["goals", "--board", "--all"]).Limit);
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit", "0"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit", "501"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit", "x"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--all", "--limit", "5"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--unknown"]));
    }

    [Xunit.Fact]
    public void ProgramStartupGoalBoardRouteUsesReadOnlyRepositoryOnly()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Startup route goal");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var factoryCalls = 0;

            var output = CaptureConsole(() => Xunit.Assert.Equal(0, ProgramStartupLifecycle.RunGoalBoard(
                ["goals", "--board", "--all"],
                workspace,
                _ =>
                {
                    factoryCalls++;
                    return repository;
                },
                () => ProcessCommandLineSnapshot.Empty)));

            Xunit.Assert.Equal(1, factoryCalls);
            Xunit.Assert.Contains("shown=1 omitted=0", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(0, repository.SaveAsyncCount);
            Xunit.Assert.Equal(0, repository.SaveGoalSnapshotsCount);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("zero")]
    [Xunit.InlineData("too-large")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("conflict")]
    [Xunit.InlineData("unknown")]
    public void ProgramStartupGoalBoardRouteRejectsInvalidArgumentsWithUsage(string scenario)
    {
        var root = CreateTempDirectory();
        try
        {
            string[] args = scenario switch
            {
                "zero" => ["goals", "--board", "--limit", "0"],
                "too-large" => ["goals", "--board", "--limit", "501"],
                "missing" => ["goals", "--board", "--limit"],
                "conflict" => ["goals", "--board", "--all", "--limit", "2"],
                _ => ["goals", "--board", "--unknown"]
            };
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
            var error = CaptureConsoleError(() => Xunit.Assert.Equal(1, ProgramStartupLifecycle.RunGoalBoard(
                args,
                workspace,
                _ => repository,
                () => ProcessCommandLineSnapshot.Empty)));

            Xunit.Assert.Contains(GoalBoardOptions.Usage, error, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(0, repository.SaveAsyncCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ProgramStartupGoalBoardRouteHonorsLimitAndAll()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < 3; index++)
                _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, $"Limited goal {index}");
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var limited = CaptureConsole(() => Xunit.Assert.Equal(0, ProgramStartupLifecycle.RunGoalBoard(
                ["goals", "--board", "--limit", "1"],
                workspace,
                _ => repository,
                () => ProcessCommandLineSnapshot.Empty)));
            var all = CaptureConsole(() => Xunit.Assert.Equal(0, ProgramStartupLifecycle.RunGoalBoard(
                ["goals", "--board", "--all"],
                workspace,
                _ => repository,
                () => ProcessCommandLineSnapshot.Empty)));

            Xunit.Assert.Contains("shown=1 omitted=2", limited, StringComparison.Ordinal);
            Xunit.Assert.Contains("rerun goals --board --all or --limit 3", limited, StringComparison.Ordinal);
            Xunit.Assert.Contains("shown=3 omitted=0", all, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void CoordinatorFiltersAllLifecycleStatusesAndOrdersInterventionFirst()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = new AgentOrchestratorKernel();
            var statuses = Enum.GetValues<GoalStatus>();
            foreach (var status in statuses)
                _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(source, AgentCatalog.Default().Agents, $"{status} store title");
            var snapshot = source.ExportSnapshot();
            var indexedStatuses = statuses.Select((status, index) => (status, index)).ToArray();
            var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select((goal, index) => goal with { Status = indexedStatuses[index].status }).ToList()
            });

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                OrchestratorWorkspace.ForDirectory(root),
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            foreach (var included in new[]
                     {
                         GoalStatus.Draft, GoalStatus.Active, GoalStatus.WaitingForHuman, GoalStatus.Parked,
                         GoalStatus.Verifying, GoalStatus.Verified, GoalStatus.AcceptanceFailed, GoalStatus.Failed
                     })
            {
                var row = Xunit.Assert.Single(output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith($"{included} store title [", StringComparison.Ordinal)));
                Xunit.Assert.Contains($"status={included}", row, StringComparison.Ordinal);
                Xunit.Assert.Contains("stage=", row, StringComparison.Ordinal);
            }

            Xunit.Assert.DoesNotContain("Completed store title", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("Cancelled store title", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("Superseded store title", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("shown=8 omitted=0", output, StringComparison.Ordinal);
            Xunit.Assert.True(
                output.IndexOf("Failed store title", StringComparison.Ordinal) < output.IndexOf("Active store title", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void CoordinatorRendersDispatchAcceptanceAndOptionalFailuresWithoutDuplicateRows()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db"), "not sqlite");
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName), "not sqlite");
            var kernel = new AgentOrchestratorKernel();
            var deadGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Dead dispatch goal");
            var liveGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Live worker goal");
            var gateGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Live acceptance goal");
            RecordRunningProcess(kernel, deadGoal, deadGoal.Tasks.Single(), root, processId: 999999);
            RecordRunningProcess(kernel, liveGoal, liveGoal.Tasks.Single(), root, processId: Environment.ProcessId);
            WriteIdentityBoundHeartbeat(
                kernel.GetGoal(liveGoal.Id)!.Tasks.Single().LastProcess!,
                Environment.ProcessId);
            DispatchExitArtifacts.Write(
                Path.Combine(root, $"{deadGoal.Tasks.Single().Id.Value}-exit.txt"),
                DispatchExitArtifacts.Native(0, "worker exited", DateTimeOffset.UtcNow));
            WriteAcceptanceAttempt(workspace, gateGoal.Id.Value, live: true);
            var leaseStore = new ReconcileSweepRemediationStore(workspace.SqliteStatePath);
            Xunit.Assert.True(leaseStore.TryClaimAcceptanceLease(
                gateGoal.Id.Value,
                "goal-replace:test:active",
                ConductorDriver.EvidenceMutationLeaseDuration));

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => new ProcessCommandLineSnapshot(new Dictionary<int, string>
                {
                    [Environment.ProcessId] = "codex exec prompt.md"
                })));

            var deadRow = SingleLineContaining(output, "Dead dispatch goal");
            var liveRow = SingleLineContaining(output, "Live worker goal");
            var gateRow = SingleLineContaining(output, "Live acceptance goal");
            Xunit.Assert.Contains("next=refresh-dispatch", deadRow, StringComparison.Ordinal);
            Xunit.Assert.Contains("work=developer:running", liveRow, StringComparison.Ordinal);
            Xunit.Assert.Contains("held=worker live", liveRow, StringComparison.Ordinal);
            Xunit.Assert.Contains("work=gate:live", gateRow, StringComparison.Ordinal);
            Xunit.Assert.Contains("held=acceptance live", gateRow, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("lease-status=", gateRow, StringComparison.Ordinal);
            Xunit.Assert.All(new[] { deadRow, liveRow, gateRow }, row =>
                Xunit.Assert.Contains("attention=unknown intents=unknown", row, StringComparison.Ordinal));
            Xunit.Assert.Equal(1, CountLinesContaining(output, $"[{deadGoal.Id.Value[..8]}]"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void AcceptanceAttemptReaderDistinguishesLiveCorruptAndMissingFacts()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var attemptsRoot = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts");
            const string liveId = "10000000000000000000000000000000";
            const string corruptId = "20000000000000000000000000000000";
            const string missingId = "30000000000000000000000000000000";
            WriteAcceptanceAttempt(workspace, liveId, live: true);
            var corruptDirectory = Path.Combine(attemptsRoot, corruptId);
            Directory.CreateDirectory(corruptDirectory);
            File.WriteAllText(Path.Combine(corruptDirectory, "bad.attempt.json"), "{");

            var facts = GoalBoardAcceptanceAttemptReader.Read(attemptsRoot, [liveId, corruptId, missingId]);

            Xunit.Assert.True(facts[liveId].Available);
            Xunit.Assert.True(facts[liveId].IsLive);
            Xunit.Assert.NotNull(facts[liveId].LastHeartbeatAt);
            Xunit.Assert.False(facts[corruptId].Available);
            Xunit.Assert.False(facts[corruptId].IsLive);
            Xunit.Assert.True(facts[missingId].Available);
            Xunit.Assert.False(facts[missingId].IsLive);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void CoordinatorMapsGitInspectionFailuresToUnknown()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Git failure goal");
            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty,
                worktreeResolver: (_, ids) => ids.ToDictionary(id => id, _ => root),
                worktreeStatusInspector: _ => new GitCli.WorktreeStatusInspection(false, [], "status denied"),
                gitRunner: (_, _) => new GitCli.GitResult(1, string.Empty, "for-each-ref denied")));

            var row = SingleLineContaining(output, $"[{goal.Id.Value[..8]}]");
            Xunit.Assert.Contains("worktree=unknown ahead=? behind=?", row, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("worktree=clean", row, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void BareGoalsAndBoardAllReportSameSeededStoreDiagnostics()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Diagnostic first");
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Diagnostic second");
            ProgramStartupLifecycle.EnsureStateDbInitialized(["goals"], workspace);
            var writable = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            writable.SaveAsync(kernel).GetAwaiter().GetResult();

            var bareTimer = Stopwatch.StartNew();
            var bareOutput = CaptureConsole(() => GoalListTextView.Print(writable.ListGoalMetadataAsync().GetAwaiter().GetResult()));
            bareTimer.Stop();
            var boardTimer = Stopwatch.StartNew();
            var boardOutput = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));
            boardTimer.Stop();
            var bareRows = CountLinesContaining(bareOutput, "Diagnostic ");
            var boardRows = CountLinesContaining(boardOutput, "Diagnostic ");

            _output.WriteLine($"command=goals exit=0 elapsed_ms={bareTimer.ElapsedMilliseconds} rows={bareRows}");
            _output.WriteLine($"command=goals --board --all exit=0 elapsed_ms={boardTimer.ElapsedMilliseconds} rows={boardRows}");
            Xunit.Assert.Equal(2, bareRows);
            Xunit.Assert.Equal(2, boardRows);
            Xunit.Assert.Contains("shown=2 omitted=0", boardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InspectWorktreesInvokesForEachRefOnceForMultipleGoals()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var first = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "First git goal");
            var second = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Second git goal");
            var gitCalls = 0;
            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty,
                worktreeResolver: (_, ids) => ids.ToDictionary(id => id, _ => root),
                worktreeStatusInspector: _ => new GitCli.WorktreeStatusInspection(true, [], null),
                gitRunner: (_, args) =>
                {
                    gitCalls++;
                    Xunit.Assert.Equal("for-each-ref", args[0]);
                    return new GitCli.GitResult(
                        0,
                        $"{GoalWorktrees.BranchName(first.Id)} 3 7\n{GoalWorktrees.BranchName(second.Id)} 3 7\n",
                        string.Empty);
                }));

            Xunit.Assert.Equal(1, gitCalls);
            Xunit.Assert.Contains("ahead=3 behind=7", SingleLineContaining(output, "First git goal"), StringComparison.Ordinal);
            Xunit.Assert.Contains("ahead=3 behind=7", SingleLineContaining(output, "Second git goal"), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InspectWorktreesProbesDirtyStatusOncePerResolvedWorktree()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var resolved = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Resolved worktree");
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Unresolved worktree");
            var probes = 0;
            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty,
                worktreeResolver: (_, _) => new Dictionary<GoalId, string> { [resolved.Id] = root },
                worktreeStatusInspector: _ =>
                {
                    probes++;
                    return new GitCli.WorktreeStatusInspection(true, [], null);
                },
                gitRunner: (_, _) => new GitCli.GitResult(
                    0,
                    $"{GoalWorktrees.BranchName(resolved.Id)} 1 0\n",
                    string.Empty)));

            Xunit.Assert.Equal(1, probes);
            Xunit.Assert.Contains("worktree=clean ahead=1 behind=0", SingleLineContaining(output, "Resolved worktree"), StringComparison.Ordinal);
            Xunit.Assert.Contains("worktree=- ahead=? behind=?", SingleLineContaining(output, "Unresolved worktree"), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InspectWorktreesFailedForEachRefLeavesUnknownDivergence()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Unknown divergence");
            var gitCalls = 0;
            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty,
                worktreeResolver: (_, ids) => ids.ToDictionary(id => id, _ => root),
                worktreeStatusInspector: _ => new GitCli.WorktreeStatusInspection(true, [], null),
                gitRunner: (_, _) =>
                {
                    gitCalls++;
                    return new GitCli.GitResult(128, string.Empty, "unknown field ahead-behind");
                }));

            Xunit.Assert.Equal(1, gitCalls);
            Xunit.Assert.Contains("worktree=clean ahead=? behind=?", SingleLineContaining(output, "Unknown divergence"), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void VerifiedGoalWithCleanupJournalIsOmittedFromBoard()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var cleaned = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cleaned verified goal");
            var live = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Live verified goal");
            GoalOperationJournal.Completed(root, cleaned, "conductor:land");
            GoalOperationJournal.Completed(root, cleaned, "conductor:record");
            GoalOperationJournal.Completed(root, cleaned, "conductor:cleanup");
            kernel = WithGoalStatus(kernel, GoalStatus.Verified);

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            Xunit.Assert.DoesNotContain("Cleaned verified goal", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("status=Verified", SingleLineContaining(output, "Live verified goal"), StringComparison.Ordinal);
            Xunit.Assert.Contains("shown=1 omitted=0", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MergedJournalRendersLandedCleanupStage()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Landed verified goal");
            GoalOperationJournal.Completed(root, goal, "conductor:land");
            GoalOperationJournal.Completed(root, goal, "conductor:record");
            kernel = WithGoalStatus(kernel, GoalStatus.Verified);

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            var row = SingleLineContaining(output, "Landed verified goal");
            Xunit.Assert.Contains("status=Landed", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("stage=cleanup", row, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task OpenDecisionIsCountedAndAdvertisedOnBoard()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Decision attention goal");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            _ = await store.RaiseAsync(
                CollaborationItemType.Decision,
                goal.Id.Value,
                "Need a call",
                "Decide whether to keep the current slice.");

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            var row = SingleLineContaining(output, "Decision attention goal");
            Xunit.Assert.Contains("attention=1", row, StringComparison.Ordinal);
            Xunit.Assert.Contains($"next=attention show {goal.Id.Value[..8]}", row, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalBoardReadsLeaseEvidenceWithoutMutatingRecoveryState()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Persisted goal-evidence lease");
            var store = new ReconcileSweepRemediationStore(workspace.SqliteStatePath);
            const string operation = "conductor:developer-branch-integration";
            const string instance = "board-instance";
            using var currentProcess = Process.GetCurrentProcess();
            var ownerProcessStartedAt = new DateTimeOffset(
                currentProcess.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            var owner = $"goal-evidence:v1:{operation}:{currentProcess.Id}:{instance}";
            Xunit.Assert.True(store.TryClaimAcceptanceLease(
                goal.Id.Value,
                owner,
                ConductorDriver.EvidenceMutationLeaseDuration));
            GoalOperationJournal.Begin(
                root,
                goal,
                operation,
                operationInstanceId: instance,
                ownerProcessStartedAtUtc: ownerProcessStartedAt);
            var journalPath = GoalOperationJournal.PathFor(root, goal.Id);
            var entryCount = GoalOperationJournal.Read(root, goal.Id).Entries.Count;

            var liveOutput = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                utcNow: () => DateTimeOffset.UtcNow.AddMinutes(12),
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            var liveRow = SingleLineContaining(liveOutput, "Persisted goal-evidence lease");
            Xunit.Assert.Contains($"lease-goal={goal.Id.Value[..8]}", liveRow, StringComparison.Ordinal);
            Xunit.Assert.Contains("owner-operation=conductor:developer-branch-integrati", liveRow, StringComparison.Ordinal);
            Xunit.Assert.Contains("lease-status=live", liveRow, StringComparison.Ordinal);
            Xunit.Assert.Equal(owner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
            Xunit.Assert.Equal(entryCount, GoalOperationJournal.Read(root, goal.Id).Entries.Count);

            using (new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var unavailableOutput = CaptureConsole(() => GoalBoardCommand.Run(
                    ["goals", "--board", "--all"],
                    new InMemoryTransactionalStateRepository(kernel),
                    workspace,
                    processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));
                var unavailableRow = SingleLineContaining(unavailableOutput, "Persisted goal-evidence lease");
                Xunit.Assert.Contains("lease-status=state-unavailable", unavailableRow, StringComparison.Ordinal);
            }

            Xunit.Assert.Equal(owner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
            Xunit.Assert.Equal(entryCount, GoalOperationJournal.Read(root, goal.Id).Entries.Count);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalBoardRendersLegacyGoalEvidenceOwnerAsStateUnavailable()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Pre-upgrade goal-evidence lease");
            var store = new ReconcileSweepRemediationStore(workspace.SqliteStatePath);
            const string owner =
                "goal-evidence:conductor:developer-branch-integration:no-pid:a3b675d01a2349aaa8aee97aff86f334";
            Xunit.Assert.True(store.TryClaimAcceptanceLease(
                goal.Id.Value,
                owner,
                ConductorDriver.EvidenceMutationLeaseDuration));

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            var row = SingleLineContaining(output, "Pre-upgrade goal-evidence lease");
            Xunit.Assert.Contains($"lease-goal={goal.Id.Value[..8]}", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("owner-operation=unknown", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("lease-status=state-unavailable", row, StringComparison.Ordinal);
            Xunit.Assert.Equal(owner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalBoardMalformedLeaseTimestampIsStateUnavailable()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Malformed lease timestamp");
            var store = new ReconcileSweepRemediationStore(workspace.SqliteStatePath);
            const string owner = "goal-evidence:v1:conductor:developer-branch-integration:42:malformed-time";
            Xunit.Assert.True(store.TryClaimAcceptanceLease(
                goal.Id.Value,
                owner,
                ConductorDriver.EvidenceMutationLeaseDuration));
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                       $"Data Source={workspace.SqliteStatePath};Pooling=False"))
            {
                connection.Open();
                using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "UPDATE reconcile_acceptance_leases SET acquired_at = 'malformed-timestamp' WHERE goal_id = $goal";
                corrupt.Parameters.AddWithValue("$goal", goal.Id.Value);
                Xunit.Assert.Equal(1, corrupt.ExecuteNonQuery());
            }

            var output = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () => ProcessCommandLineSnapshot.Empty));

            var row = SingleLineContaining(output, "Malformed lease timestamp");
            Xunit.Assert.Contains("lease-status=state-unavailable", row, StringComparison.Ordinal);
            Xunit.Assert.Equal(owner, store.TryGetAcceptanceLeaseOwner(goal.Id.Value));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static AgentOrchestratorKernel WithGoalStatus(AgentOrchestratorKernel kernel, GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with { Status = status }).ToList()
        });
    }

    private static void WriteAcceptanceAttempt(OrchestratorWorkspace workspace, string goalId, bool live)
    {
        var root = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", goalId);
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var attempt = new ConductorParallelAcceptanceAttempt(
            "attempt-1", goalId, goalId[..8], 0, "candidate", "main", now, now,
            Environment.ProcessId,
            live ? ConductorParallelAcceptanceAttemptOutcome.Running : ConductorParallelAcceptanceAttemptOutcome.Passed,
            "out", "err", "exit", "heartbeat", "result", "metadata");
        File.WriteAllText(
            Path.Combine(root, "attempt-1.attempt.json"),
            JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
