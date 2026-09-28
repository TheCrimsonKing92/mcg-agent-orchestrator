using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorLessonSelectorTests
{
    [Xunit.Fact]
    public void Selects_newest_matching_active_lessons_once_and_caps_count()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("retired", ["author"], retire: true);
        store.Add("other", ["steward"]);
        for (var index = 0; index < 7; index++)
            store.Add($"matching-{index}", ["author", "author:runtime"], minute: index);

        var result = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.AuthorTags(" Runtime "));

        Xunit.Assert.Equal(5, result.Lessons.Count);
        Xunit.Assert.Equal(2, result.Omitted);
        Xunit.Assert.Equal("matching-6", result.Lessons[0].Id);
        Xunit.Assert.DoesNotContain(result.Lessons, lesson => lesson.Id is "retired" or "other");
        Xunit.Assert.Contains("(truncated: 2 more matching lessons omitted)",
            ConductorLessonSelector.Render(result));
    }

    [Xunit.Fact]
    public void Oversize_newest_lesson_is_kept_with_bounded_entry()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("older", ["steward"]);
        store.Add("newest", ["steward"], minute: 1, rule: new string('x', 2000));

        var result = new ConductorLessonSelector(store.Path).Select(["steward"]);

        Xunit.Assert.Equal("newest", Xunit.Assert.Single(result.Lessons).Id);
        Xunit.Assert.Equal(ConductorLessonSelector.MaxCharacters, result.Entries[0].Length);
        Xunit.Assert.EndsWith("…", result.Entries[0]);
        Xunit.Assert.Equal(1, result.Omitted);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Missing_or_unreadable_store_returns_empty_and_one_note(bool unreadable)
    {
        using var store = new ConductorLessonTestStore();
        if (unreadable) File.WriteAllText(store.Path, "not a SQLite database");
        var notes = new List<string>();

        var result = new ConductorLessonSelector(store.Path, notes.Add).Select(["author"]);

        Xunit.Assert.Empty(result.Lessons);
        Xunit.Assert.Equal("none", ConductorLessonSelector.Render(result));
        Xunit.Assert.Single(notes);
    }
}

internal sealed class ConductorLessonTestStore : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "conductor-lesson-tests-" + Guid.NewGuid().ToString("N"));
    internal string Path => System.IO.Path.Combine(_directory, SqliteOperatorLessonStore.DatabaseFileName);

    internal ConductorLessonTestStore() => Directory.CreateDirectory(_directory);

    internal void Add(string id, IReadOnlyList<string> tags, int minute = 0,
        string situation = "When the case appears", string rule = "Inspect the receipt", bool retire = false)
    {
        var store = new SqliteOperatorLessonStore(Path);
        var recorded = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute);
        Xunit.Assert.True(store.TryAppendLesson(new OperatorLesson(id, situation, rule, tags, [],
            "operator", OperatorActorKind.Human, "cli", recorded, null, null, null, null, null),
            "source-" + id));
        if (retire)
            Xunit.Assert.Equal(OperatorLessonRetireResult.Retired,
                store.TryAppendRetirement(id, "retire-" + id, "obsolete", [], "operator",
                    OperatorActorKind.Human, "cli", recorded.AddSeconds(1)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
