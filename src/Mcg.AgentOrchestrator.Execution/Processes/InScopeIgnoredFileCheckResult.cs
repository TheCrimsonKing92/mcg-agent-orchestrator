namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record InScopeIgnoredFileCheckResult(bool FailsRound, string Diagnostic);
