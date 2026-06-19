using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class OperatorEscalationProjection
{
    private static readonly HashSet<OperatorInboxKind> PromoteToMainKinds =
    [
        OperatorInboxKind.LandingEscalation,
        OperatorInboxKind.AcceptanceGate
    ];

    public static OperatorEscalation? Project(OperatorInboxItem item, string? dashboardBaseUrl = null)
    {
        if (item.Severity != OperatorInboxSeverity.Blocker)
            return null;

        var actions = DeriveActions(item);
        var deepLink = BuildDeepLink(dashboardBaseUrl, item.GoalPrefix);

        return new OperatorEscalation(
            item.Id,
            item.GoalId,
            item.GoalPrefix,
            item.Kind.ToString(),
            item.Title,
            FormatSummary(item.Objective, item.Message, item.SuggestedAction),
            item.Evidence,
            actions,
            deepLink);
    }

    public static IReadOnlyList<OperatorEscalation> ProjectAll(
        IReadOnlyList<OperatorInboxItem> items,
        string? dashboardBaseUrl = null)
    {
        return items
            .Where(item => item.Severity == OperatorInboxSeverity.Blocker && !item.Acknowledged)
            .Select(item => Project(item, dashboardBaseUrl))
            .Where(e => e is not null)
            .Cast<OperatorEscalation>()
            .ToList();
    }

    internal static IReadOnlyList<OperatorEscalationAction> DeriveActions(OperatorInboxItem item)
    {
        var command = item.SuggestedCommand?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
            return [];

        var requiresConfirm = IsPromoteToMain(item.Kind, command);
        var requiresInput = command.Contains("<answer>", StringComparison.OrdinalIgnoreCase)
            || command.Contains("<command>", StringComparison.OrdinalIgnoreCase);

        var label = BuildActionLabel(item.Kind, item.TaskNumber);

        var actions = new List<OperatorEscalationAction>
        {
            new(label, command, requiresConfirm, requiresInput)
        };

        if (item.Kind == OperatorInboxKind.AcceptanceGate && !requiresInput)
        {
            var acknowledgeCommand = $"inbox-ack {item.Id}";
            actions.Add(new OperatorEscalationAction("Acknowledge", acknowledgeCommand, RequiresConfirm: false));
        }

        return actions.Take(3).ToList();
    }

    private static bool IsPromoteToMain(OperatorInboxKind kind, string command)
    {
        if (PromoteToMainKinds.Contains(kind))
            return true;
        return command.StartsWith("land ", StringComparison.OrdinalIgnoreCase)
            || command.Equals("land", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("acceptance ", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildActionLabel(OperatorInboxKind kind, int? taskNumber) =>
        kind switch
        {
            OperatorInboxKind.HumanInput => "Provide Answer",
            OperatorInboxKind.FailedTask => taskNumber is null ? "Retry Next" : $"Retry Task {taskNumber}",
            OperatorInboxKind.FailedVerification => "Re-verify",
            OperatorInboxKind.MissingVerification => "Run Verification",
            OperatorInboxKind.AcceptanceGate => "Accept Goal",
            OperatorInboxKind.LandingEscalation => "Promote to Main",
            OperatorInboxKind.ReadinessPreflight => "View Readiness",
            _ => "Take Action"
        };

    private static string? BuildDeepLink(string? dashboardBaseUrl, string goalPrefix)
    {
        if (string.IsNullOrWhiteSpace(dashboardBaseUrl))
            return null;
        var trimmed = dashboardBaseUrl.TrimEnd('/');
        return $"{trimmed}/goals/{goalPrefix}";
    }

    internal static string FormatSummary(string objective, string reason, string suggestedAction)
    {
        var title = FormatObjectiveTitle(objective);
        var summary = $"Goal: {title}{Environment.NewLine}Reason: {Clean(reason)}";
        if (!string.IsNullOrWhiteSpace(suggestedAction))
        {
            summary += $"{Environment.NewLine}Response: {Clean(suggestedAction)}";
        }

        return summary;
    }

    internal static string FormatObjectiveTitle(string objective)
    {
        var clean = Clean(objective);
        if (clean.Length <= 120)
            return clean;
        return clean[..117] + "...";
    }

    internal static string Clean(string value) =>
        string.Join(" ", value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
