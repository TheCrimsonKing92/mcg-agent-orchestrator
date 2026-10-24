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
        "tasks", "task", "finding", "status", "goals", "monitor-goal",
        "failure-clusters",
        "storage-health",
        "build-attribution",
        "intent-coverage",
        "remote-executors",
        "round-value",
        "board-simulate",
        "epic-outcome-proposals",
        "epic-plan",
        "architecture", "config", "model-outcomes",
        "backlog-list", "backlog-show", "backlog-similar", "goal-events", "timeline",
        "owner-digest", "context-usage", "next", "readiness"
    };

    internal static IReadOnlySet<string> QueryOnlyVerbs => QueryCommands;

    public static CliCommandCapability Classify(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return CliCommandCapability.Execution;

        return QueryCommands.Contains(args[0]) ||
               CliGateStatusJsonQueryCommand.IsGateStatusJsonCommand(args) ||
               CliAttentionShowJsonQueryCommand.IsAttentionShowJsonCommand(args) ||
               CliAttentionJsonQueryCommand.IsAttentionJsonCommand(args) ||
               args.Count == 3 && args[0] == "conductor" && args[1] == "setup" && args[2] == "--plan" ||
               CliCommandHelp.IsCommandSpecificHelp(args)
            ? CliCommandCapability.QueryOnly
            : CliCommandCapability.Execution;
    }

}
