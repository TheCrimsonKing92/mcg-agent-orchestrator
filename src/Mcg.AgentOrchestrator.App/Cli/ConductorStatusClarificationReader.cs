using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusClarificationReader
{
    internal sealed record Result(bool Readable,
        IReadOnlyDictionary<string, IReadOnlyList<ConductorStatusClarification>> ByGoal);

    internal static Result Read(OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
    {
        if (!File.Exists(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")))
            return new(true, new Dictionary<string, IReadOnlyList<ConductorStatusClarification>>());

        try
        {
            var store = CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory);
            var queue = store.GetAttentionQueueAsync().GetAwaiter().GetResult();
            var items = CliCommandHandlers.CollaborationItemsForAttention(queue, kernel, null, includeHistory: false)
                .Where(CliCommandHandlers.IsSpecClarification);
            var goals = kernel.Goals.ToDictionary(goal => goal.Id.Value, StringComparer.OrdinalIgnoreCase);
            var byGoal = new Dictionary<string, IReadOnlyList<ConductorStatusClarification>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in items.Where(item => item.GoalId is not null && goals.ContainsKey(item.GoalId))
                .GroupBy(item => item.GoalId!, StringComparer.OrdinalIgnoreCase))
            {
                var goal = goals[group.Key];
                var all = CliCommandHandlers.AllClarificationsForGoal(store, goal);
                var hold = AuthorOwnerQuestionHold.TryParse(goal.CurrentHold);
                byGoal[goal.Id.Value] = group.OrderBy(item => item.RaisedAt).Select(item =>
                {
                    var id = CliCommandHandlers.ClarificationId(item, all);
                    var reason = hold?.TargetKind == nameof(OperatorAnswerTargetKind.Clarification) &&
                        string.Equals(hold.TargetId, item.Id, StringComparison.OrdinalIgnoreCase)
                        ? "author-owner-question" : "awaiting-clarification";
                    return new ConductorStatusClarification(id, item.RaisedAt, reason,
                        $"attention answer {goal.Id.Value[..8]} {id} <answer>");
                }).ToArray();
            }
            return new(true, byGoal);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(false, new Dictionary<string, IReadOnlyList<ConductorStatusClarification>>());
        }
    }
}
