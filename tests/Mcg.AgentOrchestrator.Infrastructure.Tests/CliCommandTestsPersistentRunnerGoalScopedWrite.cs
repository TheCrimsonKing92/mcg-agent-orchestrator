using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerGoalScopedWrite : CliCommandTestBase
{
    [Fact]
    public async Task Revise_UnrelatedGoalAndRequest_PreservesStoredBytes()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Original brief", [new TaskSpec(TaskId.New(), "work", AgentRole.Developer)]);
        var unrelated = kernel.CreateGoal("Unrelated operator decision");
        var request = kernel.RequestHumanInput(unrelated.Id, null, "Which option?");
        await repository.SaveAsync(kernel);
        IndentStoredRows(workspace, unrelated.Id, request.Id.Value);
        StoredGoal before;
        string requestBefore;
        using (var conn = OpenState(workspace))
        {
            before = ReadGoal(conn, unrelated.Id);
            requestBefore = ReadRequest(conn, request.Id.Value);
            Assert.Contains('\n', before.Json);
            Assert.Contains('\n', requestBefore);
        }
        const string revisedBrief = "Revised focused brief";
        var briefPath = Path.Combine(root, "brief.md");
        File.WriteAllText(briefPath, revisedBrief);
        (bool Changed, Goal? CurrentGoal) result = default;

        _ = CaptureConsole(() => result = Execute(repository, workspace,
            ["revise", target.Id.Value[..8], "--brief-file", briefPath], unrelated));

        Assert.True(result.Changed);
        Assert.Equal(target.Id, result.CurrentGoal!.Id);
        Assert.Equal(revisedBrief, (await repository.LoadAsync()).GetGoal(target.Id).AuthoritativeBrief.Text);
        using var after = OpenState(workspace);
        Assert.Equal(before, ReadGoal(after, unrelated.Id));
        Assert.Equal(requestBefore, ReadRequest(after, request.Id.Value));
    }

    [Fact]
    public async Task GoalDepends_NewDependency_PreservesUnrelatedStoredBytes()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var dependent = kernel.CreateGoal("Dependent goal");
        var dependency = kernel.CreateGoal("Prerequisite goal");
        var unrelated = kernel.CreateGoal("Unrelated goal");
        await repository.SaveAsync(kernel);
        IndentStoredRows(workspace, unrelated.Id);
        StoredGoal before;
        StoredGoal dependencyBefore;
        using (var conn = OpenState(workspace))
        {
            before = ReadGoal(conn, unrelated.Id);
            dependencyBefore = ReadGoal(conn, dependency.Id);
            Assert.Contains('\n', before.Json);
        }
        (bool Changed, Goal? CurrentGoal) result = default;

        _ = CaptureConsole(() => result = Execute(repository, workspace,
            ["goal-depends", dependent.Id.Value[..8], "--on", dependency.Id.Value[..8]], unrelated));

        Assert.True(result.Changed);
        Assert.Same(unrelated, result.CurrentGoal);
        Assert.Equal(dependency.Id, Assert.Single((await repository.LoadAsync()).GetGoal(dependent.Id).DependsOn));
        using var after = OpenState(workspace);
        Assert.Equal(before, ReadGoal(after, unrelated.Id));
        Assert.Equal(dependencyBefore, ReadGoal(after, dependency.Id));
    }

    [Fact]
    public async Task GoalDepends_TransitiveCycle_RejectsWithoutWritingDependent()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var x = kernel.CreateGoal("Goal X");
        var y = kernel.CreateGoal("Goal Y");
        var z = kernel.CreateGoal("Goal Z");
        kernel.CreateGoal("Unrelated goal");
        kernel.SetGoalDependency(y.Id, z.Id);
        kernel.SetGoalDependency(z.Id, x.Id);
        await repository.SaveAsync(kernel);
        IndentStoredRows(workspace, x.Id);
        StoredGoal before;
        using (var conn = OpenState(workspace))
            before = ReadGoal(conn, x.Id);

        var error = Assert.Throws<InvalidOperationException>(() => CaptureConsole(() => Execute(
            repository, workspace, ["goal-depends", x.Id.Value[..8], "--on", y.Id.Value[..8]])));

        Assert.Contains("would create a cycle", error.Message);
        Assert.Empty((await repository.LoadAsync()).GetGoal(x.Id).DependsOn);
        using var after = OpenState(workspace);
        Assert.Equal(before, ReadGoal(after, x.Id));
    }

    [Theory]
    [InlineData("--remove")]
    [InlineData("--clear")]
    public async Task GoalDepends_Removal_PreservesOtherEdgesAndUnrelatedRows(string flag)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var dependent = kernel.CreateGoal("Dependent goal");
        var dependency = kernel.CreateGoal("Removed prerequisite");
        var other = kernel.CreateGoal("Other prerequisite outside remove scope");
        kernel.SetGoalDependency(dependent.Id, dependency.Id);
        kernel.SetGoalDependency(dependent.Id, other.Id);
        await repository.SaveAsync(kernel);
        IndentStoredRows(workspace, other.Id);
        StoredGoal before;
        using (var conn = OpenState(workspace))
            before = ReadGoal(conn, other.Id);
        string[] args = flag == "--clear"
            ? ["GOAL-DEPENDS", dependent.Id.Value[..8], flag]
            : ["GOAL-DEPENDS", dependent.Id.Value[..8], flag, dependency.Id.Value[..8]];

        _ = CaptureConsole(() => Assert.True(Execute(repository, workspace, args).Changed));

        var restored = (await repository.LoadAsync()).GetGoal(dependent.Id);
        Assert.Equal(flag == "--clear" ? Array.Empty<GoalId>() : [other.Id], restored.DependsOn);
        using var after = OpenState(workspace);
        Assert.Equal(before, ReadGoal(after, other.Id));
    }

    [Theory]
    [InlineData("revise", "abc", true)]
    [InlineData("REVISE", "abc", true)]
    [InlineData("revise", "--brief-file", false)]
    [InlineData("goal-depends", "abc", true)]
    [InlineData("GOAL-DEPENDS", "abc", true)]
    [InlineData("goal-depends", "--clear", false)]
    [InlineData("recover", "abc", false)]
    public void WriteRoute_ArgumentShape_SelectsOnlyScopedVerbs(string verb, string target, bool expected)
    {
        Assert.Equal(expected, CliPersistentStateRunner.IsGoalScopedWriteCommand([verb, target]));
        Assert.False(CliPersistentStateRunner.IsGoalScopedWriteCommand(["revise", target, "--history"]));
        Assert.False(CliPersistentStateRunner.IsGoalScopedWriteCommand([verb]));
        Assert.False(CliPersistentStateRunner.IsGoalScopedWriteCommand([]));
    }

    private static (bool Changed, Goal? CurrentGoal) Execute(SqliteOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, IReadOnlyList<string> args, Goal? currentGoal = null)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var changed = CliPersistentStateRunner.ExecuteCommand(args, repository, workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal);
        return (changed, currentGoal);
    }

    private static SqliteConnection OpenState(OrchestratorWorkspace workspace)
    {
        var conn = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Pooling=False");
        conn.Open();
        return conn;
    }

    private static void IndentStoredRows(OrchestratorWorkspace workspace, GoalId goal, string? request = null)
    {
        using var conn = OpenState(workspace);
        var goalJson = ReadGoal(conn, goal).Json;
        var requestJson = request is null ? null : ReadRequest(conn, request);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", goal.Value);
        cmd.Parameters.AddWithValue("$json", Indent(goalJson));
        Assert.Equal(1, cmd.ExecuteNonQuery());
        if (request is null)
            return;
        cmd.CommandText = "UPDATE human_input_requests SET snapshot_json = $json WHERE id = $id";
        cmd.Parameters["$id"].Value = request;
        cmd.Parameters["$json"].Value = Indent(requestJson!);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static string Indent(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static StoredGoal ReadGoal(SqliteConnection conn, GoalId goal)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, updated_at, snapshot_json FROM goals WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", goal.Value);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return new StoredGoal(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    private static string ReadRequest(SqliteConnection conn, string request)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_json FROM human_input_requests WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", request);
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    private sealed record StoredGoal(long Version, string UpdatedAt, string Json);
}
