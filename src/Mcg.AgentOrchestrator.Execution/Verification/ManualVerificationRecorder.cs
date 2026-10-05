using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ManualVerificationRecorder
{
    public static TaskVerificationRecord Create(
        bool passed,
        string note,
        string workingDirectory,
        DateTimeOffset completedAt)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Manual verification note cannot be empty.", nameof(note));
        }

        var standardOutput = passed ? note.Trim() : string.Empty;
        var standardError = passed ? string.Empty : note.Trim();
        return new TaskVerificationRecord(
            passed ? "manual-verification passed" : "manual-verification failed",
            workingDirectory,
            passed ? 0 : 1,
            standardOutput,
            standardError,
            completedAt,
            FullStandardOutput: standardOutput,
            FullStandardError: standardError);
    }
}
