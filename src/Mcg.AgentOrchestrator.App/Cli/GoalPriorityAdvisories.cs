using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalPriorityAdvisories
{
    internal static void Warn(CliExecutionContext context, Goal goal, GoalPriority submitted,
        Func<PortfolioStore>? openPortfolio = null)
    {
        if (submitted < GoalPriority.High) return;
        TryAdvisory("dilution", () =>
        {
            if (openPortfolio is null && !File.Exists(context.Workspace.PortfolioStorePath)) return;
            var store = openPortfolio is null ? PortfolioStore.OpenReadOnly(context.Workspace.PortfolioStorePath) : openPortfolio();
            var goals = context.Kernel.Goals.ToDictionary(item => item.Id.Value, StringComparer.Ordinal);
            foreach (var epic in store.ListEpicsAsync().GetAwaiter().GetResult())
            {
                var members = store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult()
                    .Where(member => member.Kind == PortfolioMemberKind.Goal)
                    .Select(member => member.MemberId).Distinct(StringComparer.Ordinal).ToArray();
                if (!members.Contains(goal.Id.Value, StringComparer.Ordinal)) continue;
                var active = members.Where(goals.ContainsKey).Select(id => goals[id]).Where(item => !item.IsTerminal).ToArray();
                var high = active.Count(item => (item.Id == goal.Id ? submitted : item.Priority) >= GoalPriority.High);
                if (high * 3 > active.Length)
                    Emit($"PRIORITY_DILUTION goal={goal.Id.Value[..8]} epic={epic.Id[..8]} high={high} active={active.Length} threshold=1/3");
            }
        });
        TryAdvisory("overlap", () =>
        {
            var others = context.Kernel.Goals.Where(item => item.Id != goal.Id && !item.IsTerminal).ToArray();
            var byId = others.ToDictionary(item => item.Id.Value, StringComparer.Ordinal);
            var report = GoalScopeCollisionAdvisor.Build([goal.Objective], others);
            foreach (var collision in report.Collisions.Where(BoardFillScopeDepends.Counts)
                         .DistinctBy(item => (item.GoalId, item.Kind, item.ProposedPath)))
            {
                var other = byId[collision.GoalId];
                if (goal.DependsOn.Contains(other.Id) || other.DependsOn.Contains(goal.Id)) continue;
                Emit($"PRIORITY_OVERLAP goal={goal.Id.Value[..8]} other={other.Id.Value[..8]} kind={collision.Kind} path={collision.ProposedPath}");
            }
        });

        void Emit(string line)
        {
            Console.Error.WriteLine(line);
            TryAdvisory("log", () => new ConductEventLogWriter(context.Workspace.ConductEventsLogPath)
                .Append("priority", goal.Id.Value[..8], line));
        }
    }

    private static void TryAdvisory(string kind, Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Priority {kind} advisory unavailable: {exception.Message.Replace('\r', ' ').Replace('\n', ' ')}");
        }
    }
}
