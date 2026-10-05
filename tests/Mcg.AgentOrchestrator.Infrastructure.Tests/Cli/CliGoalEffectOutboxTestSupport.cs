using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each fixture owns its SQLite stores, journal and git repository. Console capture is scoped
// by the existing ConsoleSerialized collection; there is no elapsed-time assertion.
public abstract class CliGoalEffectOutboxTestSupport : CliGoalLifecycleOutboxTestSupport
{
    private protected const string ParkKind = "goal-park-attention-resolution";
    private protected const string AbandonKind = "goal-abandon-after-commit";
    private protected const string EffectPendingSentence =
        "The delivery is pending and will be retried by the next writer-path command.";

    private protected static string[] EffectParts(Seed seed, string command) => command switch
    {
        "park" => ["park-goal", seed.GoalId.Value[..8], "Operator transition", "--confirm-goal-park"],
        "stop-park" => ["stop", seed.GoalId.Value[..8], "Operator transition", "--as", "park", "--confirm-goal-park"],
        _ => Parts(seed, command)
    };

    private protected static CommandResult EffectTransition(Seed seed, string command) =>
        RunCommand(EffectParts(seed, command), seed.Repository, seed.Workspace);

    private protected static string AttentionPath(Seed seed) =>
        Path.Combine(seed.Workspace.OrchestratorDirectory, "collaboration-items.db");

    private protected static async Task<CollaborationItem> Raise(Seed seed, string key = "early")
    {
        await CollaborationItemStore.ForDirectory(seed.Workspace.OrchestratorDirectory).RaiseAsync(
            CollaborationItemType.Decision, seed.GoalId.Value, "Decision", "Choose", key);
        await SetRaisedAt(seed, key, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return await Item(seed, key);
    }

    private protected static async Task<CollaborationItem> Item(Seed seed, string key = "early") =>
        Xunit.Assert.Single((await CollaborationItemStore.ForDirectory(seed.Workspace.OrchestratorDirectory)
            .ListAsync(seed.GoalId.Value)).Where(item => item.CorrelationKey == key));

    private protected static async Task SetRaisedAt(Seed seed, string key, DateTimeOffset at)
    {
        await using var connection = new SqliteConnection($"Data Source={AttentionPath(seed)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE collaboration_items SET raised_at = $at WHERE correlation_key = $key";
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$key", key);
        Xunit.Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private protected static DateTimeOffset ParkedAt(OrchestratorStateOutboxMessage message)
    {
        using var document = JsonDocument.Parse(message.PayloadJson);
        return document.RootElement.GetProperty("parkedAt").GetDateTimeOffset();
    }

    private protected static void AssertItemUnchanged(CollaborationItem before, CollaborationItem after)
    {
        Xunit.Assert.Equal(before with { AnswerHistory = null }, after with { AnswerHistory = null });
        Xunit.Assert.Equal(before.AnswerHistory?.ToArray(), after.AnswerHistory?.ToArray());
    }

    private protected static void AssertEffectFailure(CommandResult result, string effect, GoalStatus status)
    {
        var error = Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(result.Error);
        Xunit.Assert.Equal($"Goal 'aaaaaaaa' is committed {status}, but {effect} delivery failed: " +
            error.InnerException!.Message.TrimEnd().TrimEnd('.') + ". " + EffectPendingSentence, error.Message);
        Xunit.Assert.Equal(string.Empty, result.Output);
    }

    private protected sealed class PathBlocker : IDisposable
    {
        private readonly string path;
        private readonly string backup;
        private readonly bool hadFile;

        internal PathBlocker(string path)
        {
            this.path = path;
            backup = path + ".saved";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            hadFile = File.Exists(path);
            if (hadFile) File.Move(path, backup);
            Directory.CreateDirectory(path);
        }

        public void Dispose()
        {
            Directory.Delete(path);
            if (hadFile) File.Move(backup, path);
        }
    }

    // Git object files can be read-only on Windows. Restore only this fixture's file
    // attributes before Seed.Dispose removes its private repository and worktree.
    private protected sealed class GitFixtureCleanup(Seed seed) : IDisposable
    {
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(seed.Root, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private protected static string AddWorktree(Seed seed)
    {
        void Git(params string[] args)
        {
            var result = GitCli.Run(seed.Root, 30_000, args);
            Xunit.Assert.True(result.ExitCode == 0, result.Error);
        }
        Git("init");
        Git("config", "user.email", "tests@example.com");
        Git("config", "user.name", "Effect outbox tests");
        File.WriteAllText(Path.Combine(seed.Root, ".gitignore"), ".orchestrator/\n.orchestrator-worktrees/\n");
        File.WriteAllText(Path.Combine(seed.Root, "seed.txt"), "seed");
        Git("add", ".gitignore", "seed.txt");
        Git("commit", "-m", "Seed");
        var path = GoalWorktrees.Ensure(seed.Root, seed.GoalId);
        Xunit.Assert.True(Directory.Exists(path));
        Xunit.Assert.Equal(path, GoalWorktrees.TryResolve(seed.Root, seed.GoalId));
        return path;
    }

    private protected static async Task AddLiveDispatch(Seed seed)
    {
        var kernel = await seed.Repository.LoadGoalsAsync([seed.GoalId]);
        kernel.ActivateGoal(seed.GoalId, AgentCatalog.Default().Agents);
        var task = kernel.GetGoal(seed.GoalId).Tasks.Single();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskDispatch(seed.GoalId, task.Id, new TaskDispatchRecord("codex-cli", "test worker", seed.Root, at));
        kernel.RecordTaskProcessStarted(seed.GoalId, task.Id, new TaskProcessRecord(900001, "test worker", seed.Root,
            Path.Combine(seed.Root, "worker.out"), Path.Combine(seed.Root, "worker.err"),
            Path.Combine(seed.Root, "worker.exit"), at, null, null));
        await seed.Repository.SaveAsync(kernel);
    }

    private protected static GoalOperationJournalSummary Journal(Seed seed) =>
        GoalOperationJournal.Read(seed.Root, seed.GoalId);
}
