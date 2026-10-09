using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class DirtyDispatchRecoveryTextView
{
public static string BuildDirtyDispatchRecoverySuggestedCommand(int taskNumber, DirtyDispatchRecovery recovery)
{
    return $"task {taskNumber}";
}

public static string FormatDirtyDispatchRecoverySummary(DirtyDispatchRecovery recovery)
{
    var changedFiles = string.Join(", ", recovery.ChangedFiles);
    var evidence = recovery.HasUsefulVerification
        ? string.Join("; ", recovery.VerificationEvidence)
        : "none found; rerun focused verification before committing";
    return $"{recovery.Label}: changed files [{changedFiles}]; verification evidence: {evidence}";
}

public static void PrintDirtyDispatchRecovery(int taskNumber, DirtyDispatchRecovery recovery)
{
    Console.WriteLine($"Dispatch recovery: {recovery.Label}");
    Console.WriteLine($"Changed files: {string.Join(", ", recovery.ChangedFiles)}");
    Console.WriteLine(recovery.HasUsefulVerification
        ? $"Verification evidence: {string.Join("; ", recovery.VerificationEvidence)}"
        : "Verification evidence: none found; rerun focused verification before committing.");
    Console.WriteLine("Safe recovery workflow:");
    Console.WriteLine($"  1. {BuildGitCommand(recovery.WorkingDirectory, "status --short")}");
    Console.WriteLine($"  2. {BuildGitCommand(recovery.WorkingDirectory, "diff --stat")}");
    Console.WriteLine("  3. rerun the focused/full verification command in the worktree");
    Console.WriteLine($"  4. {BuildGitCommand(recovery.WorkingDirectory, "add -A")}");
    Console.WriteLine($"  5. {BuildGitCommand(recovery.WorkingDirectory, $"commit -m {QuotePowerShellArgument($"Recover {recovery.Label} task {taskNumber}")}")}");
    Console.WriteLine($"  6. verify-manual {taskNumber} passed {QuotePowerShellArgument($"Reviewed {recovery.Label} dispatch diff, reran verification, committed <sha>.")}");
}

private static string BuildGitCommand(string workingDirectory, string arguments)
{
    return $"git -C {QuotePowerShellArgument(workingDirectory)} {arguments}";
}

private static string QuotePowerShellArgument(string value)
{
    return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
}
