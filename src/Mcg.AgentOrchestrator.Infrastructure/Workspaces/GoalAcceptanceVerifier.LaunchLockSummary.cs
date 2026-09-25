namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private const string LaunchLockSummaryPrefix = "LAUNCH_LOCK_SUMMARY ";

    private static void PreserveLaunchLockSummary(
        CommandResult result,
        string trxPath,
        string? attemptPrefix)
    {
        if (string.IsNullOrWhiteSpace(attemptPrefix))
        {
            return;
        }

        var attemptDirectory = Path.GetDirectoryName(attemptPrefix)
            ?? throw new InvalidOperationException("Acceptance attempt has no artifact directory.");
        var summaryPath = Path.Combine(
            attemptDirectory,
            Path.GetFileNameWithoutExtension(trxPath) + ".launch-lock-summary.txt");
        var output = string.IsNullOrEmpty(result.Stderr) ? result.Output : result.Stderr;
        string? summary = null;
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith(LaunchLockSummaryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // A nested test host can finish before the lane host. Its teardown line appears first.
            summary = line;
        }

        if (summary is null)
        {
            // An invocation may reuse a lane artifact name after a failed test-host start.
            if (File.Exists(summaryPath))
            {
                File.Delete(summaryPath);
            }
            return;
        }

        Directory.CreateDirectory(attemptDirectory);
        File.WriteAllText(summaryPath, summary + Environment.NewLine);
    }
}
