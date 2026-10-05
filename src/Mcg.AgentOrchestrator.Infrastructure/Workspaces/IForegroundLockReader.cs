namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IForegroundLockReader
{
    ForegroundLockReading Read();
}

internal sealed record ForegroundLockReading(uint? TimeoutMs, int? Build, string? UnavailableReason)
{
    internal bool IsAvailable => TimeoutMs.HasValue && Build.HasValue && UnavailableReason is null;
    internal static ForegroundLockReading Available(uint timeoutMs, int build) => new(timeoutMs, build, null);
    internal static ForegroundLockReading Unavailable(string reason) => new(null, null, reason);
}
