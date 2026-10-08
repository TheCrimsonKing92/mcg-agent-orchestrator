namespace Mcg.AgentOrchestrator.App.Cli;

// Commands whose writer fallback can operate on a single goal snapshot.
internal static class CliSingleGoalReportCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "status" or
            "monitor" or
            "readiness" or
            "readiness-repair" or
            "goal-recovery" or
            "dogfood-eval" or
            "failure-triage" or
            "retention-plan" or
            "evidence" or
            "goal-changes" or
            "stages" or
            "gates" or
            "verify-needed" or
            "input-needed" or
            "goal-diagnostics" or
            "next" or
            "subscription-plan" or
            "supervisor" or
            "build-lease-cleanup" => true,
            _ => false
        };
    }
}
