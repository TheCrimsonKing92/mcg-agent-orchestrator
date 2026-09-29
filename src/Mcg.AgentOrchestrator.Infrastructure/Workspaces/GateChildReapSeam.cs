using System.ComponentModel;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IGateChildReapSeam
{
    IRecordedGateChild? TryOpen(int processId, DateTimeOffset recordedStartedAt);
}

internal interface IRecordedGateChild : IDisposable
{
    bool Kill();
    bool WaitForExit(TimeSpan budget);
}

internal sealed class DefaultGateChildReapSeam : IGateChildReapSeam
{
    internal static readonly DefaultGateChildReapSeam Instance = new();

    public IRecordedGateChild? TryOpen(int processId, DateTimeOffset recordedStartedAt)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited || !IsRecordedChild(process.StartTime.ToUniversalTime(), recordedStartedAt))
            {
                process.Dispose();
                return null;
            }

            return new RecordedGateChild(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            return null;
        }
    }

    internal static bool IsRecordedChild(DateTime processStartUtc, DateTimeOffset recordedStartedAt) =>
        recordedStartedAt != default && processStartUtc <= recordedStartedAt.UtcDateTime;

    private sealed class RecordedGateChild(Process process) : IRecordedGateChild
    {
        public bool Kill()
        {
            try
            {
                return process.HasExited ||
                    WorkerProcessJobs.TryKillRecordedOwnedChildAndWait(process.Id, TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        public bool WaitForExit(TimeSpan budget)
        {
            try
            {
                return process.WaitForExit(budget);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }

        public void Dispose() => process.Dispose();
    }
}

internal enum GateChildReapOutcomeKind { NotApplicable, Reaped, StillAlive }

internal sealed record GateChildReapOutcome(int? ChildPid, GateChildReapOutcomeKind Kind, string Reason);

internal sealed class GateChildStillAliveException(
    string progressLine,
    string artifactsPath,
    string? leaseId) : Exception(progressLine)
{
    internal string ProgressLine { get; } = progressLine;
    internal string ArtifactsPath { get; } = artifactsPath;
    internal string? LeaseId { get; } = leaseId;
}
