using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns bounded observations and escalation of indeterminate dispatch apparatus evidence.
internal static class DispatchApparatusHoldRecorder
{
    private const int ApparatusHoldObservationsBeforeEscalation = 2;
    private const int AnsweredApparatusHoldObservationsBeforeFailure = 3;
    private const string ApparatusHoldReceiptPrefix = "DispatchApparatusHoldObserved:";

    internal static void Record(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRecoveryDecision decision,
        string blocker)
    {
        var priorObservations = kernel.GetTimeline(goalId).Count(evt =>
            evt.TaskId == taskId &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith(ApparatusHoldReceiptPrefix, StringComparison.Ordinal) &&
            evt.Message.Contains($"blocker='{blocker}'", StringComparison.Ordinal));
        var observation = priorObservations + 1;
        var diagnostic = DispatchProcessRecoveryService.BuildRecoveryDiagnostic(decision);
        kernel.RecordTaskNote(
            goalId,
            taskId,
            $"{ApparatusHoldReceiptPrefix} observation={observation}/{ApparatusHoldObservationsBeforeEscalation}; {diagnostic}");

        if (observation < ApparatusHoldObservationsBeforeEscalation)
        {
            return;
        }

        var fingerprint = $"dispatch-apparatus-hold:{taskId.Value}:{blocker}";
        var requestResult = kernel.RequestHumanInputDeduplicated(
            goalId,
            taskId,
            $"Dispatch recovery cannot determine the worker outcome after {observation} observations because blocker '{blocker}' remains. " +
            $"Inspect and repair or remove the apparatus artifact at '{decision.EvidencePath}', then answer this request to resume. {diagnostic}",
            HumanWaitKind.RecoveryChoice,
            isAutoDefaultable: false,
            isDismissible: false,
            isAnswerRequired: true,
            isExternallyBlocked: false,
            questionFingerprint: fingerprint,
            blockerFingerprint: fingerprint,
            recordDuplicateSuppression: false);
        if (!requestResult.WasSuppressedByAnswer || requestResult.Request.AnsweredAt is null)
        {
            return;
        }

        var answeredObservations = kernel.GetTimeline(goalId).Count(evt =>
            evt.TaskId == taskId &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.OccurredAt >= requestResult.Request.AnsweredAt &&
            evt.Message.StartsWith(ApparatusHoldReceiptPrefix, StringComparison.Ordinal) &&
            evt.Message.Contains($"blocker='{blocker}'", StringComparison.Ordinal));
        var task = kernel.GetTask(goalId, taskId);
        if (answeredObservations >= AnsweredApparatusHoldObservationsBeforeFailure &&
            task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled))
        {
            kernel.ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Dispatch apparatus remained indeterminate for {answeredObservations} observations after answered recovery request " +
                $"{requestResult.Request.Id.Value[..8]}; blocker='{blocker}'; evidence='{decision.EvidencePath}'.");
        }
    }

}
