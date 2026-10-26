using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AnsweredSelectorRebinding
{
    internal sealed record Rebind(string OldClass, string NewClass, string RequestId, string AnswerId);

    // Only the keyword ignores case. Quotes must balance and names must be whole identifiers.
    private const string IdentifierPart = @"\p{L}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}\p{Cf}";
    private static readonly Regex RenamePattern = new(
        $@"(?<![{IdentifierPart}.`])(?<oldQuote>`?)(?<old>[A-Z][{IdentifierPart}]*)\k<oldQuote>(?![{IdentifierPart}.`])" +
        $@"\s+(?:(?i:becomes)|->)\s+" +
        $@"(?<![{IdentifierPart}.`])(?<newQuote>`?)(?<new>[A-Z][{IdentifierPart}]*)\k<newQuote>(?![{IdentifierPart}.`])",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<(string OldClass, string NewClass)> Parse(string? answer)
    {
        if (string.IsNullOrEmpty(answer)) return [];
        return RenamePattern.Matches(answer)
            .Select(match => (OldClass: match.Groups["old"].Value, NewClass: match.Groups["new"].Value))
            .Where(pair => pair.OldClass != pair.NewClass).Distinct().ToArray();
    }

    internal static IReadOnlyList<Rebind> For(
        Goal goal, GateTestObligation obligation, IEnumerable<HumanInputRequest> requests) =>
        requests.Where(request => request.GoalId == goal.Id && !request.WasDismissed)
            .Select(request => new { Request = request, Answer = request.AuthoritativeAnswer })
            .Where(item => item.Answer is { Origin: HumanInputAnswerOrigin.Operator } &&
                item.Answer.BriefVersion == obligation.BriefVersion)
            .OrderBy(item => item.Answer!.AnsweredAt)
            .ThenBy(item => item.Answer!.Id, StringComparer.Ordinal)
            .SelectMany(item => Parse(item.Answer!.Text).Select(pair =>
                new Rebind(pair.OldClass, pair.NewClass, item.Request.Id.Value, item.Answer.Id)))
            .GroupBy(rebind => rebind.OldClass, StringComparer.Ordinal)
            .Where(group => group.Select(rebind => rebind.NewClass).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => group.First()).ToArray();
}
