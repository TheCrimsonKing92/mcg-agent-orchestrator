using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Presence is a fresh observation of the lease owner's process identity, never a cached launch claim.
internal static class SupervisedSelfRelaunchGuard
{
    internal sealed record Presence(string Kind, string Reason);
    private sealed record Lease(ConductorSupervisorProcessIdentity? Process);

    internal static Presence Probe(string continuityDirectory, IConductLockPidProbe probe,
        Action<int>? leaseRetryDelay = null)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(Path.Combine(continuityDirectory, "conduct-supervisor.lock"),
                        FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length == 0) return new("absent", "lease-empty");
                    var owner = JsonSerializer.Deserialize<Lease>(stream)?.Process;
                    if (owner is null || owner.ProcessId <= 0 || owner.StartedAt == default)
                        return new("unknown", "invalid-json");
                    return probe.IsSameProcess(owner.ProcessId, owner.StartedAt)
                        ? new("present", "owner-running") : new("absent", "owner-not-running");
                }
                catch (IOException ex) when (attempt < SystemConductorSupervisorHandoffSeam.LeaseOpenAttempts &&
                    SystemConductorSupervisorHandoffSeam.IsLeaseContention(ex))
                {
                    SystemConductorSupervisorHandoffSeam.DelayLeaseRetry(attempt, leaseRetryDelay);
                }
            }
        }
        catch (FileNotFoundException) { return new("absent", "lease-missing"); }
        catch (DirectoryNotFoundException) { return new("absent", "lease-missing"); }
        catch (JsonException) { return new("unknown", "invalid-json"); }
        catch { return new("unknown", "lease-unreadable"); }
    }

    internal static Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> Create(
        string continuityDirectory,
        Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> delegated,
        Action<string, string, string> raiseAttention,
        IConductLockPidProbe probe,
        Action<int>? leaseRetryDelay = null) => request =>
    {
        var presence = Probe(continuityDirectory, probe, leaseRetryDelay);
        if (presence.Kind == "present") return delegated(request);
        var reason = $"supervisor-{presence.Kind} {presence.Reason}";
        try
        {
            raiseAttention("Conductor self-relaunch held: no live continuity supervisor",
                $"{reason}. The current conductor will continue running.",
                $"conductor-supervisor-absent:{request.GoalId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conduct] Could not raise supervisor attention: {ex.Message}");
        }
        return new ConductorSelfRelaunchResult(false, "handoff", reason, IncumbentCanContinue: true);
    };

    internal static Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> ForWorkspace(
        OrchestratorWorkspace workspace) => Create(
            Path.Combine(workspace.OrchestratorDirectory, "continuity"),
            ConductorSelfRelaunch.CreateSupervisorDelegated(),
            (title, body, key) => CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .RaiseAsync(CollaborationItemType.Decision, null, title, body, key).GetAwaiter().GetResult(),
            new ConductLockPidProbe());
}
