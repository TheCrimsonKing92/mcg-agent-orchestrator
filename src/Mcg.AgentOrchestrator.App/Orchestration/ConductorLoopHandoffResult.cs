namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record ConductorLoopHandoffResult(
    bool Started,
    int? ProcessId,
    string? StdoutPath,
    string? StderrPath,
    string? Reason,
    bool Failed = false,
    string? VerificationOutcome = null,
    bool RollbackSucceeded = true)
{
    public static ConductorLoopHandoffResult StartedProcess(
        int processId,
        string stdoutPath,
        string stderrPath,
        string? verificationOutcome = null) =>
        new(true, processId, stdoutPath, stderrPath, null, Failed: false, verificationOutcome);

    public static ConductorLoopHandoffResult Skipped(string reason) =>
        new(false, null, null, null, reason);

    public static ConductorLoopHandoffResult FailedStart(
        string reason,
        string? stdoutPath,
        string? stderrPath,
        string? verificationOutcome = null,
        int? processId = null,
        bool rollbackSucceeded = false) =>
        new(false, processId, stdoutPath, stderrPath, reason, Failed: true, verificationOutcome, rollbackSucceeded);
}
