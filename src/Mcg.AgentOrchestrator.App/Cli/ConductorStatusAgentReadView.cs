using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusAgentReadView
{
    internal static string Render(ConductorStatusSnapshot snapshot, TimeProvider time) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.ConductorStatusSchema,
            AgentReadEnvelope.ConductorStatusSchemaVersion, new
            {
                conductor = new { state = snapshot.ConductorState, pid = snapshot.OwnerPid },
                since = new
                {
                    kind = snapshot.Since.Kind, at = RoundTrip(snapshot.Since.At), reason = snapshot.Since.Reason,
                    previous = snapshot.Since.Previous is { } previous
                        ? new { at = RoundTrip(previous.At), reason = previous.Reason } : null
                },
                supervisorBuild = snapshot.SupervisorBuild,
                generation = new { state = snapshot.Generation.State, build = snapshot.Generation.Build, main = snapshot.Generation.Main },
                latestTick = RoundTrip(snapshot.LatestTick),
                goals = new
                {
                    available = snapshot.Goals.Available,
                    nonTerminal = snapshot.Goals.NonTerminal?.Select(goal => new { id = goal.Id, status = goal.Status, role = goal.Role }).ToArray(),
                    pendingClarifications = snapshot.Goals.PendingClarifications,
                    humanWaits = snapshot.Goals.HumanWaits
                },
                mostRecentLanding = snapshot.MostRecentLanding is { } landing
                    ? new { goal = landing.Goal, at = RoundTrip(landing.At) } : null,
                workerAdoption = snapshot.WorkerAdoption is { } adoption
                    ? new { summary = adoption.Summary, deferredIdentityUnproven = adoption.DeferredIdentityUnproven } : null,
                pendingIntents = new
                {
                    state = snapshot.PendingIntents.State,
                    reason = snapshot.PendingIntents.Reason,
                    goals = snapshot.PendingIntents.Goals.Select(goal => new
                    {
                        goal = goal.Goal, intents = goal.Intents, latest = RoundTrip(goal.Latest)
                    }).ToArray()
                }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);

    private static string? RoundTrip(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);
}
