using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionReadinessWriterPathTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("attention", "answer", "abc10000", "id", "answer")]
    [Xunit.InlineData("attention", "dismiss", "abc10000")]
    [Xunit.InlineData("attention", "dismiss", "--item", "item-id")]
    [Xunit.InlineData("readiness-repair")]
    [Xunit.InlineData("readiness-repair", "abc10000")]
    public async Task MutatingFormsStayOnWriterPath(params string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
        var error = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(() =>
            CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository));
        Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(1, repository.FullLoadAttempts);
    }

    [Xunit.Theory]
    [Xunit.InlineData("attention", "answer", "abc10000", "missing", "answer")]
    [Xunit.InlineData("attention", "dismiss", "abc10000")]
    [Xunit.InlineData("attention", "dismiss", "--item", "missing")]
    [Xunit.InlineData("readiness-repair")]
    [Xunit.InlineData("readiness-repair", "abc10000")]
    public async Task MutatingFormsExecuteWriterPathAndDrainOutbox(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Writer path probe");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

            await using (var connection = OpenStateConnection(workspace.SqliteStatePath))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('writer-path-probe', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                await insert.ExecuteNonQueryAsync();
            }

            var result = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, args);
            Xunit.Assert.Contains("outbox message 'writer-path-probe' was quarantined", result.Error,
                StringComparison.Ordinal);

            await using var check = OpenStateConnection(workspace.SqliteStatePath);
            await check.OpenAsync();
            await using var query = check.CreateCommand();
            query.CommandText = "SELECT quarantined_at FROM state_outbox WHERE id = 'writer-path-probe'";
            Xunit.Assert.False(string.IsNullOrWhiteSpace((string?)await query.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("readiness")]
    [Xunit.InlineData("readiness", "abc10000")]
    [Xunit.InlineData("READINESS", "ABC10000")]
    public async Task Readiness_LeavesPendingOutboxAndStateUnchanged(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Readiness read probe",
                [new TaskSpec(TaskId.New(), "Inspect readiness", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Xunit.Assert.True(DispatchReadinessRules.HasAssignedDispatchCandidates(goal));
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            await using (var connection = OpenStateConnection(workspace.SqliteStatePath))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('readiness-read-probe', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                Xunit.Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            var before = JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot());

            var result = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, args);

            Xunit.Assert.True(result.ExitCode == 0, result.Error);
            Xunit.Assert.Contains("Goal readiness", result.Output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("quarantined", result.Error, StringComparison.Ordinal);
            Xunit.Assert.Equal(before, JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot()));
            await using var check = OpenStateConnection(workspace.SqliteStatePath);
            await check.OpenAsync();
            await using var query = check.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM state_outbox WHERE id = 'readiness-read-probe' AND payload_json = '{}' AND quarantined_at IS NULL AND processing_token IS NULL AND processing_started_at IS NULL";
            Xunit.Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);
            Xunit.Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("readiness")]
    [Xunit.InlineData("readiness", "abc10000")]
    public async Task Readiness_SkipsStartupHydration(params string[] args)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
        var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
        Xunit.Assert.False(startup.Hydrated);
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
    }

    [Xunit.Fact]
    public void Readiness_Help_DeclinesReadOnlyRoute()
    {
        string[] args = ["readiness", "--help"];
        Xunit.Assert.True(CliCommandHelp.IsCommandSpecificHelp(args));
        Xunit.Assert.False(CliReadinessQueryCommand.IsReadinessQueryCommand(args));
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    public async Task Readiness_Bare_SelectsCreationOrderAndLoadsOnlySelectedGoal(
        bool tiedCreationTimes, bool newestIsTerminal)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var older = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Older, updated last");
            var newer = kernel.CreateGoal(new GoalId("abc20000aaaaaaaaaaaaaaaaaaaaaaaa"), "Newer, updated first");
            var created = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal =>
                {
                    var isOlder = goal.Id == older.Id.Value;
                    var creationTime = isOlder || tiedCreationTimes ? created : created.AddDays(1);
                    var updateTime = created.AddDays(isOlder ? 3 : 2);
                    var first = goal.Timeline[0];
                    return goal with
                    {
                        Status = !isOlder && newestIsTerminal ? GoalStatus.Completed : goal.Status,
                        CreatedAt = creationTime,
                        Timeline = [first with { OccurredAt = creationTime },
                            first with { OccurredAt = updateTime, Message = "Later update" }]
                    };
                }).ToArray()
            });
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            // The repository enumerates by last update: the older goal comes first.
            var metadata = await repository.ListGoalMetadataAsync();
            Xunit.Assert.Equal(older.Id.Value, metadata[0].Id);
            if (tiedCreationTimes)
                Xunit.Assert.Equal(metadata[0].CreatedAt, metadata[1].CreatedAt);
            else
                Xunit.Assert.True(metadata[0].CreatedAt < metadata[1].CreatedAt);
            Xunit.Assert.True(StringComparer.Ordinal.Compare(metadata[0].UpdatedAt, metadata[1].UpdatedAt) > 0);
            var expected = tiedCreationTimes ? older.Id : newer.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = kernel.GetGoal(older.Id);
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    ["readiness"], repository, OrchestratorWorkspace.ForDirectory(root),
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal,
                    out var changed));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Equal(expected, currentGoal!.Id);
            Xunit.Assert.Contains("Goal readiness", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(2, repository.ListGoalMetadataCount); // Arrangement + command.
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal(new[] { expected.Value }, repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Readiness_Bare_NoGoals_PreservesErrorWithoutHydrationOrWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliPersistentStateRunner.ExecuteCommand(["readiness"], repository,
                    OrchestratorWorkspace.ForDirectory(root), ref agents,
                    new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));

            Xunit.Assert.Equal("Create a goal first with: goal <objective>", error.Message);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("readiness", "abc10000", "extra")]
    [Xunit.InlineData("readiness", "abc10000", "--repair")]
    [Xunit.InlineData("readiness", "--unknown")]
    [Xunit.InlineData("readiness", " ")]
    public void Readiness_InvalidExplicitForm_FailsWithoutWriterEffects(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            Xunit.Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
            var error = Xunit.Assert.Throws<ArgumentException>(() =>
                CliPersistentStateRunner.ExecuteCommand(args, repository, workspace, ref agents,
                    new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
            Xunit.Assert.Contains("Usage: readiness", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
            Xunit.Assert.Empty(repository.ObservedWriteOperationTags);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task Readiness_SupersededBranch_ReportsRepairWithoutApplyingIt()
    {
        var root = CreateTempDirectory();
        GoalId? cleanupGoalId = null;
        try
        {
            RunGit(root, "init", "-b", "main");
            RunGit(root, "config", "user.email", "tests@example.com");
            RunGit(root, "config", "user.name", "CLI Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Seed");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Superseded readiness branch");
            cleanupGoalId = goal.Id;
            var worktree = GoalWorktrees.Ensure(root, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "equivalent.txt"), "already upstream");
            RunGit(worktree, "add", "equivalent.txt");
            RunGit(worktree, "commit", "-m", "Goal work");
            File.WriteAllText(Path.Combine(root, "equivalent.txt"), "already upstream");
            RunGit(root, "add", "equivalent.txt");
            RunGit(root, "commit", "-m", "Equivalent work landed by another goal");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(item => item.Id == goal.Id.Value
                    ? item with { Status = GoalStatus.Completed } : item).ToArray()
            });
            Xunit.Assert.False(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
            Xunit.Assert.Contains(TerminalGoalSweep.Diagnose(kernel, root, goal.Id).Blockers,
                blocker => blocker.Kind == "completed-branch-superseded");
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            var before = JsonSerializer.Serialize(kernel.ExportSnapshot());
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var output = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    ["readiness", goal.Id.Value[..8]], repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal,
                    out var changed));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains("SWEEP_BLOCKER", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("kind=completed-branch-superseded", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("retirement required", output, StringComparison.Ordinal);
            Xunit.Assert.Contains("Goal readiness", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("SWEEP_REPAIR", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(before, JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot()));
            Xunit.Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
            Xunit.Assert.Equal(JsonSerializer.Serialize(kernel.GetGoal(goal.Id)), JsonSerializer.Serialize(currentGoal));
            Xunit.Assert.Equal(worktree, GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.False(string.IsNullOrWhiteSpace(RunGit(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id))));
            Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            if (cleanupGoalId is not null)
                GoalWorktrees.Remove(root, cleanupGoalId);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (result.DrainTimedOut)
            throw new InvalidOperationException($"git output incomplete: {result}");
        Xunit.Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output;
    }

    private static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

    [Xunit.Fact]
    public async Task NextRetainsItsExistingQueryOnlyCompositionWhileUsingTheReadOnlyRoute()
    {
        var args = new[] { "next", "abc10000" };
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Xunit.Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
        var startup = await CliReadOnlyStartupHydration.PrepareStartupAsync(args, repository);
        Xunit.Assert.False(startup.Hydrated);
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
    }

    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "list")]
    [Xunit.InlineData("attention", "show")]
    public void AttentionReadFormsRetainExecutionCompositionForDeclinedCalls(params string[] args) =>
        Xunit.Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));

    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "show")]
    public void ParkedGoalDeclinesReadRouteAndRehydratesBeforeFallback(params string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Parked goal");
            kernel.ParkGoal(goal.Id, "Operator wait");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var output = CaptureConsole(() => Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out _)));
            Xunit.Assert.Equal(string.Empty, output);

            var declined = 0;
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliReadOnlyStartupHydration.ExecuteStartupCommand(args, repository, workspace,
                    ref agents, providers, ref profiles, ref currentGoal, hydrated: false,
                    onReadOnlyDeclined: () => declined++));
            Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, declined);
            Xunit.Assert.Equal(1, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task NextFullWithStaleSweepAttentionDeclinesToWriterPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Stale sweep item");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                "Stale sweep blocker", "Would be resolved by next --full",
                $"terminal-sweep-blocker:{goal.Id.Value}:stale");
            var repository = new ProbeStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var args = new[] { "next", "--full", goal.Id.Value[..8] };
            var output = CaptureConsole(() => Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
                args, repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out _)));
            Xunit.Assert.Equal(string.Empty, output);
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                CliReadOnlyStartupHydration.ExecuteStartupCommand(args, repository, workspace,
                    ref agents, providers, ref profiles, ref currentGoal, hydrated: false));
            Xunit.Assert.Contains("Full-kernel hydration", error.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, repository.FullLoadAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
