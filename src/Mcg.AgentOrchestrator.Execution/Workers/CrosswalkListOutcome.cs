namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkListOutcome(string Status, IReadOnlyList<CrosswalkTestRef> Result, string Detail)
{
    internal CrosswalkExecutableIdentity? Executable { get; init; }
    internal int? ExitCode { get; init; }
    internal string StandardError { get; init; } = "";
    internal IReadOnlyList<CrosswalkInvocationDiagnostic> Diagnostics { get; init; } = [];
}
