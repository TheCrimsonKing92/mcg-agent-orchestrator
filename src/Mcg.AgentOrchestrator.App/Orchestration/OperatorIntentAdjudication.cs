using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class OperatorIntentAdjudicationRejectedException(string reason)
    : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}

internal sealed class OperatorIntentAdjudication(
    ICollaborationItemStore decisions,
    Func<GoalId, long?> goalStateVersionResolver,
    AdjudicationEvidenceResolver evidenceResolver)
{
    private const string TemplateVersion = "operator-adjudication-v1";

    public void Apply(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OperatorIntentRecord intent,
        AdjudicateOperatorIntentPayload payload,
        DateTimeOffset now)
    {
        var shape = NormalizeShape(payload.Shape);
        var manifest = BuildEvidenceManifest(goal, payload, out var unresolvedEvidence);
        var reason = Validate(goal, task, payload, shape, unresolvedEvidence, out var retryCause);
        var reversibility = ParseReversibility(payload.Reversibility, shape, out var invalidReversibility);
        if (reason is null && invalidReversibility)
            reason = "unknown-reversibility";
        var currentVersion = goalStateVersionResolver(goal.Id);
        if (reason is null && currentVersion != payload.ExpectedGoalStateVersion)
            reason = "stale-goal-state-version";

        var actionKind = shape == "route" ? DecisionActionKind.Retry : DecisionActionKind.VerifyManual;
        var requiredTier = DecisionAuthorization.RequiredTierFor(actionKind);
        var assurance = ResolveAuthorizationTier(intent.AuthenticationAssurance);
        var actionToken = shape is "close" or "reopen-regate" or "route" ? shape : "invalid";
        var actionRef = new DecisionActionRef($"adjudicate.{actionToken}.{intent.Id}");
        var requestId = $"adjudicate-{intent.Id}";
        var expiresAt = now.AddHours(24);
        var request = new DecisionRequest(
            requestId,
            DecisionRequestKind.General,
            goal.Id.Value,
            $"Adjudicate task {task.Id.Value}",
            string.IsNullOrWhiteSpace(payload.Text)
                ? $"Adjudication rejected: {reason ?? "missing-explanation"}."
                : payload.Text.Trim(),
            TemplateVersion,
            manifest,
            expiresAt,
            DecisionDefaultDisposition.NoAction,
            new DecisionBlockingImpact("The task remains unchanged when adjudication is rejected.", []),
            [DecisionReuseScope.ThisOccurrence],
            [new DecisionAllowedAction(actionRef, shape, actionKind, requiredTier, expiresAt, payload.ExpectedGoalStateVersion)],
            now);
        decisions.RaiseDecisionRequestAsync(request).GetAwaiter().GetResult();
        var receipt = decisions.RecordDecisionAsync(
            requestId,
            OperatorActorIdentity.Format(intent.Actor, intent.ActorKind),
            intent.Channel,
            assurance,
            payload.ExpectedGoalStateVersion,
            new DecisionResponse(actionRef, shape, DecisionReuseScope.ThisOccurrence, false),
            now,
            reversibility,
            string.IsNullOrWhiteSpace(payload.Precedent) ? null : payload.Precedent.Trim()).GetAwaiter().GetResult();

        if (reason is not null)
        {
            Reject(kernel, goal, task, intent, receipt, actionRef, currentVersion, reason, now);
        }
        if (!DecisionAuthorization.Meets(assurance, requiredTier))
        {
            var denied = decisions.TryApplyDecisionEffectAsync(
                requestId, receipt.Id, actionRef, currentVersion, "adjudication applied", now).GetAwaiter().GetResult();
            RejectWithRecordedEffect(kernel, goal, task, intent, receipt, denied.Receipt.Result);
        }

        switch (shape)
        {
            case "close":
                CompleteAndVerify(kernel, goal, task, payload, now, CorrectionSource(intent));
                break;
            case "reopen-regate":
                kernel.RetryTaskWithAuthoritativeFeedback(
                    goal.Id,
                    task.Id,
                    payload.Text,
                    RetryCause.UnchangedContextRepeat,
                    invalidateDownstream: true,
                    retryRoundKind: RetryRoundKind.Mechanical,
                    correctionSource: CorrectionSource(intent));
                CompleteAndVerify(kernel, goal, task, payload, now, CorrectionSource(intent));
                break;
            case "route":
                kernel.RetryTaskWithAuthoritativeFeedback(
                    goal.Id,
                    task.Id,
                    payload.Text,
                    retryCause!.Value,
                    invalidateDownstream: true,
                    correctionSource: CorrectionSource(intent));
                break;
            default:
                throw new InvalidOperationException($"Unsupported adjudication shape '{shape}'.");
        }

        var applied = decisions.TryApplyDecisionEffectAsync(
            requestId, receipt.Id, actionRef, currentVersion,
            $"applied task={task.Status} goal={goal.Status}", now).GetAwaiter().GetResult();
        if (!applied.Applied)
            throw new InvalidOperationException($"Decision effect was unexpectedly rejected: {applied.Receipt.Result}");
        RecordTimeline(kernel, goal, task, intent, receipt.Id, "applied");
    }

    private void Reject(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OperatorIntentRecord intent,
        DecisionReceipt receipt,
        DecisionActionRef actionRef,
        long? currentVersion,
        string reason,
        DateTimeOffset now)
    {
        var effect = decisions.TryApplyDecisionEffectAsync(
            receipt.RequestId,
            receipt.Id,
            actionRef,
            currentVersion,
            "adjudication rejected",
            now,
            reason).GetAwaiter().GetResult();
        RejectWithRecordedEffect(kernel, goal, task, intent, receipt, effect.Receipt.Result);
    }

    private static void RejectWithRecordedEffect(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OperatorIntentRecord intent,
        DecisionReceipt receipt,
        string reason)
    {
        RecordTimeline(kernel, goal, task, intent, receipt.Id, reason);
        throw new OperatorIntentAdjudicationRejectedException(reason);
    }

    private static void CompleteAndVerify(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        AdjudicateOperatorIntentPayload payload,
        DateTimeOffset now,
        CriteriaCorrectionSource correctionSource)
    {
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, payload.Text,
            correctionSource: correctionSource);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, payload.Text, payload.WorkingDirectory, now));
    }

    private static CriteriaCorrectionSource CorrectionSource(OperatorIntentRecord intent) =>
        intent.ActorKind == OperatorActorKind.Human
            ? CriteriaCorrectionSource.Operator : CriteriaCorrectionSource.AgentIntent;

    private static void RecordTimeline(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OperatorIntentRecord intent,
        string decisionId,
        string outcome) =>
        kernel.RecordOperatorIntentApplied(
            goal.Id,
            intent.Id,
            intent.Verb,
            task.Id.Value,
            intent.Actor,
            intent.Channel,
            intent.AuthenticationAssurance,
            $"OPERATOR_ADJUDICATION decision={decisionId} shape={DeserializeShape(intent)} outcome={outcome}",
            intent.ActorKind,
            decisionId,
            outcome);

    private static string? Validate(
        Goal goal,
        TaskSpec task,
        AdjudicateOperatorIntentPayload payload,
        string shape,
        bool unresolvedEvidence,
        out RetryCause? retryCause)
    {
        retryCause = null;
        if (string.IsNullOrWhiteSpace(payload.Text)) return "missing-explanation";
        if (payload.EvidenceReferences is null || payload.EvidenceReferences.All(string.IsNullOrWhiteSpace)) return "missing-evidence";
        if (shape is not ("close" or "reopen-regate" or "route")) return "unknown-shape";
        if (shape == "reopen-regate" && goal.Status != GoalStatus.AcceptanceFailed) return "goal-not-acceptance-failed";
        if (shape is "close" or "reopen-regate" && task.Status is not
            (WorkTaskStatus.Assigned or WorkTaskStatus.Failed or WorkTaskStatus.Completed))
            return "task-not-closable";
        if (shape == "route" && task.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman)
            return "task-not-retryable";
        if (shape == "route" &&
            (!Enum.TryParse(payload.Cause, ignoreCase: true, out RetryCause parsedCause) ||
             !Enum.IsDefined(parsedCause) || parsedCause == RetryCause.Unknown))
            return "unknown-retry-cause";
        if (shape == "route") retryCause = Enum.Parse<RetryCause>(payload.Cause!, ignoreCase: true);
        if (unresolvedEvidence) return "evidence-reference-unresolved";
        return null;
    }

    private EvidenceManifest BuildEvidenceManifest(
        Goal goal,
        AdjudicateOperatorIntentPayload payload,
        out bool unresolved)
    {
        unresolved = false;
        var entries = new List<EvidenceManifestEntry>();
        foreach (var reference in (payload.EvidenceReferences ?? [])
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.Ordinal))
        {
            if (!evidenceResolver.TryResolve(reference, goal, payload, out var entry))
                unresolved = true;
            entries.Add(entry);
        }
        return EvidenceManifest.Create(entries);
    }

    private static DecisionReversibility? ParseReversibility(string? value, string shape, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(value))
            return shape == "route" ? DecisionReversibility.Reversible : DecisionReversibility.ReversibleWithCost;
        var parsed = value.Trim().ToLowerInvariant() switch
        {
            "reversible" => DecisionReversibility.Reversible,
            "reversible-with-cost" => DecisionReversibility.ReversibleWithCost,
            "irreversible" => DecisionReversibility.Irreversible,
            _ => (DecisionReversibility?)null
        };
        invalid = parsed is null;
        return parsed;
    }

    private static AuthorizationTier ResolveAuthorizationTier(string assurance) => assurance switch
    {
        "local-process" => AuthorizationTier.AttestLand,
        "discord-operator-allowlist" => AuthorizationTier.Mutate,
        _ => AuthorizationTier.Answer
    };

    private static string NormalizeShape(string? shape) => shape?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string DeserializeShape(OperatorIntentRecord intent)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(
                intent.PayloadJson,
                OperatorIntentJson.Options)?.Shape ?? "unknown";
        }
        catch (System.Text.Json.JsonException)
        {
            return "unknown";
        }
    }
}
