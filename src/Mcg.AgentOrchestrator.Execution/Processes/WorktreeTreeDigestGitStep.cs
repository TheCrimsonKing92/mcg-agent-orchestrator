namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record WorktreeTreeDigestGitStep(
    int ExitCode, string Output, string? StandardErrorFirstLine, bool TimedOut, string? TimeoutKind = null);
