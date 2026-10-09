using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetail
{
    internal static async Task<string?> ResolveAsync(IOrchestratorStateQueries state,
        string idOrPrefix, IOwnerConsoleOutput output, CancellationToken cancellationToken)
    {
        var matches = (await state.ListGoalMetadataAsync(cancellationToken))
            .Where(item => item.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A board selection supplies an exact id, even when that id prefixes another goal.
        var exact = matches.FirstOrDefault(item => item.Id.Equals(idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) matches = [exact];
        if (matches.Length != 1)
        {
            output.WriteLine(matches.Length == 0 ? $"no goal matches '{idOrPrefix}'" : "ambiguous goal");
            foreach (var match in matches)
                output.WriteLine(match.Id[..Math.Min(8, match.Id.Length)] + "  " + OwnerGoalTitle.From(match.Objective));
            return null;
        }
        return matches[0].Id;
    }

    internal static string Stage(Goal goal) =>
        goal.Tasks.FirstOrDefault(task => task.Status != WorkTaskStatus.Completed)?.RequiredRole.ToString() ?? "-";
}
