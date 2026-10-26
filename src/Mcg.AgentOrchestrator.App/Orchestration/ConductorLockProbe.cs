using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorLockProbe
{
    int? ActiveOwnerPid(OrchestratorWorkspace workspace);

    ConductorLockOwnership? ActiveOwnership(OrchestratorWorkspace workspace) =>
        ActiveOwnerPid(workspace) is { } pid ? new(pid, null, null) : null;
}

internal sealed record ConductorLockOwnership(int Pid, DateTimeOffset? AcquiredAt, DateTimeOffset? ProcessStartedAt);

internal sealed class SystemConductorLockProbe(IConductLockPidProbe? pidProbe = null) : IConductorLockProbe
{
    public int? ActiveOwnerPid(OrchestratorWorkspace workspace) => ActiveOwnership(workspace)?.Pid;

    public ConductorLockOwnership? ActiveOwnership(OrchestratorWorkspace workspace)
    {
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory, pidProbe)) return null;
        var path = Path.Combine(workspace.OrchestratorDirectory, "conduct-loop.lock");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var firstLine = reader.ReadLine();
        if (!int.TryParse(firstLine, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            throw new InvalidOperationException($"Invalid conductor lock: {path}");
        return new(pid, ParseInstant(reader.ReadLine()), ParseInstant(reader.ReadLine()));
    }

    private static DateTimeOffset? ParseInstant(string? text) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at.ToUniversalTime() : null;
}
