namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkDetectOutcome(string Status, CrosswalkDetectResult? Result, string Detail)
{
    internal CrosswalkExecutableIdentity? Executable { get; init; }
    internal const string Detected = "detected";
    internal const string NotConfigured = "not-configured";
    internal const string Missing = "missing";
    internal const string TimedOut = "timeout";
    internal const string StartFailed = "start-failed";
    internal const string ExitNonZero = "exit-nonzero";
    internal const string InvalidJson = "invalid-json";
}
