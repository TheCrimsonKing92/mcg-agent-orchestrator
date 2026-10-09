namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerQuestionResolutionText
{
    internal static string Build(OwnerQuestionResolution resolution, TimeZoneInfo zone)
    {
        var lines = new List<string>
        {
            $"Goal: {OwnerConsoleViewModelBuilder.Prefix(resolution.GoalId)} {resolution.GoalTitle}",
            "Asked at stage: " + resolution.Stage,
            "Question:", resolution.Question,
            $"{resolution.Outcome} by {resolution.Answerer} at {TimeZoneInfo.ConvertTime(resolution.ResolvedAt, zone):HH:mm:ss}"
        };
        if (resolution.Answer is { } answer)
        {
            lines.Add("Answer:");
            lines.Add(answer);
            var evidence = resolution.Evidence.Where(reference => !reference.StartsWith("author-item=", StringComparison.Ordinal)).ToArray();
            if (evidence.Length > 0) { lines.Add("Evidence:"); lines.AddRange(evidence); }
        }
        lines.Add("What happens next: " + resolution.NextStage);
        return string.Join("\n", lines);
    }

    internal static string Summary(OwnerQuestionResolution item, TimeZoneInfo zone) =>
        $"{item.Stage}: {item.Question.Split(['\r', '\n'], 2)[0]} — {item.Answerer}, {TimeZoneInfo.ConvertTime(item.ResolvedAt, zone):HH:mm:ss} ({item.Outcome})";
}
