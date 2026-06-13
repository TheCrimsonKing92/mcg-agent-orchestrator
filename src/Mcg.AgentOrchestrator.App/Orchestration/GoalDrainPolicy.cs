using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalDrainPolicy(
    string Name,
    int MaxSubscriptionStartsPerDrain,
    IReadOnlyList<string> AllowedRoles,
    IReadOnlyList<string> AllowedProviders,
    string LargePromptBehavior,
    bool RequireReadinessRiskConfirmation,
    bool RequireAcceptanceGate,
    IReadOnlyList<GoalDrainTimeWindow>? AllowedLocalTimeWindows = null)
{
    public static GoalDrainPolicy Default { get; } = new(
        "default",
        1,
        ["Planner", "Researcher", "Developer", "Tester", "Reviewer"],
        ["*"],
        "require-confirmation",
        RequireReadinessRiskConfirmation: true,
        RequireAcceptanceGate: true,
        AllowedLocalTimeWindows: []);

    public bool AllowsRole(string role) =>
        AllowedRoles.Any(item => item.Equals("*", StringComparison.OrdinalIgnoreCase) ||
                                 item.Equals(role, StringComparison.OrdinalIgnoreCase));

    public bool AllowsProvider(string? provider) =>
        AllowedProviders.Any(item => item.Equals("*", StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(provider) &&
         AllowedProviders.Any(item => item.Equals(provider, StringComparison.OrdinalIgnoreCase)));

    public GoalDrainScheduleDecision EvaluateSchedule(DateTimeOffset now)
    {
        var windows = AllowedLocalTimeWindows ?? [];
        if (windows.Count == 0)
        {
            return new GoalDrainScheduleDecision(true, "no local time window configured; drain may run now", null);
        }

        var localTime = TimeOnly.FromDateTime(now.LocalDateTime);
        foreach (var window in windows)
        {
            if (window.Contains(localTime))
            {
                return new GoalDrainScheduleDecision(
                    true,
                    $"inside allowed local drain window {window}",
                    null);
            }
        }

        var next = windows
            .Select(window => BuildNextStart(now.LocalDateTime, window.Start))
            .OrderBy(value => value)
            .First();
        return new GoalDrainScheduleDecision(
            false,
            $"outside allowed local drain windows ({string.Join(", ", windows)}); next window starts {next:yyyy-MM-dd HH:mm}",
            next);
    }

    private static DateTime BuildNextStart(DateTime localNow, TimeOnly start)
    {
        var candidate = localNow.Date + start.ToTimeSpan();
        return candidate > localNow ? candidate : candidate.AddDays(1);
    }
}

internal sealed record GoalDrainTimeWindow(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly time)
    {
        if (Start == End)
        {
            return true;
        }

        return Start < End
            ? time >= Start && time < End
            : time >= Start || time < End;
    }

    public override string ToString() => $"{Start:HH:mm}-{End:HH:mm}";
}

internal sealed record GoalDrainScheduleDecision(
    bool IsOpen,
    string Detail,
    DateTime? NextAllowedLocalStart);

internal static class GoalDrainPolicyStore
{
    public const string FileName = "drain-policy.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string PolicyPath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, FileName);

    public static GoalDrainPolicy LoadOrDefault(OrchestratorWorkspace workspace)
    {
        var path = PolicyPath(workspace);
        if (!File.Exists(path))
        {
            return GoalDrainPolicy.Default;
        }

        var policy = JsonSerializer.Deserialize<GoalDrainPolicyFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Drain policy file '{path}' is empty.");
        return new GoalDrainPolicy(
            string.IsNullOrWhiteSpace(policy.Name) ? GoalDrainPolicy.Default.Name : policy.Name.Trim(),
            Math.Max(0, policy.MaxSubscriptionStartsPerDrain ?? GoalDrainPolicy.Default.MaxSubscriptionStartsPerDrain),
            NormalizeList(policy.AllowedRoles, GoalDrainPolicy.Default.AllowedRoles),
            NormalizeList(policy.AllowedProviders, GoalDrainPolicy.Default.AllowedProviders),
            string.IsNullOrWhiteSpace(policy.LargePromptBehavior) ? GoalDrainPolicy.Default.LargePromptBehavior : policy.LargePromptBehavior.Trim(),
            policy.RequireReadinessRiskConfirmation ?? GoalDrainPolicy.Default.RequireReadinessRiskConfirmation,
            policy.RequireAcceptanceGate ?? GoalDrainPolicy.Default.RequireAcceptanceGate,
            NormalizeWindows(policy.AllowedLocalTimeWindows));
    }

    private static string[] NormalizeList(IReadOnlyList<string>? values, IReadOnlyList<string> fallback)
    {
        var normalized = values?
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized is { Length: > 0 } ? normalized : fallback.ToArray();
    }

    private static GoalDrainTimeWindow[] NormalizeWindows(IReadOnlyList<GoalDrainPolicyWindowFile>? windows)
    {
        if (windows is not { Count: > 0 })
        {
            return [];
        }

        return windows
            .Select(ParseWindow)
            .Where(window => window is not null)
            .Cast<GoalDrainTimeWindow>()
            .ToArray();
    }

    private static GoalDrainTimeWindow? ParseWindow(GoalDrainPolicyWindowFile window)
    {
        if (!TimeOnly.TryParse(window.Start, out var start) ||
            !TimeOnly.TryParse(window.End, out var end))
        {
            return null;
        }

        return new GoalDrainTimeWindow(start, end);
    }

    private sealed record GoalDrainPolicyFile(
        string? Name,
        int? MaxSubscriptionStartsPerDrain,
        IReadOnlyList<string>? AllowedRoles,
        IReadOnlyList<string>? AllowedProviders,
        string? LargePromptBehavior,
        bool? RequireReadinessRiskConfirmation,
        bool? RequireAcceptanceGate,
        IReadOnlyList<GoalDrainPolicyWindowFile>? AllowedLocalTimeWindows);

    private sealed record GoalDrainPolicyWindowFile(string? Start, string? End);
}
