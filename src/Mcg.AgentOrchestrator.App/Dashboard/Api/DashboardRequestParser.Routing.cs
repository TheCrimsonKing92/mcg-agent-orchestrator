using Microsoft.AspNetCore.Http;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static class DashboardHttpRequestParser
{
public static GoalOperationPath ParseGoalOperationPath(string path)
{
    var remainder = path["/api/goals/".Length..].Trim('/');
    var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 2)
    {
        return new GoalOperationPath(
            Uri.UnescapeDataString(segments[0]),
            Uri.UnescapeDataString(segments[1]),
            null,
            null);
    }

    if (segments.Length == 4 && segments[1].Equals("tasks", StringComparison.OrdinalIgnoreCase))
    {
        return new GoalOperationPath(
            Uri.UnescapeDataString(segments[0]),
            "task-action",
            Uri.UnescapeDataString(segments[2]),
            Uri.UnescapeDataString(segments[3]));
    }

    throw new ArgumentException("Goal API path must be /api/goals/<goal-id-prefix>/tasks, /api/goals/<goal-id-prefix>/ask, /api/goals/<goal-id-prefix>/subscription-plan, /api/goals/<goal-id-prefix>/advance, /api/goals/<goal-id-prefix>/advance-subscription, /api/goals/<goal-id-prefix>/advance-until-blocked, /api/goals/<goal-id-prefix>/advance-subscription-until-blocked, /api/goals/<goal-id-prefix>/delegate, /api/goals/<goal-id-prefix>/start-subscription-ready, or /api/goals/<goal-id-prefix>/tasks/<task-id-prefix>/<operation>.");
}

public static TaskQuery ParseTaskQueryFromRequest(HttpRequest request)
{
    var tokens = new List<string>();
    AddQueryToken(tokens, "status", GetQueryValue(request, "status"));
    AddQueryToken(tokens, "role", GetQueryValue(request, "role"));
    AddQueryToken(tokens, "id", GetQueryValue(request, "id"));
    AddQueryToken(tokens, "evidence", GetQueryValue(request, "evidence"));
    AddQueryToken(tokens, "event", GetQueryValue(request, "event"));
    return DashboardApplicationServices.ParseTaskQuery(tokens);
}

public static string? GetQueryValue(HttpRequest request, string name)
{
    return request.Query.TryGetValue(name, out var value) ? value.FirstOrDefault() : null;
}

public static void AddQueryToken(List<string> tokens, string name, string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return;
    }

    tokens.Add(name);
    tokens.Add(value);
}
}
