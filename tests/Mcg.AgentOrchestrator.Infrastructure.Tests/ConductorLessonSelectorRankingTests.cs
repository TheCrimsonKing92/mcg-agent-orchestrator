using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns a unique temporary lesson store.
public sealed class ConductorLessonSelectorRankingTests
{
    [Fact]
    public void Older_requested_specific_match_precedes_newer_bare_author_match()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("specific", ["author:runtime"], minute: 0);
        store.Add("general", ["author"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(new[] { "specific", "general" }, selected.LessonIds);
    }

    [Fact]
    public void Older_two_tag_match_precedes_newer_single_specific_match()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("both", ["author", "author:runtime"], minute: 0);
        store.Add("specific-only", ["author:runtime"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(new[] { "both", "specific-only" }, selected.LessonIds);
    }

    [Fact]
    public void Equal_specificity_and_match_counts_use_recency_then_descending_ordinal_id()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("z-older", ["author", "author:runtime"], minute: 0);
        store.Add("a-newer", ["author", "author:runtime"], minute: 10);
        store.Add("z-newer", ["author", "author:runtime"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(new[] { "z-newer", "a-newer", "z-older" }, selected.LessonIds);
    }

    [Fact]
    public void Older_requested_specific_match_precedes_newer_bare_steward_match()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("specific", ["steward:planneroutputcontractrejected"], minute: 0);
        store.Add("general", ["steward"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path).Select(
            ConductorLessonSelector.StewardTags(ConductorStewardTriggerKind.PlannerOutputContractRejected));

        Assert.Equal(new[] { "specific", "general" }, selected.LessonIds);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_counts_distinct_requested_tags()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("both", ["AUTHOR", "AUTHOR:RUNTIME"], minute: 0);
        store.Add("specific-only", ["author:runtime", "AUTHOR:RUNTIME"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(["author", "author:runtime", "AUTHOR:RUNTIME"]);

        Assert.Equal(new[] { "both", "specific-only" }, selected.LessonIds);
    }

    [Fact]
    public void Ranking_precedes_count_cap_and_preserves_omission_suffix()
    {
        using var store = new ConductorLessonTestStore();
        for (var index = 0; index < 3; index++)
            store.Add($"specific-{index}", ["author:runtime"], minute: index);
        for (var index = 0; index < 4; index++)
            store.Add($"general-{index}", ["author"], minute: index + 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(5, selected.Lessons.Count);
        Assert.Equal(new[] { "specific-2", "specific-1", "specific-0", "general-3", "general-2" },
            selected.LessonIds);
        Assert.Equal(2, selected.Omitted);
        Assert.EndsWith("(truncated: 2 more matching lessons omitted)", ConductorLessonSelector.Render(selected));
    }

    [Fact]
    public void Oversize_top_ranked_entry_preserves_character_cap_and_truncation()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("specific", ["author:runtime"], minute: 0, rule: new string('x', 2000));
        store.Add("general", ["author"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal("specific", Assert.Single(selected.Lessons).Id);
        Assert.Equal(ConductorLessonSelector.MaxCharacters, Assert.Single(selected.Entries).Length);
        Assert.EndsWith("…", selected.Entries[0]);
        Assert.Equal(1, selected.Omitted);
    }
}
