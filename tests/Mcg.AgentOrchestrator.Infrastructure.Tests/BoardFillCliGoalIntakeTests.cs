using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Real in-process intake against a private SQLite workspace; no CLI/model child is launched.
[Collection(TestCollections.CliProcessEnvironment)]
public sealed class BoardFillCliGoalIntakeTests : CliCommandTestBase
{
    [Fact]
    public async Task Real_adapter_replays_one_goal_with_full_backlog_link_and_applies_dependency()
    {
        var root = Path.Combine(Path.GetTempPath(), "board-fill-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var sandbox = ClearWorkerSandboxEnv();
            var workspace = CreateRefinedWorkspace(root);
            AgentCatalogStore.Save(workspace.AgentCatalogPath, AgentCatalog.Default());
            WorkerProfileStore.Save(workspace.WorkerProfilePath, WorkerProfileCatalog.Default());
            var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("File this tested backlog slice");
            var path = Path.Combine(root, "draft.md");
            File.WriteAllText(path, BoardFillAssessmentTestFixture.Brief);
            var request = new BoardFillIntakeRequest("draft-identity", path, item.Id);
            var adapter = new BoardFillCliGoalIntake(workspace);
            var filed = adapter.File(request, CancellationToken.None);
            var replayed = adapter.File(request, CancellationToken.None);
            Assert.Equal("filed", filed.Kind);
            Assert.Equal("replayed", replayed.Kind);
            Assert.Equal(0, filed.ExitCode);
            Assert.Equal(0, replayed.ExitCode);
            Assert.Equal(filed.GoalId, replayed.GoalId);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = await repository.LoadAsync();
            var goal = Assert.Single(kernel.Goals);
            Assert.Equal(item.Id, goal.SourceBacklogItemId);
            Assert.Equal(SourceBacklogCoverage.Full, goal.SourceBacklogCoverage);
            Assert.Equal(goal.Id.Value, new GoalIntakeRequestStore(workspace.SqliteStatePath).Get(request.RequestKey)!.GoalId);
            Assert.Contains("\"kind\":\"goal-intake-receipt\"", filed.Stdout);
            var dependency = kernel.CreateGoal("Dependency fixture");
            await repository.SaveAsync(kernel);
            var applied = adapter.Depend(goal.Id.Value, dependency.Id.Value, CancellationToken.None);
            Assert.Equal("applied", applied.Kind);
            Assert.Equal(0, applied.ExitCode);
            var stored = (await repository.LoadAsync()).Goals.Single(row => row.Id == goal.Id);
            Assert.Equal(dependency.Id, Assert.Single(stored.DependsOn));
            // Terminal links still prevent a new filing of the same backlog item.
            var latest = await repository.LoadAsync();
            latest.CancelGoal(goal.Id, "Fixture terminal link");
            await repository.SaveAsync(latest);
            Assert.Contains(item.Id, adapter.ReadBoard().LinkedBacklogItemIds);
            Assert.Equal(1, adapter.ReadBoard().NonTerminalCount);
            File.WriteAllText(path, BoardFillAssessmentTestFixture.Brief + "\nChanged objective.");
            var conflict = adapter.File(request, CancellationToken.None);
            Assert.Equal("failed", conflict.Kind);
            Assert.Equal(1, conflict.ExitCode);
            Assert.Contains("GOAL_INTAKE_PAYLOAD_CONFLICT", conflict.Stderr);
            Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
