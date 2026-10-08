using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each execution owns its workspace and probe; console capture is async-local.
public sealed class CliCommandCapabilitiesTestsQueryRouteNoWriterEffects : CliTaskQueryTestSupport
{
    private static readonly IReadOnlyDictionary<string, string[][]> ServedForms =
        new Dictionary<string, string[][]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tasks"] = [["tasks"]],
            ["task"] = [["task", "abc10000"]],
            ["status"] = [["status", "abc10000"]],
            ["goals"] = [["goals"]],
            ["failure-clusters"] = [["failure-clusters"]],
            ["lane-reuse-shadow"] = [["lane-reuse-shadow"]],
            ["remote-executors"] = [["remote-executors"]],
            ["round-value"] = [["round-value"]],
            ["architecture"] = [["architecture"]],
            ["config"] = [["config", "agents"], ["config", "profiles"], ["config", "policy"]],
            ["model-outcomes"] = [["model-outcomes"]],
            ["backlog-list"] = [["backlog-list"]],
            ["backlog-show"] = [["backlog-show", "abc10000"]],
            ["backlog-similar"] = [["backlog-similar", "query"]],
            ["goal-events"] = [["goal-events", "abc10000"]],
            ["timeline"] = [["timeline", "abc10000"]],
            ["next"] = [["next", "--full", "abc10000"], ["next", "abc10000"]]
        };

    [Fact]
    public void ServedForms_IndependentTable_MatchesConventionExactly()
    {
        var field = typeof(CliCommandCapabilitiesTestsQueryRouteConvention)
            .GetField(nameof(ServedForms), BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var convention = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[][]>>(
            field.GetValue(null));

        Assert.Equal(convention.Keys.OrderBy(key => key, StringComparer.Ordinal),
            ServedForms.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var (verb, forms) in convention)
        {
            var actual = ServedForms[verb];
            Assert.Equal(forms.Length, actual.Length);
            for (var index = 0; index < forms.Length; index++)
                Assert.True(forms[index].SequenceEqual(actual[index], StringComparer.Ordinal),
                    $"{verb}: argument form {index} differs from the convention.");
        }
    }

    [Fact]
    public void QueryOnlyVerbs_TableAndFiveExemptions_CoverEveryVerb()
    {
        string[] exemptions =
            ["monitor-goal", "owner-digest", "context-usage", "agent-list", "worker-profile-list"];
        foreach (var verb in CliCommandCapabilities.QueryOnlyVerbs)
            Assert.True(ServedForms.ContainsKey(verb) ||
                exemptions.Contains(verb, StringComparer.OrdinalIgnoreCase),
                $"{verb}: needs a served form or a named exemption.");
    }

    public static TheoryData<string, string[]> ServedFormCases()
    {
        var cases = new TheoryData<string, string[]>();
        foreach (var (verb, forms) in ServedForms)
            foreach (var args in forms)
                cases.Add(verb, args);
        return cases;
    }

    [Theory]
    [MemberData(nameof(ServedFormCases))]
    public void ServedForm_Execution_HasNoWriterEffects(string verb, string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(CreateSeed(workspace)) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(
                    args, repository, workspace, new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out var changed),
                    $"{verb}: read-only execution declined '{string.Join(" ", args)}'.");
                Assert.False(changed);
            });

            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Equal(0, repository.OutboxClaimAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.All(repository.ObservedWriteOperationTags, tag => Assert.True(tag is null,
                $"{verb}: read-only execution of '{string.Join(" ", args)}' observed write tag '{tag}'."));
            Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriterPathForm_PersistentRunner_ObservesCliWriteOperationTag()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(CreateSeed(workspace));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            CaptureConsole(() =>
            {
                // Bare status uses the writer scope and drains without opening a transaction.
                Assert.False(CliPersistentStateRunner.ExecuteCommand(
                    ["status"], repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                    ref profiles, ref currentGoal));
            });

            Assert.True(repository.ListOutboxMessagesCount >= 1);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Contains(repository.ObservedWriteOperationTags,
                tag => tag is not null && tag.StartsWith("cli:", StringComparison.Ordinal));
            Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriterPathForms_Execution_ReadOnlyRunnerDeclines()
    {
        string[][] forms = [["status"]];
        foreach (var args in forms)
        {
            var root = CreateTempDirectory();
            try
            {
                var workspace = OrchestratorWorkspace.ForDirectory(root);
                var repository = new ProbeStateRepository(CreateSeed(workspace)) { ThrowOnOutbox = true };
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                CaptureConsole(() => Assert.False(CliReadOnlyCommandRunner.TryExecute(
                    args, repository, workspace, new InMemoryModelProviderRegistry([]), null,
                    ref agents, ref profiles, ref currentGoal, out _)));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static AgentOrchestratorKernel CreateSeed(OrchestratorWorkspace workspace)
    {
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        _ = new BacklogStore(workspace.BacklogStorePath);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.BacklogStorePath,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id)
                VALUES ('abc10000eeeeeeeeeeeeeeeeeeeeeeee', 'Query route seed', '', 'Open',
                        '2026-10-07T00:00:00Z', '2026-10-07T00:00:00Z', NULL);
                """;
            command.ExecuteNonQuery();
        }
        _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(new TaskId("abc10000dddddddddddddddddddddddd"),
            "Query route task", AgentRole.Developer);
        kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Query route goal", [task]);
        return kernel;
    }
}
