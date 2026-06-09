namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
private static bool IsSimpleCommand(string command)
{
    return command.Equals("status", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("doctor", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("provider-smoke", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("prototype", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("transcript", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("monitor", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("evidence", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("stages", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("gates", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("verify-needed", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("input-needed", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("next", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("subscription-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("advance", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("advance-subscription", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("delegate", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("agents", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("timeline", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("task-timeline", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profiles", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-export", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-import", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("worker-profile-check", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("profile-dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("profile-dispatch-ready", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("subscription-dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("subscription-dispatch-ready", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("start-subscription-ready", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("run", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("api-run", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("verification-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("brief", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("execute-dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("start-dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("start-dispatches", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("refresh-dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("refresh-dispatches", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-dispatch", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("verify-manual", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("verifications", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("tasks", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("task", StringComparison.OrdinalIgnoreCase);
}
}
