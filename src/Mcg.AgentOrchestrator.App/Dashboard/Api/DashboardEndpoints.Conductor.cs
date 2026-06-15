using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardEndpoints
{
    private static async Task<IResult> HandleConductorTickAsync(HttpContext context)
    {
        try
        {
            using var reader = new System.IO.StreamReader(context.Request.Body, System.Text.Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest("empty body");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tick = root.TryGetProperty("tick", out var t) ? t.GetInt32() : 0;
            var advanced = root.TryGetProperty("advanced", out var a) ? a.GetInt32() : 0;
            var held = root.TryGetProperty("held", out var h) ? h.GetInt32() : 0;
            var escalated = root.TryGetProperty("escalated", out var e) ? e.GetInt32() : 0;
            var retried = root.TryGetProperty("retried", out var r) ? r.GetInt32() : 0;
            var done = root.TryGetProperty("done", out var d) ? d.GetInt32() : 0;
            var watchSleeping = root.TryGetProperty("watchSleeping", out var ws) && ws.GetBoolean();

            ConductorEventBus.RecordTick(tick, advanced, held, escalated, retried, done, watchSleeping);
            return Results.Ok(new { recorded = true, tick });
        }
        catch (JsonException ex)
        {
            return Results.BadRequest($"invalid JSON: {ex.Message}");
        }
    }
}
