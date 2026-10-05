namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IRepositoryIntegrityProbe
{
    RepositoryIntegrityReading Read();
}

internal sealed record RepositoryIntegrityReading(IReadOnlyList<string>? LowPaths, string? UnavailableReason)
{
    internal bool IsAvailable => LowPaths is not null && UnavailableReason is null;
    internal static RepositoryIntegrityReading Available(IReadOnlyList<string> lowPaths) => new(lowPaths, null);
    internal static RepositoryIntegrityReading Unavailable(string reason) => new(null, reason);
}
