namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private bool HasCurrentBlockingHumanInput(Goal goal, TaskSpec task)
    {
        var pending = _humanInputRequests.Values.Where(item => item.GoalId == goal.Id && item.TaskId == task.Id &&
            !item.IsCompleted && HumanWaitPolicyDefaults.BlocksActiveWork(item.Kind)).ToArray();
        return pending.Any(item => item.IsCurrentForBrief(goal.AuthoritativeBrief.Version)) ||
            task.Status == WorkTaskStatus.WaitingForHuman && !pending.Any(item => !item.IsCurrentForBrief(goal.AuthoritativeBrief.Version));
    }

    private static bool IsObsoletePlannerEvidenceRound(Goal goal, TaskSpec task) =>
        task.RequiredRole == AgentRole.Planner && task.LastDispatch is { BriefSnapshot: not null } dispatch &&
        dispatch.BriefVersion != goal.AuthoritativeBrief.Version &&
        task.LastVerification is { DispatchAttemptAt: not null, PlannerEvidenceBatch: { } batch } &&
        batch.Directives.Any(item => item.PlannerEvidence is not null);

    private void RejectPlannerEvidence(Goal goal, TaskSpec task, TaskVerificationRecord verification, string diagnostic)
    {
        // Preserve the observed process exit. A protocol rejection is nevertheless not passing
        // verification and must never authorize completion when an unrelated answer arrives.
        task.RecordVerification(verification with { OrchestratorFailureReason = diagnostic });
        ReportWorkerTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, diagnostic);
    }

    // Called by host execution paths only. Validation completes before any obligation is published.
    internal string? RecordPlannerEvidenceBatch(Goal goal, TaskSpec task,
        HumanInputDirectiveParseResult batch, int? dispatchBriefVersion, out bool blocksActiveWork)
    {
        blocksActiveWork = false;
        if (batch.IsMalformed) return batch.Diagnostic;
        if (task.RequiredRole != AgentRole.Planner)
            return "Planner evidence requires a Planner task.";
        if (batch.EvidenceLineCount > 1 && dispatchBriefVersion is null)
            return "Plural Planner evidence requires authoritative dispatch brief provenance; replan with a new dispatch.";
        lock (_humanInputRequestLock)
        {
            if (dispatchBriefVersion is not null && dispatchBriefVersion != goal.AuthoritativeBrief.Version)
                return "Planner evidence belongs to an obsolete dispatch brief; replan against the current brief.";
            if (dispatchBriefVersion is not null && goal.RefinedSpec is null)
                return "Typed Planner evidence requires authoritative acceptance criteria for range validation; refine the brief and replan.";
            var items = new List<(HumanInputDirective Directive, PlannerEvidenceIdentity? Identity)>();
            foreach (var directive in batch.Directives)
            {
                if (directive.PlannerEvidence is not { } payload)
                    return "Planner evidence transport is missing a validated payload; replan.";
                if (goal.RefinedSpec is { } spec && payload.CriterionIndex > spec.AcceptanceCriteria.Count)
                    return $"Planner evidence criterion {payload.CriterionIndex} is outside the authoritative criteria; replan.";
                var identity = dispatchBriefVersion is { } version
                    ? new PlannerEvidenceIdentity(goal.Id.Value, task.Id.Value, version, payload.CriterionIndex, payload.EvidenceKey)
                    : null;
                if (identity is not null)
                {
                    var storedPayloads = _humanInputRequests.Values.Where(item => item.PlannerIdentity == identity)
                        .Select(item => item.PlannerEvidence)
                        .Concat(goal.GateTestObligations.Where(item => item.PlannerIdentity == identity).Select(item => item.PlannerEvidence));
                    if (storedPayloads.Any(stored => !payload.SemanticallyEquals(stored)))
                        return $"Conflicting stored Planner evidence criterion={payload.CriterionIndex} key={payload.EvidenceKey}; re-emit the original payload or request a new brief.";
                }
                items.Add((directive, identity));
            }

            foreach (var (directive, identity) in items)
            {
                if (directive.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                    GateTestObligation.IsAcceptanceGateOwner(directive.EvidenceOwner) && directive.TestNames is { Count: > 0 })
                {
                    var obligation = new GateTestObligation(identity?.StableId ?? directive.QuestionFingerprint,
                        dispatchBriefVersion ?? goal.AuthoritativeBrief.Version, directive.TestNames.ToArray(),
                        task.Id.Value, directive.Question, _clock.UtcNow, identity, directive.PlannerEvidence);
                    goal.RecordGateTestObligation(obligation);
                    Append(goal, task.Id, ProgressKind.TaskNote, obligation.RecordingNote);
                }
                else
                {
                    RequestHumanInputDeduplicated(goal.Id, task.Id, directive.Question, kind: directive.Kind,
                        questionFingerprint: directive.QuestionFingerprint, blockerFingerprint: directive.BlockerFingerprint,
                        recordDuplicateSuppression: false, evidenceOwner: directive.EvidenceOwner,
                        storeReference: directive.StoreReference, testNames: directive.TestNames,
                        plannerIdentity: identity, plannerEvidence: directive.PlannerEvidence);
                }
            }
            // Record all siblings first. Answered replays stay answered and cannot reopen a task.
            blocksActiveWork = GetPendingBlockingHumanInput(goal.Id).Any(item => item.TaskId == task.Id);
        }
        return null;
    }
}
