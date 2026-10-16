using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogPremiseCheck
{
    internal sealed record GoalInput(string Id, GoalStatus Status, string Objective,
        string? SourceBacklogItemId = null, SourceBacklogCoverage? Coverage = null);
    internal sealed record Proposal(BacklogItem Item, int AgeDays, string Rules, string Evidence);
    internal sealed record SkimEntry(BacklogItem Item, int AgeDays, string Group);
    internal sealed record Result(IReadOnlyList<Proposal> Proposals, IReadOnlyList<SkimEntry> Skim,
        int Eligible, int ExemptNorthStar, int ExemptEpic);

    private static readonly Regex CitedPath = new(
        "(?:^|[\\s`'\"(\\[])(?<path>(?:src|tests|docs|scripts|config)/[A-Za-z0-9_./-]+)");
    private static readonly Regex HexId = new("^[a-f0-9]{32}$");
    private static readonly Regex Words = new("[a-z0-9]{3,}");
    private static readonly Regex HexRuns = new("[a-f0-9]{8,}");
    private static readonly Regex PartialWork = new(
        @"\b(?:step|increment|slice|first|kickoff|anchor|prerequisite|follow-up|phase|part|residual|consolidat\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static Result Evaluate(IReadOnlyList<BacklogItem> items, IReadOnlyList<GoalInput> goals,
        IReadOnlySet<string> epicLinkedIds, Func<string, bool> pathExists, DateTimeOffset now, int minAgeDays = 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minAgeDays);
        var done = items.Where(item => item.Status == BacklogItemStatus.Done).ToArray();
        var completed = goals.Where(goal => goal.Status == GoalStatus.Completed).ToArray();
        var proposals = new List<Proposal>();
        var skim = new List<SkimEntry>();
        var northStar = 0;
        var epic = 0;
        var eligible = 0;
        foreach (var item in items.OrderBy(item => item.UpdatedAt).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (item.Status != BacklogItemStatus.Open || (now - item.UpdatedAt).TotalDays <= minAgeDays) continue;
            if (item.Title.Trim().StartsWith("NORTH STAR", StringComparison.OrdinalIgnoreCase))
            {
                northStar++;
                continue;
            }
            if (epicLinkedIds.Contains(item.Id))
            {
                epic++;
                continue;
            }
            eligible++;
            var paths = Paths(item);
            var missing = paths.Where(path => !pathExists(path)).ToArray();
            var ruleA = paths.Length > 0 && missing.Length == paths.Length;
            var titleWords = Tokens(item.Title);
            var prefix = Prefix(item.Id);
            var idPattern = HexId.IsMatch(item.Id) ? $"(?<![a-f0-9]){prefix}(?![a-f0-9])" : null;
            var refs = done.Where(other => other.Id != item.Id &&
                    ((idPattern is not null && (Regex.IsMatch(other.Title, idPattern) || Regex.IsMatch(other.Body, idPattern))) ||
                     Similar(titleWords, Tokens(other.Title))))
                .Select(other => Prefix(other.Id))
                .Concat(completed.Where(goal =>
                    (idPattern is not null && Regex.IsMatch(goal.Objective, idPattern)) ||
                    Similar(titleWords, Tokens(FirstLine(goal.Objective)))).Select(goal => Prefix(goal.Id)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var linked = completed.Where(goal => goal.SourceBacklogItemId == item.Id && goal.Coverage != SourceBacklogCoverage.Slice).ToArray();
            var named = completed.Where(goal => SoleTarget(item.Id, FirstLine(goal.Objective))).ToArray();
            var ruleB = refs.Length > 0;
            var ruleC = linked.Length > 0;
            var ruleD = named.Length > 0;
            var age = (int)Math.Floor((now - item.UpdatedAt).TotalDays);
            if ((ruleA && ruleB) || ruleC || ruleD)
            {
                var rules = new List<string>();
                var evidence = new List<string>();
                if (ruleA) { rules.Add("A"); evidence.Add($"missing:{string.Join('|', missing)}"); }
                if (ruleB) { rules.Add("B"); evidence.Add($"refs:{string.Join(',', refs)}"); }
                if (ruleC) rules.Add("C");
                if (ruleD) rules.Add("D");
                var goalIds = linked.Concat(named).Select(goal => Prefix(goal.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (goalIds.Length > 0) evidence.Add($"goal:{string.Join(',', goalIds)}");
                proposals.Add(new(item, age, string.Join(',', rules), string.Join(';', evidence)));
            }
            else skim.Add(new(item, age, missing.Length > 0 ? "some-paths-missing" : ruleB ? "referenced" : "no-signal"));
        }
        return new(proposals, skim.OrderBy(entry => entry.Group switch
        {
            "some-paths-missing" => 0, "referenced" => 1, _ => 2
        }).ThenBy(entry => entry.Item.UpdatedAt).ThenBy(entry => entry.Item.Id, StringComparer.Ordinal).ToArray(), eligible, northStar, epic);
    }

    private static string[] Paths(BacklogItem item) => new[] { item.Title, item.Body }.Concat(item.Notes.Select(note => note.Text))
        .SelectMany(text => CitedPath.Matches(text).Cast<Match>().Where(match =>
            match.Index + match.Length == text.Length || text[match.Index + match.Length] != '*')
            .Select(match => match.Groups["path"].Value.TrimEnd('.', ',', ';', ':', ')', ']')))
        .Where(path => !path.Split('/').Any(segment => segment is ".." or "bin" or "obj" or "TestResults" or "artifacts" ||
            (segment.StartsWith('.') && segment != ".")))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static HashSet<string> Tokens(string title) => Words.Matches(title.ToLowerInvariant()).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    private static bool Similar(HashSet<string> left, HashSet<string> right) => left.Count > 0 && right.Count > 0 &&
        (double)left.Intersect(right).Count() / left.Union(right).Count() >= 0.8;

    private static bool SoleTarget(string id, string line)
    {
        if (!HexId.IsMatch(id)) return false;
        var prefix = Prefix(id);
        var match = Regex.Match(line, $"(?<![a-f0-9]){prefix}(?:{id[8..]})?(?![a-f0-9])");
        if (!match.Success) return false;
        var end = line.IndexOf(')', match.Index + match.Length);
        var window = end < 0 ? line : line[..(end + 1)];
        return Regex.IsMatch(window, $"backlog (?:item )?`?{prefix}") && !PartialWork.IsMatch(window) &&
            HexRuns.Matches(window).All(run => run.Value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');
    internal static string Prefix(string id) => id[..Math.Min(8, id.Length)];
}
