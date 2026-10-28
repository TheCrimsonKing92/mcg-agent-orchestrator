namespace Mcg.AgentOrchestrator.Infrastructure;

// Invocation facts are independent of the normalized test verdict.
public enum CrosswalkDiagnosticCategory
{
    NotConfigured, MissingBinary, Timeout, Cancelled, UnsupportedAdapter, NonzeroExit,
    StartFailed, CaptureLimited, InvalidJson, MalformedResults, InvalidProject,
    InvalidSelection, EmptyDiscovery, MissingCoverage, NoExecution, TestFailures, ReceiptFailure
}

public sealed record CrosswalkInvocationDiagnostic(CrosswalkDiagnosticCategory Category, string Detail);
