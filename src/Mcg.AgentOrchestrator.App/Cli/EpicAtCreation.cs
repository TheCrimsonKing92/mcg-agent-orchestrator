using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Portfolio membership is a separate-store side effect of an already committed creation.
internal static class EpicAtCreation
{
    public static PortfolioEpic? ResolveRequested(OrchestratorWorkspace workspace, string? argument)
    {
        if (argument is null)
            return null;
        if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--epic requires an epic id or title.");

        try
        {
            return new PortfolioStore(workspace.PortfolioStorePath)
                .ResolveEpicAsync(argument).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("No epic matched; nothing was created.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"--epic '{argument}': {ex.Message}", ex);
        }
    }

    public static string? ResolveGoalEpic(
        OrchestratorWorkspace workspace, PortfolioEpic? requested, string? backlogItemId)
    {
        PortfolioMembership? inherited = null;
        if (backlogItemId is not null && File.Exists(workspace.PortfolioStorePath))
        {
            try
            {
                inherited = new PortfolioStore(workspace.PortfolioStorePath)
                    .GetBacklogMembershipAsync(backlogItemId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var reason = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
                Console.WriteLine($"Warning: backlog item {backlogItemId} epic inheritance unavailable: {reason}");
            }
        }
        if (requested is not null && inherited is not null && requested.Id != inherited.EpicId)
        {
            Console.WriteLine($"Explicit epic {requested.Id[..8]} overrides backlog item {backlogItemId} epic {inherited.EpicId[..8]}.");
        }
        return requested?.Id ?? inherited?.EpicId;
    }

    public static void AssignAfterCommit(
        OrchestratorWorkspace workspace, PortfolioMemberKind kind, string memberId, string? epicId)
    {
        if (epicId is null)
            return;
        var memberKind = kind == PortfolioMemberKind.Goal ? "goal" : "backlog";
        try
        {
            var store = new PortfolioStore(workspace.PortfolioStorePath);
            if (kind == PortfolioMemberKind.Goal)
                store.AssignGoalToEpicAsync(memberId, epicId).GetAwaiter().GetResult();
            else
                store.AssignBacklogItemToEpicAsync(memberId, epicId).GetAwaiter().GetResult();
            var label = kind == PortfolioMemberKind.Goal ? "goal" : "backlog item";
            Console.WriteLine($"Assigned {label} {memberId[..8]} to epic {epicId[..8]}");
        }
        catch (Exception ex)
        {
            var reason = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            Console.WriteLine($"EPIC_ASSIGN_FAILED {memberKind} {memberId} epic={epicId} reason={reason} recover: epic-assign {memberId} {epicId}");
        }
    }
}
