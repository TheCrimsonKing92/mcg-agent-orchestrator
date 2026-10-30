namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ForgeProcessResult(bool Started, int ExitCode, string StandardOutput,
    string StandardError, bool TimedOut = false);
