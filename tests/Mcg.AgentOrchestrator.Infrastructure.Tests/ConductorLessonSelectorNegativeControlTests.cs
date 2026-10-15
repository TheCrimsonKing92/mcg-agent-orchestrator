using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns a unique temporary lesson store.
public sealed class ConductorLessonSelectorNegativeControlTests
{
    [Fact]
    public void Newest_unrelated_lesson_is_excluded_even_with_spare_slots()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("match", ["author"], minute: 0);
        store.Add("unrelated", ["steward:runtime"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal("match", Assert.Single(selected.Lessons).Id);
        Assert.Equal(0, selected.Omitted);
    }

    [Fact]
    public void Retired_specific_match_consumes_neither_slot_nor_omitted_count()
    {
        using var store = new ConductorLessonTestStore();
        for (var index = 0; index < 4; index++)
            store.Add($"active-{index}", ["author"], minute: index);
        store.Add("retired", ["author:runtime"], minute: 10, retire: true);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(new[] { "active-3", "active-2", "active-1", "active-0" }, selected.LessonIds);
        Assert.Equal(0, selected.Omitted);
    }

    [Fact]
    public void Missing_specific_tag_still_returns_bare_matches()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("older", ["author"], minute: 0);
        store.Add("newer", ["author"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(new[] { "newer", "older" }, selected.LessonIds);
        Assert.Equal(0, selected.Omitted);
    }

    [Fact]
    public void Unrequested_colon_tag_does_not_promote_a_bare_match()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("older-unrequested-specific", ["author", "steward:runtime"], minute: 0);
        store.Add("newer-general", ["author"], minute: 10);

        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags("runtime"));

        Assert.Equal(new[] { "newer-general", "older-unrequested-specific" }, selected.LessonIds);
    }

    [Fact]
    public void Empty_selection_exposes_empty_computed_ids()
    {
        Assert.Empty(ConductorLessonSelection.Empty.LessonIds);
    }
}
