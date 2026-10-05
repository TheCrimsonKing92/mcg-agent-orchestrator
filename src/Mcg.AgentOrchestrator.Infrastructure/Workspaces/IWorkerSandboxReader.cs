namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IWorkerSandboxReader
{
    WorkerSandboxReading Read();
}

internal sealed record WorkerSandboxReading(bool? Enabled, string? RawValue, string? UnavailableReason)
{
    internal bool IsAvailable => Enabled.HasValue && UnavailableReason is null;
    internal static WorkerSandboxReading Available(bool enabled, string? rawValue) => new(enabled, rawValue, null);
    internal static WorkerSandboxReading Unavailable(string reason) => new(null, null, reason);
}
