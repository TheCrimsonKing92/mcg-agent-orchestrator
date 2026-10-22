using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Classifies observed compiler results and request-local stop evidence; owns no resources or persistence.
internal static class WorkerBuildTerminalDecision
{
    internal enum StopCause { None, QueueDeadline, CompileDeadline, WorkerExited, DispatchCancelled, LifetimeEnded }

    internal static (WorkerBuildOutcome Outcome, int? CompilerExitCode, string Diagnostic) Decide(
        IReadOnlyList<WorkerBuildObservation> observations, IReadOnlyList<string> projects,
        IReadOnlyList<int?> exits, StopCause cause, TimeSpan budget, DateTimeOffset now, int? workerPid = null)
    {
        var slot = observations.FirstOrDefault(o => o.Phase == WorkerBuildPhase.BuildSlotAcquired);
        if (cause == StopCause.QueueDeadline && slot is not null)
            throw new InvalidOperationException("Queue deadline cannot stop work after build slot acquisition.");

        var failure = exits.FirstOrDefault(exit => exit is not null and not 0);
        if (failure is not null)
            return (WorkerBuildOutcome.CompilerFailure, failure, "Host observed compiler/build exit failure.");
        if (exits.Count == projects.Count && exits.All(exit => exit == 0))
            return (WorkerBuildOutcome.Success, 0, "Host observed all scoped compiler/build exits successfully.");

        var phase = observations[^1].Phase;
        if (cause is not (StopCause.QueueDeadline or StopCause.CompileDeadline))
        {
            var context = exits.Count == 0 ? "" : $" at project {exits.Count}/{projects.Count} {projects[exits.Count - 1]}";
            var reason = cause switch
            {
                StopCause.WorkerExited => $"Worker process {workerPid} exited",
                StopCause.DispatchCancelled => "Dispatch cancelled",
                _ => "Worker request lifetime ended"
            };
            return (WorkerBuildOutcome.Cancelled, null, $"{reason} during {phase}{context}.");
        }

        var requested = observations.First(o => o.Phase == WorkerBuildPhase.Requested).At;
        var custody = observations.FirstOrDefault(o => o.Phase == WorkerBuildPhase.CustodyAcquired)?.At;
        var custodyTime = Duration((custody ?? now) - requested);
        var slotTime = Duration((slot?.At ?? now) - (custody ?? now));
        var bound = Duration(budget);
        var started = observations.Any(o => o.Phase == WorkerBuildPhase.CompilerStarted);
        var interrupted = slot is not null || observations.Any(o => o.Phase == WorkerBuildPhase.PreparingLaunch);
        if (!started && !interrupted)
            return phase == WorkerBuildPhase.WaitingForBuildSlot
                ? (WorkerBuildOutcome.CapacityBeforeStart, null,
                    $"Queue deadline {bound} passed while waiting for a build slot; waited {slotTime} after custody ({custodyTime}).")
                : (WorkerBuildOutcome.ApparatusFailure, null,
                    $"Queue deadline {bound} passed while waiting for goal evidence custody ({custodyTime}).");

        var index = 0;
        while (index < exits.Count && exits[index] == 0) index++;
        index = Math.Min(index, projects.Count - 1);
        var project = $"project {index + 1}/{projects.Count} {projects[index]}";
        var earlier = exits.Take(index).Count(exit => exit == 0);
        var queue = Duration((slot?.At ?? now) - requested);
        var detail = started
            ? $"{Duration(now - (slot?.At ?? requested))} after build slot acquisition while {project} was building"
            : $"before {project} launched";
        return (started ? WorkerBuildOutcome.TimeoutAfterStart : WorkerBuildOutcome.Interrupted, null,
            $"Compile deadline {bound} passed {detail}; {earlier} earlier projects exited 0; queue {queue} (custody {custodyTime}, slot {slotTime}).");
    }

    private static string Duration(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m{seconds % 60:00}s");
    }
}
