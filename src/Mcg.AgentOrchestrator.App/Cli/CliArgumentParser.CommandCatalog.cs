namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
private static bool IsSimpleCommand(string command)
{
    return command.Equals("status", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("doctor", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("state-compact", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("provider-smoke", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("prototype", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("transcript", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("monitor", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("monitor-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("readiness", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("goal-recovery", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("dogfood-eval", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("failure-triage", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("retention-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("build-lease-cleanup", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("acceptance-queue", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("drain-goals", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("evidence", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("stages", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("gates", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("verify-needed", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("input-needed", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("operator-inbox", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("operator-inbox-ack", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("next", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("subscription-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("advance", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("advance-subscription", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("run-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("lifecycle-simple-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("lifecycle-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("goal-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("intent-template", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("delegate", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("abandon-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("rollback-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("agents", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("agent-add", StringComparison.OrdinalIgnoreCase) ||
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
        command.Equals("cross-goal-start-plan", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("start-subscription-ready-goals", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("start-subscription-ready", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("run", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("api-run", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("retry", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("re-delegate", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("redelegate", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("note", StringComparison.OrdinalIgnoreCase) ||
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
        command.Equals("task", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("model-outcomes", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("loop-health", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("provenance", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("accept", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("config", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("land", StringComparison.OrdinalIgnoreCase);
}
}
