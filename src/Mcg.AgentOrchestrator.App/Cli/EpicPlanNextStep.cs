using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record EpicPlanNextStepResult(string Text, bool IsStalled);

internal static class EpicPlanNextStep
{
    internal const string StalledText = "Stalled: nothing in flight or ready";

    internal static EpicPlanNextStepResult Compute(IReadOnlyList<EpicPlanItemStatus> items)
    {
        var ordered = items.OrderBy(item => item.Item.Position).ToArray();
        var inFlight = ordered.Where(item => item.Status is EpicPlanSliceStatus.InFlight or EpicPlanSliceStatus.Verified).ToArray();
        if (inFlight.Length > 0) return new(string.Join("; ", inFlight.Select(Describe)), false);
        var ready = ordered.FirstOrDefault(item => item.Status == EpicPlanSliceStatus.Ready);
        if (ready is not null) return new(Describe(ready), false);
        var step = ordered.FirstOrDefault(item => item.Item.Kind == EpicPlanItemKind.Step && !item.Item.Done);
        if (step is not null) return new(Describe(step), false);
        var incomplete = ordered.Any(item => item.Item.Kind == EpicPlanItemKind.Step ? !item.Item.Done :
            item.Status is not (EpicPlanSliceStatus.Landed or EpicPlanSliceStatus.Done or EpicPlanSliceStatus.Superseded));
        return incomplete ? new(StalledText, true) : new(items.Count == 0 ? "no plan" : "complete", false);
    }

    internal static string Summary(IReadOnlyList<EpicPlanItemStatus> items)
    {
        var next = Compute(items);
        var slices = items.Count(item => item.Item.Kind == EpicPlanItemKind.Slice && item.Status != EpicPlanSliceStatus.Superseded);
        var landed = items.Count(item => item.Status is EpicPlanSliceStatus.Landed or EpicPlanSliceStatus.Done);
        return $"{landed} of {slices} slices landed; " + (next.IsStalled ? next.Text : $"next: {next.Text}");
    }

    private static string Describe(EpicPlanItemStatus item) => $"{item.Item.Position}. {item.Subject} — {item.Render()}";
}
