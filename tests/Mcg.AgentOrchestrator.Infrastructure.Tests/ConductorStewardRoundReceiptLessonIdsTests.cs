using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: stores and receipts have unique temporary roots; processes and file discovery are stubbed.
public sealed class ConductorStewardRoundReceiptLessonIdsTests : IConductorStewardTrackedFileLister
{
    public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];

    [Fact]
    public async Task Receipt_records_the_round_selection_ids_in_order()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("general", ["steward"], minute: 10);
        store.Add("specific", ["steward:planneroutputcontractrejected"], minute: 5);
        store.Add("both", ["steward", "steward:planneroutputcontractrejected"], minute: 0);
        var trigger = Trigger();
        var selector = new ConductorLessonSelector(store.Path);
        var selected = selector.Select(ConductorLessonSelector.StewardTags(trigger.Kind));
        Assert.Equal(new[] { "both", "specific", "general" }, selected.LessonIds);
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var calls = 0;
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorStewardModelRound(fixture.Receipts, (value, _) =>
        {
            calls++;
            request = value;
            // Receipt ids must come from the selection already used to build this prompt.
            File.WriteAllText(store.Path, "not a SQLite database");
            return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
        }, this, (_, _) => false, lessons: selector);

        Assert.Equal("adjudication", await round.DispatchAsync(trigger, fixture.Root, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.NotNull(request);
        Assert.Contains("## Operator lessons" + Environment.NewLine + ConductorLessonSelector.Render(selected),
            request.StandardInput);
        using var receipt = fixture.Receipt();
        Assert.Equal(selected.LessonIds, receipt.RootElement.GetProperty("lessonIds")
            .EnumerateArray().Select(id => id.GetString()).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unreadable_store_writes_empty_array(bool unreadable)
    {
        using var store = new ConductorLessonTestStore();
        if (unreadable) File.WriteAllText(store.Path, "not a SQLite database");
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var round = new ClaudeConductorStewardModelRound(fixture.Receipts, (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(0, "adjudication", "")),
            this, (_, _) => false, lessons: new ConductorLessonSelector(store.Path));

        await round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None);

        using var receipt = fixture.Receipt();
        Assert.Empty(receipt.RootElement.GetProperty("lessonIds").EnumerateArray());
    }

    [Fact]
    public async Task Absent_selector_writes_empty_array()
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var round = new ClaudeConductorStewardModelRound(fixture.Receipts, (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(0, "adjudication", "")), this, (_, _) => false);

        await round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None);

        using var receipt = fixture.Receipt();
        Assert.Empty(receipt.RootElement.GetProperty("lessonIds").EnumerateArray());
    }

    [Theory]
    [InlineData("invalid-model")]
    [InlineData("exception")]
    [InlineData("nonzero-exit")]
    public async Task Failure_receipts_record_ids_only_after_selection(string failureKind)
    {
        using var store = new ConductorLessonTestStore();
        store.Add("lesson", ["steward:planneroutputcontractrejected"]);
        var notes = new List<string>();
        var selector = new ConductorLessonSelector(store.Path, notes.Add);
        var selected = selector.Select(ConductorLessonSelector.StewardTags(Trigger().Kind));
        Assert.Equal("lesson", Assert.Single(selected.LessonIds));
        // Invalid model must neither select nor read the store.
        if (failureKind == "invalid-model") File.WriteAllText(store.Path, "not a SQLite database");
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var calls = 0;
        var round = new ClaudeConductorStewardModelRound(fixture.Receipts, (_, _) =>
        {
            calls++;
            return failureKind == "exception"
                ? Task.FromException<WorkerProcessRunResult>(new InvalidOperationException("process failed"))
                : Task.FromResult(new WorkerProcessRunResult(7, "partial", "process error"));
        }, this, (_, _) => false, lessons: selector, catalog: failureKind == "invalid-model"
            ? ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorSteward, "empty")
            : ModelFunctionCatalog.Empty);

        if (failureKind == "exception")
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None));
        else
            await Assert.ThrowsAsync<ConductorModelRoundException>(() =>
                round.DispatchAsync(Trigger(), fixture.Root, CancellationToken.None));

        Assert.Equal(failureKind == "invalid-model" ? 0 : 1, calls);
        Assert.Empty(notes);
        using var receipt = fixture.Receipt();
        var ids = receipt.RootElement.GetProperty("lessonIds").EnumerateArray()
            .Select(id => id.GetString()).ToArray();
        if (failureKind == "invalid-model") Assert.Empty(ids);
        else Assert.Equal(selected.LessonIds, ids);
        if (failureKind == "nonzero-exit")
            Assert.Equal(7, receipt.RootElement.GetProperty("exitCode").GetInt32());
        else
            Assert.NotNull(receipt.RootElement.GetProperty("failure").GetString());
    }

    private static ConductorStewardTrigger Trigger() => new("goal", "task", "candidate",
        ConductorStewardTriggerKind.PlannerOutputContractRejected,
        new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), "", "", [], []);
}
