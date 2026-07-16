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

    [Xunit.Fact(DisplayName = "BacklogStore_append_note_persists_ordered_notes_without_mutating_item_text")]
    public async Task AppendNotePersistsOrderedNotesWithoutMutatingItemText()
    {
        var dbPath = TempDb();
        var store = new BacklogStore(dbPath);
        var item = await store.AddAsync("Annotated item", "Original body");

        await store.AppendNoteAsync(item.Id, "First receipt");
        await store.AppendNoteAsync(item.Id, "Second receipt");

        var reloaded = await new BacklogStore(dbPath).GetByExactIdAsync(item.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("Annotated item", reloaded!.Title);
        Assert.Equal("Original body", reloaded.Body);
        Assert.Equal(BacklogItemStatus.Open, reloaded.Status);
        Assert.Equal(["First receipt", "Second receipt"], reloaded.Notes.Select(note => note.Text));
        Assert.True(reloaded.Notes[0].CreatedAt <= reloaded.Notes[1].CreatedAt);
    }

    // ── TryCloseByIdAsync (idempotent close) ──────────────────────────────────

    [Xunit.Fact(DisplayName = "BacklogStore_try_close_returns_true_and_closes_open_item")]
    public async Task TryCloseReturnsTrueAndClosesOpenItem()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Close me idempotently");

        var result = await store.TryCloseByIdAsync(item.Id);

        Assert.True(result);
        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null && fetched!.Status == BacklogItemStatus.Done);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_try_close_returns_false_for_already_done_item")]
    public async Task TryCloseReturnsFalseForAlreadyDoneItem()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Already done");
        await store.CloseAsync(item.Id);

        var result = await store.TryCloseByIdAsync(item.Id);

        Assert.False(result);
        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null && fetched!.Status == BacklogItemStatus.Done);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_try_close_returns_false_for_absent_item")]
    public async Task TryCloseReturnsFalseForAbsentItem()
    {
        var store = new BacklogStore(TempDb());

        var result = await store.TryCloseByIdAsync("nonexistent-id");

        Assert.False(result);
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
