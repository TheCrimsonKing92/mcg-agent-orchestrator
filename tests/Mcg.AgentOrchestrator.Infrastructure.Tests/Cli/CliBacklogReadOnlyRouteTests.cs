using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its databases, repository and temporary workspace.
public sealed class CliBacklogReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task ReadForms_ClassifyAndLoadAllGoalsWithoutWriterOrOutboxAccess()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateSeedAsync(root);
            foreach (var args in ExplicitForms(seed.Prerequisite.Id[..8]).Concat(FlaggedForms(seed.Prerequisite.Id[..8])))
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var repository = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = seed.Kernel.Goals.First();
                var originalGoal = currentGoal;
                var output = CaptureConsole(() =>
                {
                    Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                        args, repository, seed.Workspace, new InMemoryModelProviderRegistry([]), null,
                        ref agents, ref profiles, ref currentGoal, out var changed));
                    Xunit.Assert.False(changed);
                });

                Xunit.Assert.NotEmpty(output);
                Xunit.Assert.Same(originalGoal, currentGoal);
                Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
                Xunit.Assert.Equal(1, repository.LoadGoalsCount);
                Xunit.Assert.Equal(seed.Kernel.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
                    repository.LoadedGoalIds.Order(StringComparer.Ordinal));
                AssertNoWriterAccess(repository);
                if (args[0] == "backlog-show")
                {
                    Xunit.Assert.Contains($"Owner:   {LinkedGoalId}", output);
                    Xunit.Assert.Contains("authority=authoritative", output);
                    Xunit.Assert.Contains("Dependents:", output);
                }
                if (args.SequenceEqual(new[] { "backlog-similar", "quartz" }))
                    Xunit.Assert.Contains($"kind=goal id={CompletedGoalId[..8]}", output);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void WritesAndHelpShapes_DeclineWithoutRepositoryAccess()
    {
        string[][] writes =
        [
            ["backlog-add"], ["backlog-add", "title"], ["backlog-add", "title", "--no-similar"],
            ["backlog-add", "title", "--depends-on", "abc"], ["backlog-add", "--title", "title"],
            ["backlog-add", "title", "--depends-on", "abc", "--no-similar"],
            ["backlog-add", "title", "--body-file", "body.md"],
            ["backlog-triage"], ["backlog-update", "abc"], ["backlog-close", "abc"],
            ["backlog-depends", "abc"], ["backlog-depends", "abc", "--on", "def"],
            ["backlog-depends", "abc", "--remove", "def"], ["backlog-depends", "abc", "--clear"],
            ["backlog-annotate", "abc", "note"], ["backlog-link", "abc", "def"],
            ["backlog-supersede", "abc", "def"], ["backlog-unsupersede", "abc"]
        ];
        foreach (var args in writes)
        {
            AssertDeclined(args);
            AssertDeclined([.. args, "--help"]);
        }
        foreach (var args in ExplicitForms("abc").Concat(FlaggedForms("abc")))
        {
            foreach (var help in new[] { "--help", "-h" })
                AssertDeclined([.. args, help]);
            AssertDeclined(["help", args[0]]);
        }
        AssertDeclined([]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("backlog-list")]
    [Xunit.InlineData("backlog-show")]
    [Xunit.InlineData("backlog-similar")]
    public void InvalidFlags_RejectBeforeAnyRepositoryRead(string verb)
    {
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Xunit.Assert.Throws<ArgumentException>(() => CliReadOnlyCommandRunner.TryExecute(
            [verb, "--unknown"], repository, OrchestratorWorkspace.ForDirectory(Path.GetTempPath()),
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out _));
        Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        AssertNoWriterAccess(repository);
    }

    private static void AssertDeclined(string[] args)
    {
        Xunit.Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Xunit.Assert.False(CliReadOnlyCommandRunner.TryExecute(
            args, repository, OrchestratorWorkspace.ForDirectory(Path.GetTempPath()),
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out var changed));
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(0, repository.ListGoalMetadataCount);
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        AssertNoWriterAccess(repository);
    }

    private static void AssertNoWriterAccess(ProbeStateRepository repository)
    {
        Xunit.Assert.Equal(0, repository.MutationAttempts);
        Xunit.Assert.Equal(0, repository.SaveAttempts);
        Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
        Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
        Xunit.Assert.Equal(0, repository.FullLoadAttempts);
    }

    internal const string LinkedGoalId = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string CompletedGoalId = "abc20000bbbbbbbbbbbbbbbbbbbbbbbb";

    internal static string[][] ExplicitForms(string prefix) =>
    [
        ["backlog-list"], ["backlog-list", "--status", "open"], ["backlog-show", prefix],
        ["backlog-similar", "quartz"], ["backlog-similar", "--id", prefix]
    ];

    internal static string[][] FlaggedForms(string prefix) =>
    [
        ["BACKLOG-LIST", "--STATUS", "open"],
        ["backlog-list", "--text", "quartz", "--limit", "1"],
        ["backlog-similar", "quartz", "--excerpt", "--limit", "2", "--status", "open"],
        ["BACKLOG-SHOW", prefix.ToUpperInvariant()],
        ["backlog-similar", "--id", prefix, "--excerpt"]
    ];

    internal sealed record Seed(
        OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel,
        BacklogItem Prerequisite, BacklogItem Dependent);

    internal static async Task<Seed> CreateSeedAsync(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var prerequisite = await store.AddAsync("Quartz query prerequisite", "Read linked goal claims");
        var dependent = await store.AddAsync("Quartz query dependent", "Render dependency readiness");
        await store.AddDependencyAsync(dependent.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        var kernel = new AgentOrchestratorKernel();
        var linked = kernel.CreateGoal(new GoalId(LinkedGoalId), "Linked quartz goal");
        kernel.SetGoalSourceBacklogItemId(linked.Id, prerequisite.Id);
        kernel.CreateGoal(new GoalId(CompletedGoalId), "Completed quartz query corpus");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == CompletedGoalId
                ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        });
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var claim = await repository.TransactAsync((persistedKernel, _) =>
        {
            var materialized = claimStore.ResolveOrMaterializeClaim(persistedKernel, prerequisite.Id);
            return Task.FromResult((ShouldSave: false, Result: materialized));
        });
        Xunit.Assert.Equal(LinkedGoalId, claim.OwnerGoalId);
        // An empty kernel prevents legacy-link fallback from masking an uncommitted claim.
        var persistedClaim = claimStore.ResolveClaim(new AgentOrchestratorKernel(), prerequisite.Id);
        Xunit.Assert.NotNull(persistedClaim);
        Xunit.Assert.Equal(LinkedGoalId, persistedClaim.OwnerGoalId);
        Xunit.Assert.Equal(2, kernel.Goals.Count);
        Xunit.Assert.Single(kernel.Goals, goal => goal.Status == GoalStatus.Completed);
        Xunit.Assert.Single((await store.GetByIdPrefixAsync(dependent.Id))!.Dependencies);
        _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        return new(workspace, kernel, prerequisite, dependent);
    }

    [Xunit.Fact]
    public async Task ListAndShow_SetUpWorkspace_PrintUnchangedOutputWithoutWriting()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateSeedAsync(root);
            var bytes = File.ReadAllBytes(seed.Workspace.BacklogStorePath);

            var listing = RunBacklogRead(["backlog-list"], seed.Workspace, seed.Kernel);
            Xunit.Assert.Contains($"{seed.Prerequisite.Id} | status=", listing);
            Xunit.Assert.Contains($"| goal={LinkedGoalId} | title=Quartz query prerequisite", listing);
            Xunit.Assert.Contains($"{seed.Dependent.Id} | status=", listing);
            Xunit.Assert.Contains("| title=Quartz query dependent", listing);
            Xunit.Assert.Contains($"Backlog list: 2 item(s) from backlog store{Environment.NewLine}", listing);

            var shown = RunBacklogRead(["backlog-show", seed.Prerequisite.Id[..8]], seed.Workspace, seed.Kernel);
            Xunit.Assert.Contains($"Id:      {seed.Prerequisite.Id}{Environment.NewLine}", shown);
            Xunit.Assert.Contains($"Title:   Quartz query prerequisite{Environment.NewLine}", shown);
            Xunit.Assert.Contains($"Status:  Open{Environment.NewLine}", shown);
            Xunit.Assert.Contains($"Created: {seed.Prerequisite.CreatedAt:O}{Environment.NewLine}", shown);
            Xunit.Assert.Contains($"Updated: {seed.Prerequisite.UpdatedAt:O}{Environment.NewLine}", shown);
            Xunit.Assert.Contains($"Owner:   {LinkedGoalId}{Environment.NewLine}", shown);
            Xunit.Assert.Contains("authority=authoritative", shown);
            Xunit.Assert.Contains($"Dependents:{Environment.NewLine}- {seed.Dependent.Id}{Environment.NewLine}", shown);
            Xunit.Assert.Contains($"Read linked goal claims{Environment.NewLine}", shown);
            Xunit.Assert.Equal(bytes, File.ReadAllBytes(seed.Workspace.BacklogStorePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("backlog-list", false)]
    [Xunit.InlineData("backlog-list", true)]
    [Xunit.InlineData("backlog-show", false)]
    [Xunit.InlineData("backlog-show", true)]
    public void ListAndShow_MissingOrUnversionedBacklog_FailWithoutCreatingTables(
        string verb, bool legacy)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            if (legacy)
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = workspace.BacklogStorePath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE backlog (id TEXT PRIMARY KEY, title TEXT NOT NULL)";
                command.ExecuteNonQuery();
            }
            var bytes = legacy ? File.ReadAllBytes(workspace.BacklogStorePath) : null;
            var schema = legacy ? ReadBacklogSchema(workspace.BacklogStorePath) : null;
            var args = verb == "backlog-list" ? new[] { verb } : new[] { verb, "legacy" };

            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                RunBacklogRead(args, workspace, new AgentOrchestratorKernel()));

            Xunit.Assert.Equal($"Backlog store '{workspace.BacklogStorePath}' schema is Missing (expected version 1); run setup.", error.Message);
            if (legacy)
            {
                Xunit.Assert.Equal(bytes, File.ReadAllBytes(workspace.BacklogStorePath));
                Xunit.Assert.Equal(schema, ReadBacklogSchema(workspace.BacklogStorePath));
                Xunit.Assert.DoesNotContain(schema!, row => row.StartsWith("store_schema_versions:", StringComparison.Ordinal));
                Xunit.Assert.DoesNotContain(schema!, row => row.StartsWith("backlog_notes:", StringComparison.Ordinal));
            }
            else
                Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RunBacklogRead(string[] args, OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
    {
        var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var output = CaptureConsole(() =>
        {
            Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal, out var changed));
            Xunit.Assert.False(changed);
        });
        AssertNoWriterAccess(repository);
        return output;
    }

    private static string[] ReadBacklogSchema(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name || ':' || coalesce(sql, '') FROM sqlite_schema ORDER BY name";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }
}
