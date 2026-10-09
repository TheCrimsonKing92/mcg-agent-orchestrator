using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class EpicPlanReadySliceSelector
{
    internal static IReadOnlyList<string> Read(OrchestratorWorkspace workspace)
    {
        if (!File.Exists(workspace.PortfolioStorePath)) return [];
        var epics = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath)
            .ListEpicsAsync().GetAwaiter().GetResult();
        return Order(EpicPlanStatusReader.Load(workspace, epics).Values);
    }

    internal static IReadOnlyList<string> Order(IEnumerable<EpicPlanView> views) =>
        views.OrderBy(view => view.Plan.EpicId, StringComparer.Ordinal)
            .SelectMany(view => view.Items.OrderBy(item => item.Item.Position))
            .Where(item => item.Item.Kind == EpicPlanItemKind.Slice && item.Status == EpicPlanSliceStatus.Ready)
            .Select(item => item.Item.BacklogItemId!)
            .Distinct(StringComparer.Ordinal).ToArray();
}
