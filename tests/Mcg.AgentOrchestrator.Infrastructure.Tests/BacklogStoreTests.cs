using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns a unique database directory and disables connection pooling.
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

    [Xunit.Fact(DisplayName = "BacklogStore_update_title_leaves_other_fields_unchanged")]
    public async Task UpdateTitleLeavesOtherFieldsUnchanged()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Old title", "Original body");

        var updated = await store.UpdateAsync(item.Id, new BacklogItemUpdate(Title: "New title"));

        Assert.Equal("New title", updated.Title);
        Assert.Equal("Original body", updated.Body);
        Assert.Equal(BacklogItemStatus.Open, updated.Status);
        Assert.Equal(item.CreatedAt, updated.CreatedAt);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_update_description_and_priority_atomically")]
    public async Task UpdateDescriptionAndPriorityAtomically()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Atomic update", "Old body");

        var updated = await store.UpdateAsync(item.Id, new BacklogItemUpdate(Body: "New body", Priority: "high"));

        Assert.Equal("Atomic update", updated.Title);
        Assert.Equal("New body", updated.Body);
        Assert.Equal("high", updated.Priority);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_update_unknown_id_fails")]
    public async Task UpdateUnknownIdFails()
    {
        var store = new BacklogStore(TempDb());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync("missing", new BacklogItemUpdate(Title: "Nope")));

        Assert.Contains("No backlog item found", ex.Message);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_supersede_hides_old_item_by_default_and_all_keeps_link")]
    public async Task SupersedeHidesOldItemByDefaultAndAllKeepsLink()
    {
        var store = new BacklogStore(TempDb());
        var oldItem = await store.AddAsync("Old truth");
        var replacement = await store.AddAsync("Current truth");

        var superseded = await store.SupersedeAsync(oldItem.Id, replacement.Id);
        var defaultList = await store.ListAsync();
        var all = await store.ListAsync(includeAll: true);

        Assert.Equal(BacklogItemStatus.Superseded, superseded.Status);
        Assert.Equal(replacement.Id, superseded.SupersededBy);
        Assert.DoesNotContain(defaultList, item => item.Id == oldItem.Id);
        Assert.Contains(defaultList, item => item.Id == replacement.Id);
        Assert.Equal(replacement.Id, all.Single(item => item.Id == oldItem.Id).SupersededBy);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_supersede_validates_ids_and_self_reference")]
    public async Task SupersedeValidatesIdsAndSelfReference()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Known item");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SupersedeAsync("missing", item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SupersedeAsync(item.Id, "missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SupersedeAsync(item.Id, item.Id));
    }

    [Xunit.Fact(DisplayName = "BacklogStore_supersede_rejects_cycles_and_unsupersede_clears_metadata")]
    public async Task SupersedeRejectsCyclesAndUnsupersedeClearsMetadata()
    {
        var store = new BacklogStore(TempDb());
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");

        await store.SupersedeAsync(first.Id, second.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SupersedeAsync(second.Id, first.Id));
        var result = await store.UnsupersedeAsync(first.Id);
        var noop = await store.UnsupersedeAsync(second.Id);

        Assert.True(result.Changed);
        Assert.Equal(BacklogItemStatus.Open, result.Item.Status);
        Assert.Null(result.Item.SupersededBy);
        Assert.False(noop.Changed);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_link_duplicate_is_idempotent_bidirectional_and_hides_duplicate")]
    public async Task LinkDuplicateIsIdempotentBidirectionalAndHidesDuplicate()
    {
        var store = new BacklogStore(TempDb());
        var canonical = await store.AddAsync("Canonical");
        var duplicate = await store.AddAsync("Duplicate");

        await store.LinkAsync(canonical.Id, duplicate.Id);
        await store.LinkAsync(canonical.Id, duplicate.Id);
        var defaultList = await store.ListAsync();
        var canonicalReloaded = await store.GetByExactIdAsync(canonical.Id);
        var duplicateReloaded = await store.GetByExactIdAsync(duplicate.Id);

        Assert.Contains(defaultList, item => item.Id == canonical.Id);
        Assert.DoesNotContain(defaultList, item => item.Id == duplicate.Id);
        Assert.Single(canonicalReloaded!.Links);
        Assert.Single(duplicateReloaded!.Links);
        Assert.Equal(canonical.Id, duplicateReloaded.Links.Single().CanonicalId);
        Assert.Equal(duplicate.Id, canonicalReloaded.Links.Single().DuplicateId);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_link_related_keeps_both_items_visible")]
    public async Task LinkRelatedKeepsBothItemsVisible()
    {
        var store = new BacklogStore(TempDb());
        var first = await store.AddAsync("First related");
        var second = await store.AddAsync("Second related");

        await store.LinkAsync(first.Id, second.Id, BacklogLinkKind.Related);
        var defaultList = await store.ListAsync();

        Assert.Contains(defaultList, item => item.Id == first.Id);
        Assert.Contains(defaultList, item => item.Id == second.Id);
        Assert.Equal(BacklogLinkKind.Related, defaultList.Single(item => item.Id == first.Id).Links.Single().Kind);
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

    [Xunit.Fact(DisplayName = "BacklogStore_record_open_item_landing_is_atomic_idempotent_and_status_aware")]
    public async Task RecordOpenItemLandingIsAtomicIdempotentAndStatusAware()
    {
        var store = new BacklogStore(TempDb());
        var item = await store.AddAsync("Record a slice landing");

        var first = await store.TryRecordOpenItemLandingAsync(item.Id, "goal=abc commit=def");
        var repeated = await store.TryRecordOpenItemLandingAsync(item.Id, "goal=abc commit=def");

        Assert.Equal(BacklogLandingDisposition.Recorded, first.Disposition);
        Assert.Equal(BacklogLandingDisposition.AlreadyRecorded, repeated.Disposition);
        var openItem = await store.GetByExactIdAsync(item.Id);
        Assert.Equal(BacklogItemStatus.Open, openItem!.Status);
        Assert.Single(openItem.Notes);

        await store.CloseAsync(item.Id);
        var alreadyDone = await store.TryRecordOpenItemLandingAsync(item.Id, "goal=other commit=ghi");
        var missing = await store.TryRecordOpenItemLandingAsync("missing", "goal=other commit=ghi");

        Assert.Equal(BacklogLandingDisposition.AlreadyDone, alreadyDone.Disposition);
        Assert.Equal(BacklogLandingDisposition.NotFound, missing.Disposition);
        Assert.Single((await store.GetByExactIdAsync(item.Id))!.Notes);
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

    [Xunit.Fact(DisplayName = "BacklogStore_dependencies_roundtrip_in_order_and_expose_dependents")]
    public async Task DependenciesRoundtripInOrderAndExposeDependents()
    {
        var path = TempDb();
        var store = new BacklogStore(path);
        var first = await store.AddAsync("First prerequisite");
        var second = await store.AddAsync("Second prerequisite");
        var dependent = await store.AddWithDependenciesAsync(
            "Dependent",
            "",
            [
                new(first.Id, BacklogDependencyTargetKind.Backlog),
                new(second.Id, BacklogDependencyTargetKind.Backlog),
                new(first.Id, BacklogDependencyTargetKind.Backlog)
            ]);

        var reloaded = new BacklogStore(path);
        var found = await reloaded.GetByExactIdAsync(dependent.Id);
        var prerequisite = await reloaded.GetByExactIdAsync(first.Id);

        Assert.Equal([first.Id, second.Id], found!.Dependencies.Select(edge => edge.PrerequisiteId));
        Assert.Contains(prerequisite!.Dependents, edge => edge.DependentId == dependent.Id);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_legacy_schema_without_dependencies_upgrades_with_empty_relationships")]
    public async Task LegacySchemaWithoutDependenciesUpgradesWithEmptyRelationships()
    {
        var path = TempDb();
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE backlog (
                    id             TEXT PRIMARY KEY,
                    title          TEXT NOT NULL,
                    body           TEXT NOT NULL,
                    status         TEXT NOT NULL,
                    created_at     TEXT NOT NULL,
                    updated_at     TEXT NOT NULL,
                    source_goal_id TEXT
                );
                INSERT INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id)
                VALUES ('legacy-item', 'Legacy item', '', 'Open',
                        '2026-07-30T00:00:00Z', '2026-07-30T00:00:00Z', NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = new BacklogStore(path);
        var item = await upgraded.GetByExactIdAsync("legacy-item");

        Assert.NotNull(item);
        Assert.Empty(item.Dependencies);
        Assert.Empty(item.Dependents);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_dependency_add_remove_clear_are_idempotent_or_loud")]
    public async Task DependencyAddRemoveClearAreIdempotentOrLoud()
    {
        var store = new BacklogStore(TempDb());
        var first = await store.AddAsync("First");
        var second = await store.AddAsync("Second");
        var dependent = await store.AddAsync("Dependent");
        var firstTarget = new BacklogDependencyTarget(first.Id, BacklogDependencyTargetKind.Backlog);

        await store.AddDependencyAsync(dependent.Id, firstTarget);
        await store.AddDependencyAsync(dependent.Id, firstTarget);
        await store.AddDependencyAsync(
            dependent.Id,
            new BacklogDependencyTarget(second.Id, BacklogDependencyTargetKind.Backlog));
        Assert.Equal(2, (await store.GetByExactIdAsync(dependent.Id))!.Dependencies.Count);

        await store.RemoveDependencyAsync(dependent.Id, first.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RemoveDependencyAsync(dependent.Id, first.Id));
        await store.ClearDependenciesAsync(dependent.Id);
        Assert.Empty((await store.GetByExactIdAsync(dependent.Id))!.Dependencies);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ClearDependenciesAsync(dependent.Id));
    }

    [Xunit.Fact(DisplayName = "BacklogStore_dependency_rejects_self_and_transitive_cycles_without_mutation")]
    public async Task DependencyRejectsSelfAndTransitiveCyclesWithoutMutation()
    {
        var store = new BacklogStore(TempDb());
        var a = await store.AddAsync("A");
        var b = await store.AddAsync("B");
        var c = await store.AddAsync("C");

        var self = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddDependencyAsync(a.Id, new(a.Id, BacklogDependencyTargetKind.Backlog)));
        Assert.Contains("itself", self.Message, StringComparison.OrdinalIgnoreCase);

        await store.AddDependencyAsync(a.Id, new(b.Id, BacklogDependencyTargetKind.Backlog));
        await store.AddDependencyAsync(b.Id, new(c.Id, BacklogDependencyTargetKind.Backlog));
        var cycle = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddDependencyAsync(c.Id, new(a.Id, BacklogDependencyTargetKind.Backlog)));

        Assert.Contains("cycle", cycle.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(a.Id[..8], cycle.Message, StringComparison.Ordinal);
        Assert.Empty((await store.GetByExactIdAsync(c.Id))!.Dependencies);
    }

    [Xunit.Fact(DisplayName = "BacklogStore_goal_dependency_requires_resolved_goal_identity")]
    public async Task GoalDependencyRequiresResolvedGoalIdentity()
    {
        var store = new BacklogStore(TempDb());
        var dependent = await store.AddAsync("Dependent");
        var goalId = Guid.NewGuid().ToString("n");
        var target = new BacklogDependencyTarget(goalId, BacklogDependencyTargetKind.Goal);

        var unresolved = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddDependencyAsync(dependent.Id, target));
        Assert.Contains(goalId, unresolved.Message, StringComparison.Ordinal);
        Assert.Empty((await store.GetByExactIdAsync(dependent.Id))!.Dependencies);

        await store.AddDependencyAsync(
            dependent.Id,
            target,
            goalExists: candidate => candidate == goalId);
        Assert.Equal(
            goalId,
            Assert.Single((await store.GetByExactIdAsync(dependent.Id))!.Dependencies).PrerequisiteId);
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

    [Xunit.Fact]
    public void Setup_MissingPath_CreatesDatabaseAndRecordsVersion1()
    {
        var path = Path.Combine(TempDb(), "nested", "backlog.db");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        BacklogStore.Setup(path);

        Assert.True(File.Exists(path));
        using var connection = OpenSetupDatabase(path);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "backlog"));
    }

    [Xunit.Fact]
    public void Setup_SecondRun_PreservesFileSchemaAndVersionRows()
    {
        var path = TempDb();
        BacklogStore.Setup(path);
        using (var connection = OpenSetupDatabase(path))
            RunSetupSql(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
        var schema = ReadSetupRows(path, "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name");
        var versions = ReadSetupRows(path, "SELECT * FROM store_schema_versions ORDER BY store_name");
        var bytes = File.ReadAllBytes(path);

        BacklogStore.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, ReadSetupRows(path, "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name"));
        Assert.Equal(versions, ReadSetupRows(path, "SELECT * FROM store_schema_versions ORDER BY store_name"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task WritableConstructor_CurrentOrNewerSchema_OpensWhileItemLeaseIsHeld(int version)
    {
        var path = TempDb();
        var store = new BacklogStore(path);
        var item = await store.AddAsync("Replacement source");
        using (var connection = OpenSetupDatabase(path))
            RunSetupSql(connection, $"UPDATE store_schema_versions SET version = {version}, applied_at = 'original' WHERE store_name = 'backlog'");
        var versions = ReadSetupRows(path, "SELECT * FROM store_schema_versions ORDER BY store_name");

        using var lease = store.TryAcquireOpenItemLease(item.Id, out var observedStatus);
        Assert.NotNull(lease);
        Assert.Equal(BacklogItemStatus.Open, observedStatus);

        // The lease stays held across construction, as it does during goal-replace validation.
        var reopened = new BacklogStore(path);
        var fetched = await reopened.GetByExactIdAsync(item.Id);

        Assert.NotNull(fetched);
        Assert.Equal(item.Id, fetched.Id);
        Assert.Equal(BacklogItemStatus.Open, fetched.Status);
        Assert.Equal(versions, ReadSetupRows(path, "SELECT * FROM store_schema_versions ORDER BY store_name"));
    }

    [Xunit.Fact]
    public async Task Setup_LegacyConstructorDatabase_PreservesRowsAndAddsVersion()
    {
        var path = TempDb();
        SetupLegacyBacklog(path);
        using (var connection = OpenSetupDatabase(path))
        {
            Assert.Null(StoreSchemaVersions.Read(connection, "backlog"));
            RunSetupSql(connection, """
                INSERT INTO backlog (id, title, body, status, created_at, updated_at)
                VALUES ('legacy', 'Existing item', 'Original body', 'Open',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                """);
        }
        var schema = ReadSetupRows(path, "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name");

        BacklogStore.Setup(path);

        using var current = OpenSetupDatabase(path);
        Assert.Equal(1, StoreSchemaVersions.Read(current, "backlog"));
        Assert.Equal(schema, ReadSetupRows(path,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name <> 'store_schema_versions' ORDER BY type, name"));
        var item = Assert.Single(await BacklogStore.OpenReadOnly(path).ListAsync());
        Assert.Equal("legacy", item.Id);
        Assert.Equal("Existing item", item.Title);
        Assert.Equal("Original body", item.Body);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-file", StoreSchemaState.Missing)]
    [Xunit.InlineData("unversioned", StoreSchemaState.Missing)]
    [Xunit.InlineData("missing-record", StoreSchemaState.Missing)]
    [Xunit.InlineData("older", StoreSchemaState.Older)]
    [Xunit.InlineData("newer", StoreSchemaState.Newer)]
    public void OpenReadOnly_IncompatibleDatabase_RequiresSetupWithoutWriting(
        string scenario, StoreSchemaState state)
    {
        var path = scenario == "missing-file" ? Path.Combine(TempDb(), "absent", "backlog.db") : TempDb();
        if (scenario == "unversioned")
            SetupLegacyBacklog(path);
        else if (scenario != "missing-file")
        {
            BacklogStore.Setup(path);
            using var connection = OpenSetupDatabase(path);
            RunSetupSql(connection, scenario == "missing-record"
                ? "DELETE FROM store_schema_versions WHERE store_name = 'backlog'"
                : $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)} WHERE store_name = 'backlog'");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => BacklogStore.OpenReadOnly(path));

        Assert.Equal($"Backlog store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing-file")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Xunit.Fact]
    public async Task OpenReadOnly_CurrentDatabase_PreservesItemsFileAndVersionRows()
    {
        var path = TempDb();
        var writable = new BacklogStore(path);
        var prerequisite = await writable.AddAsync("Prerequisite", "Body", "source-goal");
        var dependent = await writable.AddAsync("Dependent");
        await writable.AppendNoteAsync(prerequisite.Id, "Receipt");
        await writable.LinkAsync(prerequisite.Id, dependent.Id, BacklogLinkKind.Related);
        await writable.AddDependencyAsync(dependent.Id, new(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        await writable.CloseAsync(dependent.Id);
        var open = await writable.ListAsync();
        var all = await writable.ListAsync(includeAll: true);
        Assert.Single(open);
        Assert.Equal(2, all.Count);
        var expected = (await writable.GetByExactIdAsync(prerequisite.Id))!;
        Assert.Single(expected.Notes);
        Assert.Single(expected.Links);
        Assert.Single(expected.Dependents);
        var versions = ReadSetupRows(path, "SELECT * FROM store_schema_versions ORDER BY store_name");
        var bytes = File.ReadAllBytes(path);

        var readOnly = BacklogStore.OpenReadOnly(path);
        Assert.Equal(open.Select(ItemSnapshot), (await readOnly.ListAsync()).Select(ItemSnapshot));
        Assert.Equal(all.Select(ItemSnapshot), (await readOnly.ListAsync(includeAll: true)).Select(ItemSnapshot));
        Assert.Equal(ItemSnapshot(expected), ItemSnapshot((await readOnly.GetByIdPrefixAsync(prerequisite.Id[..8]))!));
        Assert.Equal(ItemSnapshot(expected), ItemSnapshot((await readOnly.GetByExactIdAsync(prerequisite.Id))!));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(versions, ReadSetupRows(path, "SELECT * FROM store_schema_versions ORDER BY store_name"));
    }

    private static string ItemSnapshot(BacklogItem item) => System.Text.Json.JsonSerializer.Serialize(item);

    private static SqliteConnection OpenSetupDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string[] ReadSetupRows(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(System.Text.Json.JsonSerializer.Serialize(values));
        }
        return rows.ToArray();
    }

    private static void RunSetupSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Frozen constructor schema from ec2374d4e2dcbf7e0dc65d54b5c7e8a68627210f.
    private static void SetupLegacyBacklog(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        RunSetupSql(conn, "PRAGMA journal_mode=WAL");
        RunSetupSql(conn, "PRAGMA busy_timeout=30000");
        RunSetupSql(conn, """
            CREATE TABLE IF NOT EXISTS backlog (
                id             TEXT PRIMARY KEY,
                title          TEXT NOT NULL,
                body           TEXT NOT NULL,
                status         TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                updated_at     TEXT NOT NULL,
                source_goal_id TEXT
            )
            """);
        AddLegacyColumnIfMissing(conn, "backlog", "priority", "TEXT");
        AddLegacyColumnIfMissing(conn, "backlog", "tags", "TEXT NOT NULL DEFAULT ''");
        AddLegacyColumnIfMissing(conn, "backlog", "superseded_by", "TEXT");
        AddLegacyColumnIfMissing(conn, "backlog", "superseded_at", "TEXT");
        RunSetupSql(conn, """
            CREATE TABLE IF NOT EXISTS backlog_notes (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                backlog_item_id TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                text            TEXT NOT NULL,
                FOREIGN KEY(backlog_item_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunSetupSql(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_notes_item_created ON backlog_notes(backlog_item_id, created_at, id)");
        RunSetupSql(conn, """
            CREATE TABLE IF NOT EXISTS backlog_links (
                item1_id     TEXT NOT NULL,
                item2_id     TEXT NOT NULL,
                kind         TEXT NOT NULL,
                canonical_id TEXT,
                created_at   TEXT NOT NULL,
                PRIMARY KEY(item1_id, item2_id),
                FOREIGN KEY(item1_id) REFERENCES backlog(id) ON DELETE CASCADE,
                FOREIGN KEY(item2_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunSetupSql(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_links_item2 ON backlog_links(item2_id)");
        RunSetupSql(conn, """
            CREATE TABLE IF NOT EXISTS backlog_dependencies (
                sequence          INTEGER PRIMARY KEY AUTOINCREMENT,
                dependent_id      TEXT NOT NULL,
                prerequisite_id   TEXT NOT NULL,
                prerequisite_kind TEXT NOT NULL,
                created_at        TEXT NOT NULL,
                UNIQUE(dependent_id, prerequisite_id),
                FOREIGN KEY(dependent_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunSetupSql(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_dependencies_prerequisite ON backlog_dependencies(prerequisite_id, prerequisite_kind)");
        RunSetupSql(conn, """
            CREATE TABLE IF NOT EXISTS backlog_history (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                backlog_item_id TEXT NOT NULL,
                action          TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                details         TEXT NOT NULL,
                FOREIGN KEY(backlog_item_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunSetupSql(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_history_item_created ON backlog_history(backlog_item_id, created_at, id)");
    }

    private static void AddLegacyColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table})";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        RunSetupSql(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static string TempDb() => Path.Combine(CreateTempDirectory(), "backlog.db");
}
