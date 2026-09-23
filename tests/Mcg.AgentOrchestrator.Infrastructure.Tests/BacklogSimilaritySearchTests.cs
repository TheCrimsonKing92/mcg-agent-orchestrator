using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BacklogSimilaritySearchTests
{
    [Xunit.Fact]
    public async Task SearchRanksBodyAndNoteMatchesAndKeepsEveryHistoricalStatusAndCompletedGoals()
    {
        var databasePath = TempDb();
        var store = new BacklogStore(databasePath);
        var bodyMatch = await store.AddAsync("Body pointer", "bodyonlynebulaword");
        var noteMatch = await store.AddAsync("Note pointer");
        await store.AppendNoteAsync(noteMatch.Id, "notesonlyquasarword");
        var done = await store.AddAsync("Historical done", "sharedhistoryword");
        await store.CloseAsync(done.Id);
        var superseded = await store.AddAsync("Historical superseded", "sharedhistoryword");
        await store.UpdateAsync(superseded.Id, new BacklogItemUpdate(Status: BacklogItemStatus.Superseded));

        var corpus = (await BacklogSimilaritySearch.LoadBacklogDocumentsAsync(databasePath)).ToList();
        corpus.Add(new SimilarityDocument(
            "goal", "0123456789abcdef", GoalStatus.Completed.ToString(), "Completed goal pointer",
            new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), "sharedhistoryword completed objective"));

        var bodyHits = BacklogSimilaritySearch.Search(corpus, "bodyonlynebulaword");
        var noteHits = BacklogSimilaritySearch.Search(corpus, "notesonlyquasarword");
        var historicalHits = BacklogSimilaritySearch.Search(corpus, "sharedhistoryword");

        Assert.Equal(bodyMatch.Id, Assert.Single(bodyHits).Id);
        Assert.Equal(noteMatch.Id, Assert.Single(noteHits).Id);
        Assert.Contains(historicalHits, hit => hit.Status == BacklogItemStatus.Done.ToString());
        Assert.Contains(historicalHits, hit => hit.Status == BacklogItemStatus.Superseded.ToString());
        Assert.Contains(historicalHits, hit => hit.Kind == "goal" && hit.Status == GoalStatus.Completed.ToString());
    }

    [Xunit.Fact]
    public void SearchExcludesTheSourceItemBeforeApplyingTheLimit()
    {
        var now = DateTimeOffset.UtcNow;
        SimilarityDocument[] corpus =
        [
            new("backlog", "source-item", "Open", "Source", now, "alpha beta gamma alpha beta gamma"),
            new("backlog", "related-item", "Open", "Related", now, "alpha beta"),
            new("backlog", "other-item", "Open", "Other", now, "gamma")
        ];

        var hits = BacklogSimilaritySearch.Search(
            corpus, "alpha beta gamma", limit: 1, excludeId: "source-item");

        var hit = Assert.Single(hits);
        Assert.Equal("related-item", hit.Id);
        Assert.DoesNotContain(hits, candidate => candidate.Id == "source-item");
    }

    [Xunit.Fact]
    public void SearchHonorsLimitStatusAndBoundedOptInExcerpt()
    {
        var now = DateTimeOffset.UtcNow;
        var longText = string.Join(' ', Enumerable.Repeat("excerptword", 100));
        SimilarityDocument[] corpus =
        [
            new("backlog", "open-item", "Open", "Open", now, longText),
            new("backlog", "done-item", "Done", "Done", now, "excerptword"),
            new("goal", "goal-item", "Completed", "Goal", now, "excerptword")
        ];

        var defaultHits = BacklogSimilaritySearch.Search(corpus, "excerptword", limit: 1);
        var filteredHits = BacklogSimilaritySearch.Search(
            corpus, "excerptword", limit: 10, includeExcerpt: true, statusFilter: "Done");

        Assert.Single(defaultHits);
        Assert.Null(defaultHits[0].Excerpt);
        Assert.DoesNotContain(filteredHits, hit => hit.Kind == "backlog" && hit.Status != "Done");
        Assert.Contains(filteredHits, hit => hit.Kind == "goal");
        Assert.All(filteredHits, hit => Assert.InRange(hit.Excerpt!.Length, 1, 200));
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"")]
    [Xunit.InlineData("(")]
    [Xunit.InlineData(")")]
    [Xunit.InlineData("*")]
    [Xunit.InlineData(":")]
    [Xunit.InlineData("AND")]
    [Xunit.InlineData("OR")]
    [Xunit.InlineData("NOT")]
    [Xunit.InlineData("NEAR")]
    [Xunit.InlineData("\"(searchword)*: AND OR NOT NEAR")]
    public void SearchTreatsFtsSyntaxAsPlainInput(string query)
    {
        var corpus = new[]
        {
            new SimilarityDocument(
                "backlog", "safe-item", "Open", "Safe", DateTimeOffset.UtcNow,
                "searchword AND OR NOT NEAR")
        };

        var exception = Record.Exception(() => BacklogSimilaritySearch.Search(corpus, query));

        Assert.Null(exception);
    }

    [Xunit.Fact]
    public async Task SearchDoesNotChangePersistentTablesOrFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-backlog-similar-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "backlog.db");
        var store = new BacklogStore(databasePath);
        await store.AddAsync("Stable source", "unchangedword");
        var beforeTables = ReadTables(databasePath);
        var beforeFiles = SnapshotFiles(root);

        var corpus = await BacklogSimilaritySearch.LoadBacklogDocumentsAsync(databasePath);
        _ = BacklogSimilaritySearch.Search(corpus, "unchangedword");

        Assert.Equal(beforeTables, ReadTables(databasePath));
        Assert.Equal(beforeFiles, SnapshotFiles(root));
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "mcg-backlog-similar-" + Guid.NewGuid().ToString("n") + ".db");

    private static string[] ReadTables(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string[] SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
}
