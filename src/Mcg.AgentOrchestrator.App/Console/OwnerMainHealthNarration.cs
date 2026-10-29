using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerMainHealthNarration
{
    internal static bool TryNarrate(OwnerConductEvent item, Func<string?, string> name,
        Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence,
        IReadOnlyList<string> recentLanding, IReadOnlyList<string> recentLandingPrefixes,
        out OwnerAcceptanceNarration.Narrated narrated)
    {
        narrated = null!;
        switch (item.EventKind)
        {
            case "canary-gate":
                if (Field(item, "result") != "failed") return false;
                var checks = evidence?.Invoke(item)?.Checks;
                var check = Words(Field(item, "tests") ?? checks?.FirstOrDefault(), "the full test run on main");
                var landingNames = GoalList("Main broke after landing ", recentLanding);
                narrated = new(landingNames.Text + (recentLanding.Count == 0 ? "recent work" : "") + $": {check}",
                    "A check of main failed after the work landed.", "Further landings wait while the failure is investigated.",
                    "Yes. Review the failure and decide how to repair main.", landingNames.Spans,
                    recentLandingPrefixes.Count > 1 ? recentLandingPrefixes : null);
                return true;
            case "state-log-divergence":
                var prefix = Prefix(item.GoalId);
                var title = name(item.GoalId);
                narrated = new(prefix + " history needs review: " + title, "Some recorded progress is missing or repeated.",
                    "The operator can check the goal's history.", "No. You can let the conductor continue.",
                    SingleTitle(title, prefix.Length + " history needs review: ".Length));
                return true;
            default: return false;
        }
    }
}
