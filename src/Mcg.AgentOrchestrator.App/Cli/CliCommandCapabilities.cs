namespace Mcg.AgentOrchestrator.App.Cli;

internal enum CliCommandCapability
{
    QueryOnly,
    Execution
}

internal static class CliCommandCapabilities
{
    private static readonly HashSet<string> QueryCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasks", "task", "status", "goals", "monitor-goal",
        "failure-clusters",
        "architecture", "config", "agent-list", "worker-profile-list", "model-outcomes",
        "backlog-list", "backlog-show", "backlog-depends", "backlog-similar", "goal-events", "timeline",
        "owner-digest", "context-usage", "next"
    };

    public static CliCommandCapability Classify(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return CliCommandCapability.Execution;

        return QueryCommands.Contains(args[0]) ||
               CliCommandHelp.IsCommandSpecificHelp(args)
            ? CliCommandCapability.QueryOnly
            : CliCommandCapability.Execution;
    }

}
