using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class BacklogStoreTests
{
    // ── Store round-trip ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BacklogStore_add_and_list_open_items")]
    public async Task AddAndListOpenItems()
    {
        var store = new BacklogStore(TempDb());
        var a = await store.AddAsync("Feature A", "Body A");
        var b = await store.AddAsync("Feature B");

        var open = await store.ListAsync(includeAll: false);

        Assert.Equal(2, open.Count);
        Assert.True(open.Any(i => i.Id == a.Id && i.Title == "Feature A" && i.Body == "Body A"));
        Assert.True(open.Any(i => i.Id == b.Id && i.Title == "Feature B"));
        Assert.True(open.All(i => i.Status == BacklogItemStatus.Open));
    }

    [Xunit.Fact(DisplayName = "BacklogStore_list_excludes_done_by_default")]
    public async Task ListExcludesDoneByDefault()
    {
        var store = new BacklogStore(TempDb());
        var open = await store.AddAsync("Open item");
        var toClose = await store.AddAsync("Will be closed");
        await store.CloseAsync(toClose.Id);

        var openOnly = await store.ListAsync(includeAll: false);
        var all = await store.ListAsync(includeAll: true);

        Assert.Equal(1, openOnly.Count);
        Assert.True(openOnly.Single().Id == open.Id);
        Assert.Equal(2, all.Count);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_get_by_id_prefix_resolves_unique_match")]
    public async Task GetByIdPrefixResolvesUniqueMatch()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Prefix test");

        // Use first 8 chars of the GUID id as the prefix
        var prefix = item.Id[..8];
        var found = await store.GetByIdPrefixAsync(prefix);

        Assert.True(found is not null);
        Assert.Equal(item.Id, found!.Id);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_get_by_id_prefix_returns_null_for_no_match")]
    public async Task GetByIdPrefixReturnsNullForNoMatch()
    {
        var store = new BacklogStore(TempDb());
        await store.AddAsync("Some item");

        var result = await store.GetByIdPrefixAsync("zzzzzzzz-no-match");

        Assert.True(result is null);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_close_sets_status_to_done")]
    public async Task CloseSetStatusToDone()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Close me");

        var closed = await store.CloseAsync(item.Id);

        Assert.Equal(BacklogItemStatus.Done, closed.Status);
        Assert.Equal(item.Id, closed.Id);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_close_with_reason_appends_to_body")]
    public async Task CloseWithReasonAppendsToBody()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Task", "Original body");

        var closed = await store.CloseAsync(item.Id, "Superseded by new approach");

        Assert.Equal(BacklogItemStatus.Done, closed.Status);
        Assert.True(closed.Body.Contains("Original body", StringComparison.Ordinal));
        Assert.True(closed.Body.Contains("Superseded by new approach", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BacklogStore_reopen_flips_done_back_to_open")]
    public async Task ReopenFlipsDoneBackToOpen()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Reopen me");
        await store.CloseAsync(item.Id);

        var reopened = await store.ReopenAsync(item.Id);

        Assert.Equal(BacklogItemStatus.Open, reopened.Status);
        Assert.Equal(item.Id, reopened.Id);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_source_goal_id_roundtrips")]
    public async Task SourceGoalIdRoundtrips()
    {
        var store = new BacklogStore(TempDb());
        var goalId = Guid.NewGuid().ToString("n");
        await store.UpsertAsync(new BacklogItem(
            BacklogStore.SlugId("Goal-linked item"),
            "Goal-linked item", "",
            BacklogItemStatus.Open,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            goalId));

        var items = await store.ListAsync(includeAll: true);
        Assert.Equal(1, items.Count);
        Assert.Equal(goalId, items[0].SourceGoalId);
    }

    // ── Upsert / idempotency ─────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BacklogStore_upsert_inserts_new_item")]
    public async Task UpsertInsertsNewItem()
    {
        var store = new BacklogStore(TempDb());
        var item = new BacklogItem(
            BacklogStore.SlugId("New upsert item"),
            "New upsert item", "Body",
            BacklogItemStatus.Open,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        var inserted = await store.UpsertAsync(item);

        Assert.True(inserted);
        var listed = await store.ListAsync();
        Assert.Equal(1, listed.Count);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_upsert_is_idempotent_on_same_id")]
    public async Task UpsertIsIdempotentOnSameId()
    {
        var store = new BacklogStore(TempDb());
        var now = DateTimeOffset.UtcNow;
        var item = new BacklogItem(
            "stable-slug", "Stable item", "Body",
            BacklogItemStatus.Open, now, now, null);

        var first = await store.UpsertAsync(item);
        var second = await store.UpsertAsync(item);

        Assert.True(first);
        Assert.False(second);
        var listed = await store.ListAsync();
        Assert.Equal(1, listed.Count);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_upsert_does_not_overwrite_done_status")]
    public async Task UpsertDoesNotOverwriteDoneStatus()
    {
        var store = new BacklogStore(TempDb());
        var now = DateTimeOffset.UtcNow;
        var item = new BacklogItem("my-slug", "My item", "", BacklogItemStatus.Open, now, now, null);
        await store.UpsertAsync(item);
        await store.CloseAsync("my-slug");

        // Re-import same item as Open — must not re-open it
        var reimport = item with { Status = BacklogItemStatus.Open };
        var inserted = await store.UpsertAsync(reimport);

        Assert.False(inserted);
        var all = await store.ListAsync(includeAll: true);
        Assert.Equal(BacklogItemStatus.Done, all.Single().Status);
    }

    // ── Import parsing ────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BacklogStore_import_parse_extracts_sections")]
    public void ImportParseExtractsSections()
    {
        var lines = new[]
        {
            "# Backlog",
            "",
            "## Feature A",
            "",
            "Body of feature A.",
            "",
            "## Feature B",
            "",
            "Body of B.",
            "",
            "## Shipped (closed — done)",
            "",
            "- Done item 1",
        };

        var sections = CliCommandHandlers.ParseBacklogMdSections(lines);

        Assert.Equal(3, sections.Count);
        Assert.True(sections[0].Title == "Feature A");
        Assert.False(sections[0].IsDone);
        Assert.True(sections[0].Body.Contains("Body of feature A.", StringComparison.Ordinal));
        Assert.True(sections[1].Title == "Feature B");
        Assert.False(sections[1].IsDone);
        Assert.True(sections[2].Title == "Shipped (closed — done)");
        Assert.True(sections[2].IsDone);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_import_parse_marks_closed_sections_as_done")]
    public void ImportParseMarksDoneClosedShippedAsDone()
    {
        var lines = new[]
        {
            "## DONE item",
            "## CLOSED by operator",
            "## SHIPPED to prod",
            "## Regular open item",
        };

        var sections = CliCommandHandlers.ParseBacklogMdSections(lines);

        Assert.Equal(4, sections.Count);
        Assert.True(sections[0].IsDone);
        Assert.True(sections[1].IsDone);
        Assert.True(sections[2].IsDone);
        Assert.False(sections[3].IsDone);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_import_from_file_is_idempotent")]
    public async Task ImportFromFileIsIdempotent()
    {
        var md = "# Backlog\n\n## Item One\n\nBody one.\n\n## Item Two\n\nBody two.\n";
        var mdPath = Path.Combine(CreateTempDirectory(), "BACKLOG.md");
        File.WriteAllText(mdPath, md);
        var store = new BacklogStore(TempDb());

        // First import
        var sections1 = CliCommandHandlers.ParseBacklogMdSections(File.ReadAllLines(mdPath));
        var now = DateTimeOffset.UtcNow;
        var added1 = 0;
        var skipped1 = 0;
        foreach (var (title, body, isDone) in sections1)
        {
            var id = BacklogStore.SlugId(title);
            var inserted = await store.UpsertAsync(new BacklogItem(id, title, body, isDone ? BacklogItemStatus.Done : BacklogItemStatus.Open, now, now, null));
            if (inserted) added1++; else skipped1++;
        }

        // Second import — all should be skipped
        var added2 = 0;
        var skipped2 = 0;
        foreach (var (title, body, isDone) in sections1)
        {
            var id = BacklogStore.SlugId(title);
            var inserted = await store.UpsertAsync(new BacklogItem(id, title, body, isDone ? BacklogItemStatus.Done : BacklogItemStatus.Open, now, now, null));
            if (inserted) added2++; else skipped2++;
        }

        Assert.Equal(2, added1);
        Assert.Equal(0, skipped1);
        Assert.Equal(0, added2);
        Assert.Equal(2, skipped2);
    }

    // ── View rendering ────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BacklogStore_view_renders_open_and_done_sections")]
    public async Task ViewRendersOpenAndDoneSections()
    {
        var store = new BacklogStore(TempDb());
        await store.AddAsync("Open item", "Open body");
        var toClose = await store.AddAsync("Done item");
        await store.CloseAsync(toClose.Id);

        var all = await store.ListAsync(includeAll: true);
        var md = CliCommandHandlers.RenderBacklogMarkdown(all);

        Assert.True(md.Contains("# Backlog", StringComparison.Ordinal));
        Assert.True(md.Contains("## Open item", StringComparison.Ordinal));
        Assert.True(md.Contains("Open body", StringComparison.Ordinal));
        Assert.True(md.Contains("## Shipped (closed)", StringComparison.Ordinal));
        Assert.True(md.Contains("Done item", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BacklogStore_view_with_no_done_items_omits_shipped_section")]
    public async Task ViewWithNoDoneItemsOmitsShippedSection()
    {
        var store = new BacklogStore(TempDb());
        await store.AddAsync("Open only");

        var all = await store.ListAsync(includeAll: true);
        var md = CliCommandHandlers.RenderBacklogMarkdown(all);

        Assert.True(md.Contains("## Open only", StringComparison.Ordinal));
        Assert.False(md.Contains("## Shipped", StringComparison.Ordinal));
    }

    // ── Slug id ───────────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "BacklogStore_slug_id_is_stable_and_lowercased")]
    public void SlugIdIsStableAndLowercased()
    {
        var id1 = BacklogStore.SlugId("My Feature Title");
        var id2 = BacklogStore.SlugId("My Feature Title");

        Assert.Equal(id1, id2);
        Assert.True(id1 == id1.ToLowerInvariant());
        Assert.True(id1.Contains("my", StringComparison.Ordinal));
        Assert.True(id1.Contains("feature", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BacklogStore_slug_id_truncates_to_60_chars")]
    public void SlugIdTruncatesTo60Chars()
    {
        var longTitle = "This is a very long title that definitely exceeds sixty characters in total length";
        var id = BacklogStore.SlugId(longTitle);
        Assert.True(id.Length <= 60);
    }

    private static string TempDb() => Path.Combine(CreateTempDirectory(), "backlog.db");
}
