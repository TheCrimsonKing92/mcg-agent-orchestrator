using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductorStatusReader
{
    internal static void Print(ConductorStatusSnapshot snapshot, TextWriter output)
    {
        var owner = snapshot.OwnerPid;
        output.WriteLine(owner is null ? $"Conductor: {snapshot.ConductorState}"
            : $"Conductor: {snapshot.ConductorState} (pid {owner})");
        output.WriteLine(snapshot.Since.Kind switch
        {
            "host-restart" => $"stopped since host restart at {snapshot.Since.At:O}",
            "stopped" => $"Stopped since {snapshot.Since.At:O}; reason={snapshot.Since.Reason}",
            "running" => $"Running since {snapshot.Since.At:O}",
            "starting" => $"Starting since {snapshot.Since.At:O}; stage={snapshot.Since.Reason}",
            _ => "Since: unavailable"
        });
        if (snapshot.Since.Previous is { } previous)
            output.WriteLine($"Previous stop: {previous.At:O}; reason={previous.Reason} (earlier generation)");
        output.WriteLine($"Supervisor build: {snapshot.SupervisorBuild ?? "unavailable"}");
        output.WriteLine(snapshot.Generation.State switch
        {
            "no-build" => "Generation: unavailable (no supervisor build recorded)",
            "main-unavailable" => "Generation: main unavailable",
            "current" => $"Generation: current with main {snapshot.Generation.Main}",
            _ => $"Generation: differs from main (running {snapshot.Generation.Build}, main {snapshot.Generation.Main})"
        });
        output.WriteLine($"Latest tick: {snapshot.LatestTick?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable"}");
        if (!snapshot.Goals.Available)
        {
            output.WriteLine("Goals: unavailable (state database missing)");
            output.WriteLine("Pending clarifications: unavailable; human waits: unavailable");
        }
        else
        {
            output.WriteLine($"Non-terminal goals: {snapshot.Goals.NonTerminal!.Count}");
            foreach (var goal in snapshot.Goals.NonTerminal)
                output.WriteLine($"  {goal.Id} | {goal.Status} | role={goal.Role}");
            output.WriteLine($"Pending clarifications: {snapshot.Goals.PendingClarifications}; human waits: {snapshot.Goals.HumanWaits}");
        }
        output.WriteLine(snapshot.MostRecentLanding is { } landing
            ? $"Most recent landing: {landing.Goal} at {landing.At:O}"
            : "Most recent landing: unavailable");
        output.WriteLine(snapshot.WorkerAdoption is { } adoption
            ? $"Worker adoption: {adoption.Summary} deferred-identity-unproven={adoption.DeferredIdentityUnproven}"
            : "Worker adoption: unavailable");
        var intents = snapshot.PendingIntents;
        output.WriteLine(intents.State switch
        {
            "none" => "Pending operator intents: none",
            "pending" => $"Pending operator intents: {intents.Goals.Count} goal(s)",
            _ => intents.Reason is null ? "Pending operator intents: unavailable"
                : $"Pending operator intents: unavailable ({intents.Reason})"
        });
        foreach (var goal in intents.Goals)
        {
            var latest = goal.Latest?.ToString("O", CultureInfo.InvariantCulture) ?? "none";
            var hint = owner is null ? $" | apply with: conductor apply-intents {goal.Goal[..Math.Min(8, goal.Goal.Length)]}" : "";
            output.WriteLine($"  {goal.Goal} | intents={goal.Intents} | latest={latest}{hint}");
        }
    }
}
