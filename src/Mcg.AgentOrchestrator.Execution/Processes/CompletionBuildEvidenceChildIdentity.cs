using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum CompletionBuildChildLiveness { Dead, Live, Unknown }

public static class CompletionBuildEvidenceChildIdentity
{
    public static CompletionBuildChildLiveness Probe(CompletionBuildEvidenceOperation operation)
    {
        if (operation.ChildPid is not { } pid) return CompletionBuildChildLiveness.Unknown;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return CompletionBuildChildLiveness.Dead;
            if (!SpawnProcessIdentityReader.TryRead(process, out var identity))
                return CompletionBuildChildLiveness.Unknown;
            return identity.StartedAt == operation.ChildStartedAt &&
                string.Equals(identity.ImagePath, operation.ChildImagePath, StringComparison.OrdinalIgnoreCase)
                ? CompletionBuildChildLiveness.Live : CompletionBuildChildLiveness.Dead;
        }
        catch (ArgumentException) { return CompletionBuildChildLiveness.Dead; }
        catch { return CompletionBuildChildLiveness.Unknown; }
    }

    public static void Stop(CompletionBuildEvidenceOperation operation)
    {
        // No build can execute before the child identity is durable. Cancellation fences the
        // launch checkpoint; the launch owner must close/reap its unauthorized child on rejection.
        if (operation.ChildPid is null) return;
        if (Probe(operation) == CompletionBuildChildLiveness.Dead) return;
        if (operation.ChildPid is not { } pid)
            throw new InvalidOperationException("Completion build child identity is unavailable; cancellation is held.");
        using var process = Process.GetProcessById(pid);
        if (!SpawnProcessIdentityReader.TryRead(process, out var identity) ||
            identity.StartedAt != operation.ChildStartedAt ||
            !string.Equals(identity.ImagePath, operation.ChildImagePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Completion build child identity cannot authorize cancellation.");
        // Process holds the OS handle used for identity inspection and termination.
        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(5000))
            throw new InvalidOperationException("Completion build child did not acknowledge cancellation; cancellation is held.");
    }
}
