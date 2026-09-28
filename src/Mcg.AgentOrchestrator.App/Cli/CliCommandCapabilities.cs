namespace Mcg.AgentOrchestrator.App.Cli;

internal enum CliCommandCapability
{
    QueryOnly,
    Execution,
    DashboardHost
}

internal static class CliCommandCapabilities
{
    private static readonly HashSet<string> DashboardCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "serve-dashboard", "hosted-dashboard", "simple-hosted-dashboard", "open-dashboard", "prototype-ui"
    };

    private static readonly HashSet<string> QueryCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasks", "task", "status", "goals", "monitor-goal",
        "architecture", "config", "agent-list", "worker-profile-list", "model-outcomes",
        "backlog-list", "backlog-show", "backlog-depends", "backlog-similar", "goal-events", "timeline",
        "dashboard", "transcript", "owner-digest", "context-usage", "next"
    };

    public static CliCommandCapability Classify(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return CliCommandCapability.Execution;

        if (DashboardCommands.Contains(args[0]) || IsDashboardModeCommand(args))
            return CliCommandCapability.DashboardHost;

        return QueryCommands.Contains(args[0]) ||
               CliCommandHelp.IsCommandSpecificHelp(args)
            ? CliCommandCapability.QueryOnly
            : CliCommandCapability.Execution;
    }

    private static bool IsDashboardModeCommand(IReadOnlyList<string> args) =>
        args[0].Equals("dashboard", StringComparison.OrdinalIgnoreCase) &&
        args.Skip(1).Any(arg => arg.Equals("--mode", StringComparison.OrdinalIgnoreCase));
}
