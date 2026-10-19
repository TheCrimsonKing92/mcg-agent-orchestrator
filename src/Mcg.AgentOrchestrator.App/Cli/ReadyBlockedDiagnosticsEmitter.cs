using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ReadyBlockedDiagnosticsEmitter
{
internal static void Emit(IReadOnlyList<ReadyBlockedDiagnostic> diagnostics)
{
    foreach (var diagnostic in diagnostics.OrderBy(diagnostic => diagnostic.TaskNumber))
    {
        Console.Error.WriteLine(diagnostic.ToLine());
    }
}

internal static void EmitForAssigned(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IReadOnlyCollection<Goal> providerHoldScope,
    string reason)
{
    var planItems = SubscriptionPlanBuilder.Build(
            goal,
            agents,
            profiles,
            providerHoldScope: providerHoldScope)
        .Items
        .Where(item => item.TaskStatus == WorkTaskStatus.Assigned)
        .OrderBy(item => item.TaskNumber);
    foreach (var item in planItems)
    {
        var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == item.TaskId);
        var agent = task?.AssignedAgentId is null
            ? null
            : agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
        var provider = task is null || agent is null
            ? item.ProfileName
            : WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task, profiles);
        provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
        var hold = item.ProviderBudgetHold;
        var diagnostic = new ReadyBlockedDiagnostic(
            goal.Id.Value[..8],
            item.TaskNumber,
            item.TaskId,
            provider,
            hold is null ? reason : "provider-budget-exhausted",
            hold is null
                ? null
                :
                [
                    $"binding={hold.BindingKey}",
                    $"scope={hold.BindingScope}",
                    $"source={hold.SourceGoalId.Value[..8]}/{hold.SourceTaskId.Value[..8]}",
                    $"receipt={hold.EvidenceReceipt}"
                ]);
        Console.Error.WriteLine(diagnostic.ToLine());
    }
}
}
