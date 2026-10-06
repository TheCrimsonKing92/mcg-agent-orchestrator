namespace Mcg.AgentOrchestrator.Core;

public enum ReworkCauseFamily
{
    FirstPass,
    ReviewFinding,
    ReviewContractRepair,
    EvidenceRerun,
    CandidateRed,
    GateRed,
    FlakeOrApparatus,
    Environment,
    DownstreamRerun,
    Routing,
    Clarification,
    OperatorRetry,
    StewardRoute,
    Unclassified
}

public sealed record AppliedRetryIntent(string TaskId, DateTimeOffset AppliedAt, OperatorActorKind ActorKind);

/// <summary>Explains why a dispatch after the first happened, independently of its stop cause.</summary>
public static class ReworkCauseClassifier
{
    private static readonly (string Prefix, ReworkCauseFamily Family)[] PrefixTable =
    [
        ("auto-review-retry round ", ReworkCauseFamily.ReviewFinding),
        ("review-finding contract-repair:", ReworkCauseFamily.ReviewContractRepair),
        ("finding evidence-on-demand:", ReworkCauseFamily.EvidenceRerun),
        ("reviewer evidence-on-demand:", ReworkCauseFamily.EvidenceRerun),
        ("ACTIONABLE_CANDIDATE_RED", ReworkCauseFamily.CandidateRed),
        ("DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE", ReworkCauseFamily.CandidateRed),
        ("DEFERRED_NO_CHANGE_EVIDENCE_UNAVAILABLE", ReworkCauseFamily.CandidateRed),
        ("DEFERRED_NO_CHANGE_REPEAT_RED", ReworkCauseFamily.CandidateRed),
        ("Acceptance criteria unmet; retrying task with feedback", ReworkCauseFamily.GateRed),
        ("Auto-retry sandbox-preflight dispatch flake", ReworkCauseFamily.FlakeOrApparatus),
        ("Auto-retry verification-inconclusive Tester task", ReworkCauseFamily.FlakeOrApparatus),
        ("pre-review build repair:", ReworkCauseFamily.FlakeOrApparatus),
        ("Dispatch hit a recoverable subscription usage limit", ReworkCauseFamily.Environment),
        ("Dispatch hit provider connectivity failure", ReworkCauseFamily.Environment),
        ("Dispatch hit ProviderInterruption", ReworkCauseFamily.Environment),
        ("Auto-requeued interrupted dispatch after conductor loop stop", ReworkCauseFamily.Environment),
        ("Invalidated ", ReworkCauseFamily.DownstreamRerun),
        ("missing-planner-artifact: dependency reroute", ReworkCauseFamily.Routing),
        ("worker-build-check-failed automatic recovery ", ReworkCauseFamily.CandidateRed),
        ("Auto-retry real worker/command failure for task ", ReworkCauseFamily.CandidateRed),
        ("developer-completion structural pre-check failed: ", ReworkCauseFamily.CandidateRed)
    ];

    public static ReworkCauseFamily Classify(Goal goal, TaskSpec task, int roundIndex,
        DateTimeOffset previousDispatchAt, DateTimeOffset dispatchAt,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentOutOfRangeException.ThrowIfLessThan(roundIndex, 1);
        if (roundIndex == 1) return ReworkCauseFamily.FirstPass;

        var events = goal.Timeline.Where(e => e.TaskId == task.Id &&
            e.OccurredAt > previousDispatchAt && e.OccurredAt <= dispatchAt).ToArray();
        var retry = events.Where(e => e.Kind == ProgressKind.TaskRetried)
            .OrderBy(e => e.OccurredAt).LastOrDefault();
        if (retry is not null)
        {
            foreach (var (prefix, family) in PrefixTable)
                if (retry.Message.StartsWith(prefix, StringComparison.Ordinal)) return family;

            var intent = intents.Where(i => i.TaskId == task.Id.Value &&
                i.AppliedAt > previousDispatchAt && i.AppliedAt <= dispatchAt)
                .OrderBy(i => i.AppliedAt).LastOrDefault();
            if (intent is not null)
                return intent.ActorKind switch
                {
                    OperatorActorKind.Human => ReworkCauseFamily.OperatorRetry,
                    OperatorActorKind.Agent => ReworkCauseFamily.StewardRoute,
                    _ => ReworkCauseFamily.Unclassified
                };
        }
        else if (events.Any(e => e.Kind == ProgressKind.HumanInputReceived))
            return ReworkCauseFamily.Clarification;

        var receipt = task.RetryAdmissionHistory
            .Where(r => r.LinkedDispatchAt == dispatchAt && r.Cause != RetryCause.Unknown)
            .OrderBy(r => r.RecordedAt).LastOrDefault();
        return receipt?.Cause switch
        {
            RetryCause.ProviderInterruption or RetryCause.ProviderBudgetRecovery => ReworkCauseFamily.Environment,
            RetryCause.MainDriftConflict => ReworkCauseFamily.Routing,
            RetryCause.EnvironmentApparatusFailure => ReworkCauseFamily.FlakeOrApparatus,
            _ => ReworkCauseFamily.Unclassified
        };
    }
}
