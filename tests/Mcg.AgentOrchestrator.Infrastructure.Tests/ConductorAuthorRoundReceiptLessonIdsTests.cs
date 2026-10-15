using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: stores and receipts have unique temporary roots; processes are stubbed.
public sealed class ConductorAuthorRoundReceiptLessonIdsTests
{
    [Fact]
    public async Task Receipt_records_selector_ids_in_received_order()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("general", ["author"], minute: 10);
        store.Add("specific", ["author:runtime"], minute: 5);
        store.Add("both", ["author", "author:runtime"], minute: 0);
        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));
        Assert.Equal(new[] { "both", "specific", "general" }, selected.LessonIds);
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var calls = 0;
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "answer", ""));
        });

        Assert.Equal("answer", await round.DispatchAsync(
            ConductorAuthorModelRoundCatalogTests.Input() with { Lessons = selected },
            fixture.Root, CancellationToken.None));

        Assert.Equal(1, calls);
        using var receipt = fixture.Receipt();
        Assert.Equal(selected.LessonIds, receipt.RootElement.GetProperty("lessonIds")
            .EnumerateArray().Select(id => id.GetString()).ToArray());
    }

    [Fact]
    public async Task Null_selection_writes_empty_array_and_preserves_existing_fields()
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var input = ConductorAuthorModelRoundCatalogTests.Input() with { Lessons = null };
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(0, "answer", "diagnostic")), ModelFunctionCatalog.Empty);

        Assert.Equal("answer", await round.DispatchAsync(input, fixture.Root, CancellationToken.None));

        using var receipt = fixture.Receipt();
        var root = receipt.RootElement;
        Assert.Empty(root.GetProperty("lessonIds").EnumerateArray());
        Assert.Equal(input.Item.Identity, root.GetProperty("item").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("answer", root.GetProperty("stdout").GetString());
        Assert.Equal("diagnostic", root.GetProperty("stderr").GetString());
        Assert.Equal("sonnet", root.GetProperty("model").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("failure").ValueKind);
    }

    [Fact]
    public async Task Prompt_lesson_block_matches_existing_render_when_ranking_keys_tie()
    {
        using var store = new ConductorLessonTestStore();
        for (var index = 0; index < 3; index++)
            store.Add($"lesson-{index}", ["author", "author:runtime"], minute: index);
        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));
        Assert.Equal(new[] { "lesson-2", "lesson-1", "lesson-0" }, selected.LessonIds);
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(0, "answer", ""));
        });

        await round.DispatchAsync(ConductorAuthorModelRoundCatalogTests.Input() with { Lessons = selected },
            fixture.Root, CancellationToken.None);

        Assert.NotNull(request);
        Assert.NotNull(request.StandardInput);
        const string marker = "Operator lessons:";
        var start = request.StandardInput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The captured Author prompt must contain the operator lessons block.");
        Assert.Equal(ConductorLessonSelector.Render(selected),
            request.StandardInput[(start + marker.Length)..].Trim('\r', '\n'));
    }

    [Theory]
    [InlineData("invalid-model")]
    [InlineData("exception")]
    [InlineData("nonzero-exit")]
    public async Task Failure_receipts_keep_supplied_lesson_ids(string failureKind)
    {
        using var store = new ConductorLessonTestStore();
        store.Add("lesson", ["author:runtime"]);
        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));
        Assert.Equal("lesson", Assert.Single(selected.LessonIds));
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var calls = 0;
        var round = new ClaudeConductorAuthorModelRound(fixture.Receipts, (_, _) =>
        {
            calls++;
            return failureKind == "exception"
                ? Task.FromException<WorkerProcessRunResult>(new InvalidOperationException("process failed"))
                : Task.FromResult(new WorkerProcessRunResult(7, "partial", "process error"));
        }, failureKind == "invalid-model"
            ? ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, "empty")
            : ModelFunctionCatalog.Empty);
        var input = ConductorAuthorModelRoundCatalogTests.Input() with { Lessons = selected };

        if (failureKind == "exception")
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                round.DispatchAsync(input, fixture.Root, CancellationToken.None));
        else
            await Assert.ThrowsAsync<ConductorModelRoundException>(() =>
                round.DispatchAsync(input, fixture.Root, CancellationToken.None));

        Assert.Equal(failureKind == "invalid-model" ? 0 : 1, calls);
        using var receipt = fixture.Receipt();
        Assert.Equal(selected.LessonIds, receipt.RootElement.GetProperty("lessonIds")
            .EnumerateArray().Select(id => id.GetString()).ToArray());
        if (failureKind == "nonzero-exit")
            Assert.Equal(7, receipt.RootElement.GetProperty("exitCode").GetInt32());
        else
            Assert.NotNull(receipt.RootElement.GetProperty("failure").GetString());
    }
}
