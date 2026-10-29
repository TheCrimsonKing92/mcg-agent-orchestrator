using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleHoldPhrase
{
    private static readonly IReadOnlyDictionary<string, string> Typed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["criterion-evidence-hold"] = "waiting for criterion evidence",
        ["acceptance-apparatus-hold"] = "gate apparatus problem: operator retry needed",
        ["environmental-apparatus-hold"] = "gate apparatus problem: operator retry needed",
        ["evidence-mutation-lease-hold"] = "waiting for another evidence run",
        ["replacement-evidence-mutation-lease"] = "waiting for another evidence run",
        ["acceptance-width-admission"] = "waiting for a gate slot",
        ["gate-start-infrastructure-deferred"] = "gate start deferred",
        ["gate-start-build-slots-busy"] = "waiting for a build slot",
        ["gate-start-build-lock-blocked"] = "waiting for the build lock",
        ["attempt-running-in-background"] = "focused tests running"
    };

    internal static string For(string kind, int? queuePosition = null)
    {
        var slug = kind[(kind.LastIndexOf('/') + 1)..];
        if (Typed.TryGetValue(slug, out var phrase)) return phrase;
        if (kind.StartsWith("untyped/", StringComparison.Ordinal) &&
            Enum.TryParse<ConductorHoldOwner>(slug, out var owner) && Enum.IsDefined(owner))
            return ForOwner(owner, queuePosition);
        return Plain(slug);
    }

    internal static string ForOwner(ConductorHoldOwner owner, int? queuePosition = null) => owner switch
    {
        ConductorHoldOwner.AcceptanceQueue => "waiting for gate" +
            (queuePosition is null ? "" : $" ({Ordinal(queuePosition.Value)} in queue)"),
        ConductorHoldOwner.BackgroundAttempt => "focused tests running",
        ConductorHoldOwner.WorkerCapacity => "waiting for a free worker",
        ConductorHoldOwner.DurableOutbox => "waiting for spec refinement",
        _ => Plain(owner.ToString())
    };

    internal static string Ordinal(int position) => position + (position % 100 is 11 or 12 or 13 ? "th" :
        (position % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static string Plain(string slug) => Regex.Replace(slug.Replace('-', ' '), @"(?<=[a-z0-9])(?=[A-Z])", " ")
        .ToLowerInvariant();
}
