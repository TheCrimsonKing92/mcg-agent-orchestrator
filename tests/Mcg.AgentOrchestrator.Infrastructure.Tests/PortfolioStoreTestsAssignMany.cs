using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its portfolio database.
public sealed class PortfolioStoreTestsAssignMany
{
    [Xunit.Fact]
    public async Task MixedMembers_DryRunMatchesCommitAndPreservesAllMemberships()
    {
        var root = CreateRoot();
        try
        {
            var store = new PortfolioStore(Path.Combine(root, "portfolio.db"));
            var epic = await store.AddEpicAsync("Target");
            var other = await store.AddEpicAsync("Previous");
            await store.AssignGoalToEpicAsync("existing", epic.Id);
            await store.AssignBacklogItemToEpicAsync("moving", other.Id);
            PortfolioAssignmentRequest[] members =
            [
                new("new", PortfolioMemberKind.Goal, "new"),
                new("existing", PortfolioMemberKind.Goal, "existing"),
                new("moving", PortfolioMemberKind.BacklogItem, "moving"),
                new("missing", null, null)
            ];
            var beforeGoal = await store.GetGoalMembershipAsync("existing");
            var beforeBacklog = await store.GetBacklogMembershipAsync("moving");

            var preview = await store.AssignManyAsync(epic.Id, members, dryRun: true);

            Xunit.Assert.Equal(new[] { "assigned", "already-member", $"moved-from {other.Id}", "unknown" },
                preview.Select(result => result.Token));
            Xunit.Assert.Equal(members, preview.Select(result => result.Request));
            Xunit.Assert.Equal(other.Id, preview[2].PreviousEpicId);
            Xunit.Assert.Null(await store.GetGoalMembershipAsync("new"));
            Xunit.Assert.Equal(beforeGoal, await store.GetGoalMembershipAsync("existing"));
            Xunit.Assert.Equal(beforeBacklog, await store.GetBacklogMembershipAsync("moving"));
            Xunit.Assert.Null(await store.GetGoalMembershipAsync("missing"));
            Xunit.Assert.Null(await store.GetBacklogMembershipAsync("missing"));

            var committed = await store.AssignManyAsync(epic.Id, members, dryRun: false);

            Xunit.Assert.Equal(preview, committed);
            Xunit.Assert.Equal(epic.Id, (await store.GetGoalMembershipAsync("new"))!.EpicId);
            Xunit.Assert.Equal(beforeGoal, await store.GetGoalMembershipAsync("existing"));
            Xunit.Assert.Equal(epic.Id, (await store.GetBacklogMembershipAsync("moving"))!.EpicId);
            Xunit.Assert.Equal(3, (await store.ListEpicMembersAsync(epic.Id)).Count);
            Xunit.Assert.Empty(await store.ListEpicMembersAsync(other.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task DatabaseErrorAfterFirstWrite_RollsBackWholeBatch()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "portfolio.db");
            var store = new PortfolioStore(path);
            var epic = await store.AddEpicAsync("Target");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER reject_second BEFORE INSERT ON goal_epic_memberships
                    WHEN NEW.goal_id = 'second' BEGIN SELECT RAISE(ABORT, 'reject second'); END;
                    """;
                command.ExecuteNonQuery();
            }
            var error = await Xunit.Assert.ThrowsAsync<SqliteException>(() => store.AssignManyAsync(epic.Id,
                [new("first", PortfolioMemberKind.Goal, "first"),
                 new("second", PortfolioMemberKind.Goal, "second")], dryRun: false));
            Xunit.Assert.Contains("reject second", error.Message);
            Xunit.Assert.Null(await store.GetGoalMembershipAsync("first"));
            Xunit.Assert.Null(await store.GetGoalMembershipAsync("second"));
            Xunit.Assert.Empty(await store.ListEpicMembersAsync(epic.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task AliasesOfSameMember_ReuseFirstOutcome()
    {
        var root = CreateRoot();
        try
        {
            var store = new PortfolioStore(Path.Combine(root, "portfolio.db"));
            var epic = await store.AddEpicAsync("Target");
            var results = await store.AssignManyAsync(epic.Id,
                [new("prefix", PortfolioMemberKind.Goal, "member"),
                 new("member", PortfolioMemberKind.Goal, "member")], dryRun: false);
            Xunit.Assert.Equal(new[] { "assigned", "assigned" }, results.Select(result => result.Token));
            Xunit.Assert.Equal(new[] { "prefix", "member" }, results.Select(result => result.Request.RequestedId));
            Xunit.Assert.Single(await store.ListEpicMembersAsync(epic.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-portfolio-batch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
