using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class DirectivesAgentReadView
{
    internal static string Render(IReadOnlyList<OperatorDirective> directives, TimeProvider time)
    {
        var rows = directives.Select(row => new
        {
            row.Id, row.Kind, row.Text,
            recordedAt = RoundTrip(row.RecordedAt), row.Actor,
            actorKind = row.ActorKind.ToString(), row.Channel,
            expiresAt = row.ExpiresAt is { } at ? RoundTrip(at) : null,
            row.UntilHandoff, row.Expired
        }).ToArray();
        return AgentReadEnvelope.Serialize(AgentReadEnvelope.DirectivesSchema,
            AgentReadEnvelope.DirectivesSchemaVersion, new { directives = rows },
            new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
    }

    private static string RoundTrip(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
