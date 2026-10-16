using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBacklogPremiseCommands
{
    internal static bool IsCommand(string command) => command.ToLowerInvariant() is "backlog-premise-check" or "backlog-premise-close";

    internal static bool? TryExecute(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        if (!IsCommand(command)) return null;
        var close = command.Equals("backlog-premise-close", StringComparison.OrdinalIgnoreCase);
        var usage = close ? CliCommandHelp.BacklogPremiseCloseUsage : CliCommandHelp.BacklogPremiseCheckUsage;
        var minAge = 30;
        var confirmed = false;
        var prefixes = new List<string>();
        for (var i = 1; i < parts.Count; i++)
        {
            if (parts[i] == "--min-age-days")
            {
                if (++i >= parts.Count || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out minAge))
                    throw new ArgumentException(usage);
            }
            else if (close && parts[i] == "--confirm-backlog-premise-close") confirmed = true;
            else if (parts[i].StartsWith('-') || !close || string.IsNullOrWhiteSpace(parts[i])) throw new ArgumentException(usage);
            // The interactive parser groups positional arguments after the first one.
            else prefixes.AddRange(parts[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(prefix => prefix.ToLowerInvariant()));
        }
        if (close && (!confirmed || prefixes.Count == 0)) throw new ArgumentException(usage);

        var readStore = BacklogStore.OpenReadOnly(context.Workspace.BacklogStorePath);
        var items = readStore.ListAsync(includeAll: true).GetAwaiter().GetResult()
            .Select(item => item.Status == BacklogItemStatus.Open
                ? readStore.GetByExactIdAsync(item.Id).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException($"Backlog item {item.Id} disappeared during premise check.")
                : item).ToArray();
        var epicIds = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(context.Workspace.PortfolioStorePath))
        {
            var portfolio = PortfolioStore.OpenReadOnly(context.Workspace.PortfolioStorePath);
            foreach (var epic in portfolio.ListEpicsAsync().GetAwaiter().GetResult())
                foreach (var member in portfolio.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult())
                    if (member.Kind == PortfolioMemberKind.BacklogItem) epicIds.Add(member.MemberId);
        }
        var goals = context.Kernel.Goals.Select(goal => new BacklogPremiseCheck.GoalInput(
            goal.Id.Value, goal.Status, goal.Objective, goal.SourceBacklogItemId, goal.SourceBacklogCoverage)).ToArray();
        var result = BacklogPremiseCheck.Evaluate(items, goals, epicIds, path =>
        {
            var fullPath = Path.Combine(context.Workspace.RootDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }, DateTimeOffset.UtcNow, minAge);

        if (!close)
        {
            foreach (var proposal in result.Proposals)
                Console.WriteLine($"propose-close {BacklogPremiseCheck.Prefix(proposal.Item.Id)} age={proposal.AgeDays}d rules={proposal.Rules} evidence={proposal.Evidence} title={Title(proposal.Item)}");
            foreach (var entry in result.Skim)
                Console.WriteLine($"skim {BacklogPremiseCheck.Prefix(entry.Item.Id)} group={entry.Group} age={entry.AgeDays}d title={Title(entry.Item)}");
            Console.WriteLine($"Backlog premise check: eligible={result.Eligible} exempt-north-star={result.ExemptNorthStar} exempt-epic={result.ExemptEpic} propose-close={result.Proposals.Count} skim={result.Skim.Count} min-age-days={minAge}");
            return false;
        }

        var eligible = result.Proposals.Select(entry => entry.Item.Id).Concat(result.Skim.Select(entry => entry.Item.Id)).ToHashSet(StringComparer.Ordinal);
        var proposals = result.Proposals.ToDictionary(entry => entry.Item.Id, StringComparer.Ordinal);
        var closedIds = new HashSet<string>(StringComparer.Ordinal);
        BacklogStore? writeStore = null;
        var refused = 0;
        foreach (var prefix in prefixes)
        {
            var matches = items.Where(item => item.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var reason = matches.Length == 0 ? "not-found" : matches.Length > 1 ? "ambiguous" :
                matches[0].Status != BacklogItemStatus.Open || closedIds.Contains(matches[0].Id) ? "not-open" :
                !eligible.Contains(matches[0].Id) ? "not-eligible" : !proposals.ContainsKey(matches[0].Id) ? "no-close-rule" : null;
            if (reason is null)
            {
                var item = readStore.GetByExactIdAsync(matches[0].Id).GetAwaiter().GetResult();
                if (item is null || item.Status != BacklogItemStatus.Open) reason = "not-open";
                else
                {
                    writeStore ??= new BacklogStore(context.Workspace.BacklogStorePath);
                    var proposal = proposals[item.Id];
                    var closed = writeStore.CloseAsync(item.Id, $"premise-check {proposal.Rules}: {proposal.Evidence}").GetAwaiter().GetResult();
                    if (closed.Status == BacklogItemStatus.Done) closedIds.Add(item.Id);
                    else reason = "not-open";
                }
            }
            if (reason is not null)
            {
                refused++;
                Console.WriteLine($"refused {prefix}: {reason}");
            }
        }
        Console.WriteLine($"Backlog premise close: closed={closedIds.Count} refused={refused}");
        return false;
    }

    private static string Title(BacklogItem item) => item.Title.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
