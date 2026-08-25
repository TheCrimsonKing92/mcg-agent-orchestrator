namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliSingleGoalReportSelector
{
    internal static string? ResolveGoalPrefix(
        IReadOnlyList<string> parts,
        Func<IReadOnlyList<string>, string[], string?> getOptionalArgument) =>
        parts[0].ToLowerInvariant() switch
        {
            "next" => getOptionalArgument(parts, ["--full"]),
            "status" => getOptionalArgument(parts, ["--tasks-only"]),
            "goal-changes" => getOptionalArgument(parts, ["--role", "--task", "--committed", "--working", "--all", "--flat", "--json"]),
            "failure-triage" => getOptionalArgument(parts, ["--autonomy", "--policy"]),
            "supervisor" => getOptionalArgument(parts, ["--apply-safe", "--autonomy", "--policy"]),
            "build-lease-cleanup" => getOptionalArgument(parts, ["--confirm-build-lease-cleanup"]),
            _ => parts.Count > 1 ? parts[1] : null
        };
}
