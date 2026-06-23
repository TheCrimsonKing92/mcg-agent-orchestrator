using Mcg.AgentOrchestrator.Core;

public sealed class DispatchEmptyOutputFlakeTests
{
    // Reproduces 0d5a51b7: a buffered claude-cli worker exits 0 and writes a complete result
    // (incl. WORKER_RESULT) to its out.log, but the in-memory StandardOutput string captured at
    // reconcile is empty (the worker streamed nothing until exit). The dispatch must NOT be
    // classified as a transient empty-output flake when the out.log file on disk is populated.
    [Xunit.Fact(DisplayName = "IsTransientEmptyOutputDispatchFlake_false_for_exit0_with_populated_outlog_and_empty_inmemory_stdout")]
    public void ReturnsFalseForExit0WithPopulatedOutLogAndEmptyInMemoryStdout()
    {
        var outLog = Path.Combine(Path.GetTempPath(), $"mcg-emptyflake-{Guid.NewGuid():N}.out.log");
        var workerOutput =
            "The plan is complete.\n\n" + new string('x', 1600) + "\n\n" +
            "WORKER_RESULT:\nfiles: plan.md\ncommands: none\ntests: not-run\ncommit: none\nblockers: none\nEND_WORKER_RESULT\n";
        File.WriteAllText(outLog, workerOutput);
        try
        {
            var verification = new TaskVerificationRecord(
                "claude -p",
                "C:\\repo",
                0,
                string.Empty,
                string.Empty,
                DateTimeOffset.UtcNow,
                StandardOutputPath: outLog);

            var isFlake = DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(verification);

            Xunit.Assert.False(isFlake);
        }
        finally
        {
            File.Delete(outLog);
        }
    }

    // Control: a genuinely empty exit-0 dispatch (no path, no output) is still a flake.
    [Xunit.Fact(DisplayName = "IsTransientEmptyOutputDispatchFlake_true_for_exit0_with_no_output_at_all")]
    public void ReturnsTrueForExit0WithNoOutputAtAll()
    {
        var verification = new TaskVerificationRecord(
            "claude -p",
            "C:\\repo",
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow);

        var isFlake = DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(verification);

        Xunit.Assert.True(isFlake);
    }
}
