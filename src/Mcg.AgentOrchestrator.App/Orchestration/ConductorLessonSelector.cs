using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorLessonSelection(
    IReadOnlyList<OperatorLesson> Lessons, IReadOnlyList<string> Entries, int Omitted)
{
    internal static ConductorLessonSelection Empty { get; } = new([], [], 0);
}

internal sealed class ConductorLessonSelector(string storePath, Action<string>? note = null)
{
    internal const int MaxLessons = 5;
    internal const int MaxCharacters = 1500;

    internal ConductorLessonSelection Select(IReadOnlyCollection<string> requestedTags)
    {
        try
        {
            if (!File.Exists(storePath))
            {
                Note("operator lessons store absent");
                return ConductorLessonSelection.Empty;
            }

            var tags = requestedTags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tags.Count == 0) return ConductorLessonSelection.Empty;
            var stored = new SqliteOperatorLessonStore(storePath).List(includeRetired: true);
            if (stored.Any(lesson => !Valid(lesson)))
                Note("operator lessons malformed record skipped");
            var matches = stored
                .Where(lesson => lesson.RetiredAt is null && lesson.RetireReason is null)
                .Where(lesson => Valid(lesson) && lesson.AppliesTo.Any(tags.Contains))
                .DistinctBy(lesson => lesson.Id, StringComparer.Ordinal)
                .OrderByDescending(lesson => lesson.RecordedAt)
                .ThenByDescending(lesson => lesson.Id, StringComparer.Ordinal)
                .ToArray();

            var selected = new List<OperatorLesson>();
            var entries = new List<string>();
            var characters = 0;
            foreach (var lesson in matches)
            {
                if (selected.Count == MaxLessons) break;
                var entry = Entry(lesson);
                var separator = selected.Count == 0 ? 0 : Environment.NewLine.Length;
                var remaining = MaxCharacters - characters - separator;
                if (entry.Length > remaining)
                {
                    if (selected.Count > 0) break;
                    entry = entry[..(MaxCharacters - 1)] + "…";
                }
                selected.Add(lesson);
                entries.Add(entry);
                characters += separator + entry.Length;
            }
            return new ConductorLessonSelection(selected, entries, matches.Length - selected.Count);
        }
        catch (Exception ex)
        {
            Note($"operator lessons store unreadable: {ex.GetType().Name}");
            return ConductorLessonSelection.Empty;
        }
    }

    internal static IReadOnlyList<string> StewardTags(ConductorStewardTriggerKind kind) =>
        ["steward", $"steward:{kind.ToString().Trim().ToLowerInvariant()}"];

    internal static IReadOnlyList<string> AuthorTags(string? forkKind) =>
        string.IsNullOrWhiteSpace(forkKind)
            ? ["author"]
            : ["author", $"author:{forkKind.Trim().ToLowerInvariant()}"];

    internal static string Render(ConductorLessonSelection? selection)
    {
        if (selection is null || selection.Entries.Count == 0) return "none";
        var lines = selection.Entries.ToList();
        if (selection.Omitted > 0)
            lines.Add($"(truncated: {selection.Omitted} more matching lessons omitted)");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool Valid(OperatorLesson lesson) =>
        !string.IsNullOrWhiteSpace(lesson.Id) && lesson.Situation is not null &&
        lesson.Rule is not null && lesson.AppliesTo is not null;

    private static string Entry(OperatorLesson lesson) =>
        $"- {lesson.Id} | situation: {lesson.Situation} | rule: {lesson.Rule}";

    private void Note(string message)
    {
        try { note?.Invoke(message); }
        catch (Exception) { /* Lessons never block a judgment round, including note failures. */ }
    }
}
