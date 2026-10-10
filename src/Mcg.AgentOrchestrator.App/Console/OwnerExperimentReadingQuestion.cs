using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerExperimentReadingQuestion
{
    internal static string? ShortId(string questionText)
    {
        var firstLine = questionText.Split('\n', 2)[0];
        var match = System.Text.RegularExpressions.Regex.Match(firstLine, @"^Experiment (\S{8}) reading due: ");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static OwnerQuestion? From(CollaborationItem item)
    {
        if (item.Type != CollaborationItemType.Decision || !string.IsNullOrWhiteSpace(item.GoalId) ||
            item.CorrelationKey?.StartsWith("experiment-reading-due:", StringComparison.Ordinal) != true)
            return null;
        var parts = item.CorrelationKey.Split(':');
        if (parts.Length != 3 || parts[1].Length != 32) return null;
        var trigger = parts[2] switch
        {
            "stop-rule" => "stop rule reached",
            "guardrail" => "guardrail breached",
            _ => null
        };
        if (trigger is null) return null;
        var id = parts[1][..8];
        var text = $"Experiment {id} reading due: {trigger}\n" +
            $"keep: confirm the result with experiment-decide {id} --outcome confirmed --evidence <reference> --action <text>\n" +
            $"revert: refute it with experiment-decide {id} --outcome refuted --evidence <reference> --action <text>\n" +
            "extend: do nothing now; the question stays open until you decide\n" +
            $"show: run experiment-show {id} to print the reading";
        return new OwnerQuestion(item.Id, "", OwnerQuestionKind.ExperimentReading, text);
    }
}
