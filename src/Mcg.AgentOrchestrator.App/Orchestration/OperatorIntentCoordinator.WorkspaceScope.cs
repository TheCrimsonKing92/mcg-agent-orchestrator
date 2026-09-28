using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OperatorLessonIntentServices(
    SqliteOperatorLessonStore Store,
    AdjudicationEvidenceResolver EvidenceResolver,
    Func<string, Goal?> GoalLookup);

internal sealed class OperatorLessonRejectedException(string message) : Exception(message);

internal sealed partial class OperatorIntentCoordinator
{
    internal OperatorLessonIntentServices? Lessons { get; init; }

    public IReadOnlyList<string> ExecuteWorkspacePending(AgentOrchestratorKernel kernel)
    {
        var lines = new List<string>();
        for (var count = 0; count < MaxIntentsPerGoalPerTick; count++)
        {
            var intent = _store.ClaimNextAsync(OperatorIntentScopes.Workspace, ClaimOwner)
                .GetAwaiter().GetResult();
            if (intent is null) break;
            if (Lessons is null)
                throw new InvalidOperationException("Workspace intent services are unavailable.");
            try
            {
                var replayed = intent.Verb switch
                {
                    OperatorIntentVerbs.LessonRecord => ApplyLessonRecord(kernel, intent, Lessons),
                    OperatorIntentVerbs.LessonRetire => ApplyLessonRetire(intent, Lessons),
                    _ => throw new OperatorLessonRejectedException($"unsupported-workspace-verb {intent.Verb}")
                };
                _store.CompleteAsync(intent.Id, ClaimOwner, OperatorIntentStatus.Applied,
                    replayed ? $"Applied; recovered lesson intent {intent.Id}." : $"Applied {intent.Verb}.",
                    _utcNow()).GetAwaiter().GetResult();
                lines.Add($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} scope=workspace result={(replayed ? "applied-recovered" : "applied")}");
            }
            catch (Exception ex) when (ex is OperatorLessonRejectedException or System.Text.Json.JsonException)
            {
                var reason = Sanitize(ex.Message);
                _store.CompleteAsync(intent.Id, ClaimOwner, OperatorIntentStatus.Rejected,
                    $"Rejected {intent.Verb}: {reason}", _utcNow()).GetAwaiter().GetResult();
                lines.Add($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} scope=workspace result=rejected reason={reason}");
            }
        }
        return lines;
    }

    private bool ApplyLessonRecord(AgentOrchestratorKernel kernel, OperatorIntentRecord intent,
        OperatorLessonIntentServices services)
    {
        if (services.Store.HasRecordSource(intent.Id)) return true;
        var payload = DeserializeLesson<LessonRecordOperatorIntentPayload>(intent);
        if (string.IsNullOrWhiteSpace(payload.Situation) || string.IsNullOrWhiteSpace(payload.Rule))
            throw new OperatorLessonRejectedException("lesson-situation-and-rule-required");
        var evidence = ResolveLessonEvidence(payload.EvidenceReferences, payload.WorkingDirectory,
            payload.GoalId, kernel, services, required: true);
        var lesson = new OperatorLesson(intent.Id, payload.Situation, payload.Rule,
            payload.AppliesTo ?? [], evidence, intent.Actor, intent.ActorKind, intent.Channel,
            _utcNow(), payload.GoalId, null, null, null, null);
        return !services.Store.TryAppendLesson(lesson, intent.Id);
    }

    private bool ApplyLessonRetire(OperatorIntentRecord intent, OperatorLessonIntentServices services)
    {
        if (services.Store.HasRetirementSource(intent.Id)) return true;
        var payload = DeserializeLesson<LessonRetireOperatorIntentPayload>(intent);
        if (string.IsNullOrWhiteSpace(payload.LessonId) || string.IsNullOrWhiteSpace(payload.Reason))
            throw new OperatorLessonRejectedException("lesson-id-and-reason-required");
        var evidence = ResolveLessonEvidence(payload.EvidenceReferences, payload.WorkingDirectory,
            null, null, services, required: false);
        var outcome = services.Store.TryAppendRetirement(payload.LessonId, intent.Id,
            payload.Reason, evidence, intent.Actor, intent.ActorKind, intent.Channel, _utcNow());
        return outcome switch
        {
            OperatorLessonRetireResult.Retired => false,
            OperatorLessonRetireResult.Replayed => true,
            OperatorLessonRetireResult.UnknownLesson => throw new OperatorLessonRejectedException($"lesson-not-found {payload.LessonId}"),
            _ => throw new OperatorLessonRejectedException($"lesson-already-retired {payload.LessonId}")
        };
    }

    private static IReadOnlyList<EvidenceManifestEntry> ResolveLessonEvidence(
        IReadOnlyList<string>? references, string workingDirectory, string? goalId,
        AgentOrchestratorKernel? kernel, OperatorLessonIntentServices services, bool required)
    {
        if (required && (references is null || references.Count == 0))
            throw new OperatorLessonRejectedException("lesson-evidence-required <none>");
        Goal? goal = null;
        if (!string.IsNullOrWhiteSpace(goalId))
            goal = kernel?.Goals.FirstOrDefault(g => g.Id.Value == goalId) ?? services.GoalLookup(goalId);
        var entries = new List<EvidenceManifestEntry>();
        foreach (var reference in references ?? [])
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new OperatorLessonRejectedException("evidence-reference-unresolved <empty>");
            if (!services.EvidenceResolver.TryResolveForLesson(reference, workingDirectory, goal,
                    out var entry, out var rejection))
            {
                if (goalId is not null && goal is null &&
                    (reference.StartsWith("focused-evidence:", StringComparison.OrdinalIgnoreCase) ||
                     reference.StartsWith("acceptance-attempt:", StringComparison.OrdinalIgnoreCase)))
                    rejection = $"evidence-reference-unresolved {reference}";
                throw new OperatorLessonRejectedException(rejection);
            }
            entries.Add(entry);
        }
        return entries;
    }

    private static T DeserializeLesson<T>(OperatorIntentRecord intent)
    {
        try { return Deserialize<T>(intent); }
        catch (InvalidOperationException ex)
        {
            throw new OperatorLessonRejectedException($"invalid-lesson-payload {Sanitize(ex.Message)}");
        }
    }
}
