using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliHandledVerbRegistry
{
    // The suggestion catalog is not exhaustive. Include route aliases and nested
    // handler labels conservatively so existing commands keep their startup path.
    private static readonly HashSet<string> AdditionalVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--goal",
        "abandon",
        "add",
        "add-task",
        "agent",
        "answer",
        "architecture",
        "ask",
        "ask-goal",
        "backlog-intake",
        "cancel",
        "create",
        "dispatch",
        "escape",
        "goal",
        "goal-delivery-retry",
        GoalRefinementWorkCoordinator.CommandName,
        "goal-replace",
        "help",
        "list",
        "merge",
        "mute",
        "operator-commands",
        "park",
        "pending",
        "policy",
        "profiles",
        "progress",
        "rebase",
        "record",
        "recover",
        "remove",
        "replay",
        "set",
        "show",
        "simple-goal",
        "stop",
        "supervisor",
        "templates",
        "tenant",
        "test",
        "verify",
        "worker-dispatch",
        "worker-profile"
    };

    internal static bool IsHandled(string verb) =>
        CliArgumentParser.IsRecognizedCommand(verb) || AdditionalVerbs.Contains(verb);
}
