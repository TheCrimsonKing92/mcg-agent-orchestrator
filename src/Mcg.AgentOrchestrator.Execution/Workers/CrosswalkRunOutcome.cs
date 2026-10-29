namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkRunOutcome(string Status, IReadOnlyList<TestResult> Result, string Detail)
{
    internal CrosswalkExecutableIdentity? Executable { get; init; }
    internal int? ExitCode { get; init; }
    internal string StandardError { get; init; } = "";
    internal IReadOnlyList<CrosswalkInvocationDiagnostic> Diagnostics { get; init; } = [];
}
