namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkListOutcome(string Status, IReadOnlyList<CrosswalkTestRef> Result, string Detail)
{
    internal int? ExitCode { get; init; }
    internal string StandardError { get; init; } = "";
    internal IReadOnlyList<CrosswalkInvocationDiagnostic> Diagnostics { get; init; } = [];
}
