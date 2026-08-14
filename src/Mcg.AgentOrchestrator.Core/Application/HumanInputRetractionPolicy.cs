using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class HumanInputRetractionPolicy
{
    public static string Apply(
        string content,
        IReadOnlyList<HumanInputRequest> requests,
        IReadOnlyList<HumanInputAnswerRecord> clarificationAnswerHistory)
    {
        var authoritativeTexts = requests
            .Select(request => request.AuthoritativeAnswer?.Text)
            .Concat(clarificationAnswerHistory
                .Where(answer => !answer.IsRetracted)
                .Select(answer => answer.Text))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(text => text.Length)
            .ToArray();
        var supersededRequestIds = requests
            .Where(request => request.AnswerHistory.Any(answer => answer.IsRetracted))
            .Select(request => request.Id)
            .ToHashSet();
        var retractedTexts = requests
            .SelectMany(request => request.AnswerHistory)
            .Concat(clarificationAnswerHistory)
            .Where(answer => answer.IsRetracted)
            .Select(answer => answer.Text)
            .Concat(requests
                .Where(request =>
                    request.AnswerHistory.Any(answer => answer.IsRetracted) ||
                    (request.SupersededByRequestId is not null && supersededRequestIds.Contains(request.SupersededByRequestId)))
                .Select(request => request.DerivedBlockerEvidence)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Except(authoritativeTexts, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(text => text.Length)
            .ToArray();
        if (retractedTexts.Length == 0)
        {
            return content;
        }

        var alternatives = authoritativeTexts
            .Select(text => (Text: text, IsRetracted: false))
            .Concat(retractedTexts.Select(text => (Text: text, IsRetracted: true)))
            .OrderByDescending(candidate => candidate.Text.Length)
            .ThenBy(candidate => candidate.IsRetracted)
            .ToArray();
        var retractedSet = retractedTexts.ToHashSet(StringComparer.Ordinal);
        var pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', alternatives.Select(candidate => Regex.Escape(candidate.Text)))})(?![\p{{L}}\p{{N}}])";
        return Regex.Replace(
            content,
            pattern,
            match => retractedSet.Contains(match.Value) ? string.Empty : match.Value,
            RegexOptions.CultureInvariant);
    }
}
