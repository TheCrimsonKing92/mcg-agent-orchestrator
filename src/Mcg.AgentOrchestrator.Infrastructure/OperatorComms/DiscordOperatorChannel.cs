using Mcg.AgentOrchestrator.Core;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordOperatorChannel : IOperatorChannel
{
    private readonly ICollaborationItemStore _store;
    private readonly Func<string, CancellationToken, Task<long?>>? _currentGoalStateVersion;

    public DiscordOperatorChannel(
        ICollaborationItemStore store,
        Func<string, CancellationToken, Task<long?>>? currentGoalStateVersion = null)
    {
        _store = store;
        _currentGoalStateVersion = currentGoalStateVersion;
    }

    public string ChannelType => "discord";

    public async Task SendEscalationAsync(OperatorEscalation escalation, CancellationToken cancellationToken = default)
    {
        var expectedGoalStateVersion = _currentGoalStateVersion is null
            ? null
            : await _currentGoalStateVersion(escalation.GoalId, cancellationToken);
        await _store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            escalation.GoalId,
            escalation.Title,
            BuildContent(escalation),
            escalation.InboxItemId,
            BuildBindings(escalation.InboxItemId, escalation.Actions, expectedGoalStateVersion),
            cancellationToken);
    }

    private static IReadOnlyList<CollaborationActionBinding> BuildBindings(
        string inboxItemId,
        IReadOnlyList<OperatorEscalationAction> actions,
        long? expectedGoalStateVersion)
    {
        var bindings = actions
            .Take(3)
            .Select(action => new CollaborationActionBinding(
                action.Label,
                action.Command,
                action.RequiresConfirm,
                action.RequiresInput,
                action.ExpectedGoalStateVersion ?? expectedGoalStateVersion,
                action.ExpiresAt))
            .ToList();
        return bindings.Count == 0
            ? [new CollaborationActionBinding("Resolve", $"operator-inbox-ack {inboxItemId}", ExpectedGoalStateVersion: expectedGoalStateVersion)]
            : bindings;
    }

    private static string BuildContent(OperatorEscalation escalation)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**[{escalation.Kind}]** {escalation.Title}");
        sb.AppendLine($"**Goal:** `{escalation.GoalPrefix}` ({escalation.GoalId})");
        sb.AppendLine();
        sb.AppendLine(escalation.Summary);
        sb.AppendLine();
        sb.AppendLine($"**Evidence:** {escalation.KeyEvidence}");
        if (escalation.Actions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Suggested command(s):**");
            foreach (var action in escalation.Actions.Take(3))
            {
                sb.AppendLine($"- `{action.Command}`");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"**Response:** {BuildResponseInstructions(escalation.Actions)}");
        if (!string.IsNullOrWhiteSpace(escalation.DashboardDeepLink))
        {
            sb.AppendLine();
            sb.AppendLine($"**Dashboard:** {escalation.DashboardDeepLink}");
        }
        return sb.ToString().Trim();
    }

    private static string BuildResponseInstructions(IReadOnlyList<OperatorEscalationAction> actions)
    {
        if (actions.Any(action => action.RequiresInput))
        {
            return "Run the suggested command after replacing placeholder values, then acknowledge the inbox item if it remains open.";
        }

        if (actions.Any(action => action.RequiresConfirm))
        {
            return "Review the evidence first; use the confirmation button or run the command from the repository root when ready.";
        }

        return "Use a button when present, or run the command from the repository root; acknowledge the inbox item once resolved.";
    }

}
