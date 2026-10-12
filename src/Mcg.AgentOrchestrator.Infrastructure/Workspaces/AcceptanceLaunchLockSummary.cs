namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaunchLockSummary
{
    private const string LaunchLockSummaryPrefix = "LAUNCH_LOCK_SUMMARY ";

    internal static void Preserve(
        string output,
        string? stderr,
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
        var selectedOutput = string.IsNullOrEmpty(stderr) ? output : stderr;
        string? summary = null;
        using var reader = new StringReader(selectedOutput);
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
