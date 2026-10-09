using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Presentation only: the reader and next-step projection own all plan state.
internal static class OwnerConsoleEpicPlanFormatter
{
    internal static IReadOnlyList<string> Lines(OwnerConsoleEpicViewModel.Detail detail, int width)
    {
        var view = detail.Plan;
        var items = view?.Items ?? [];
        var next = EpicPlanNextStep.Compute(items);
        var lines = new List<string>();
        if (next.IsStalled) lines.Add(EpicPlanNextStep.StalledText);
        lines.Add($"Bar: {view?.Plan.Bar ?? "(no bar)"}");
        if (items.Count == 0) lines.Add("(no plan)");
        foreach (var item in items.OrderBy(item => item.Item.Position))
            lines.Add($"{item.Item.Position}. {item.Subject} — {item.Render()}");

        lines.Add("Decisions (newest first):");
        var decisions = view?.Plan.Decisions ?? [];
        if (decisions.Count == 0) lines.Add("  (none)");
        foreach (var decision in decisions.OrderByDescending(decision => decision.DecidedAt)
                     .ThenByDescending(decision => decision.Id).Take(5))
        {
            var author = string.IsNullOrWhiteSpace(decision.DecidedBy) ? "" : $" by {decision.DecidedBy}";
            lines.Add($"  {decision.DecidedAt.ToLocalTime():yyyy-MM-dd HH:mm}{author}: {decision.Text}");
        }
        if (decisions.Count > 5)
            lines.Add($"  {decisions.Count - 5} older decisions: see epic-plan {Short(detail.Epic.Id)}");
        if (items.Count > 0 && !next.IsStalled) lines.Add($"Next step: {next.Text}");

        lines.Add("Not in plan:");
        var unplanned = view?.UnplannedMembers ?? [];
        if (unplanned.Count == 0) lines.Add("  (none)");
        foreach (var member in unplanned)
            lines.Add($"  {(member.Kind == PortfolioMemberKind.Goal ? "goal" : "backlog item")} {Short(member.MemberId)}");
        return lines.SelectMany(line => OwnerConsoleEpicFormatter.Wrap(line, width)).ToArray();
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
