using System.Globalization;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleSelectedPreview
{
    internal static string Title(string? goalId) => "SELECTED" +
        (goalId is null ? "" : " " + goalId[..Math.Min(8, goalId.Length)]);

    // Width is the panel's interior width, after the frame has taken its border cells.
    internal static IReadOnlyList<string> Lines(OwnerConsoleSelectedGoalPreview preview, string? stage,
        TimeZoneInfo zone, int width)
    {
        if (preview.GoalId is null) return [Fit("Select a goal", width)];
        if (preview.Unavailable is { } error)
            return [Fit("Details unavailable: " + (error == "goal unavailable" ? "the goal was not found" : "could not load the goal"), width)];
        if (preview.Summary is null) return [Fit("Loading…", width)];
        if (preview.Summary.Preview is not { } facts)
            return [Fit("Details unavailable: could not load the goal", width)];

        var lines = new List<string> { Fit(facts.Title, width), Changes(facts.Changes, width) };
        var evidence = Evidence(facts.Evidence, zone);
        var progress = string.IsNullOrWhiteSpace(stage) ? evidence :
            evidence is null ? stage : Prefer(stage, " · " + evidence, width);
        if (!string.IsNullOrWhiteSpace(progress)) lines.Add(Fit(progress, width));
        return lines;
    }

    private static string Changes(OwnerGoalPreviewChanges changes, int width)
    {
        if (changes.Kind == OwnerGoalPreviewChangeKind.Unavailable)
            return Fit("Change list unavailable: could not read the commits", width);
        if (changes.Kind == OwnerGoalPreviewChangeKind.None || changes.Total == 0)
            return Fit("No committed changes yet", width);

        var paths = changes.Files.Select(PathWithoutStatus).ToArray();
        var areas = paths.Select(path => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : ".")
            .Distinct(StringComparer.Ordinal).ToArray();
        var area = areas.Length == 1 ? areas[0] : $"{areas.Length} areas";
        var count = $"Changes {changes.Total} {(changes.Total == 1 ? "file" : "files")} in {area}";
        if (paths.Length == 0) return Fit(count, width);
        var first = paths[0][(paths[0].LastIndexOf('/') + 1)..];
        var files = ": " + first + (changes.Total == 1 ? "" : $" and {changes.Total - 1} more");
        return Fit(Prefer(count, files, width), width);
    }

    private static string PathWithoutStatus(string file)
    {
        var separator = file.IndexOf(' ');
        return (separator < 0 ? file : file[(separator + 1)..]).Replace('\\', '/');
    }

    private static string? Evidence(OwnerGoalPreviewEvidence? evidence, TimeZoneInfo zone)
    {
        if (evidence is null) return null;
        var time = TimeZoneInfo.ConvertTime(evidence.At, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var label = evidence.Kind == OwnerGoalPreviewEvidenceKind.Gate ? "gate" : "focused tests";
        return $"{label} {(evidence.Passed ? "passed" : "failed")} {time}" +
            (evidence.Kind == OwnerGoalPreviewEvidenceKind.Gate && !evidence.Passed &&
                !string.IsNullOrWhiteSpace(evidence.FirstFailedCheck) ? ": " + evidence.FirstFailedCheck : "");
    }

    private static string Prefer(string required, string optional, int width) =>
        width > 0 && (required + optional).GetColumns() > width ? required : required + optional;

    private static string Fit(string text, int width) =>
        OwnerConsoleLineFitter.Fit(text, OwnerConsoleLineSpans.None, width);
}
