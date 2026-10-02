using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OperatorEscapeIntentServices(
    SqliteOperatorEscapeStore Store, Func<IReadOnlyList<string>> ListGoalIds,
    Func<string, bool> HasLanded);

internal sealed partial class OperatorIntentCoordinator
{
    internal OperatorEscapeIntentServices? Escapes { get; init; }

    private bool ApplyEscapeRecord(AgentOrchestratorKernel kernel, OperatorIntentRecord intent,
        OperatorLessonIntentServices lessons, OperatorEscapeIntentServices escapes)
    {
        if (escapes.Store.HasRecordSource(intent.Id)) return true;
        EscapeRecordOperatorIntentPayload payload;
        try { payload = Deserialize<EscapeRecordOperatorIntentPayload>(intent); }
        catch (InvalidOperationException ex)
        {
            throw new OperatorLessonRejectedException($"invalid-escape-payload {Sanitize(ex.Message)}");
        }
        if (string.IsNullOrWhiteSpace(payload.Reason))
            throw new OperatorLessonRejectedException("escape-reason-required");
        var ids = kernel.Goals.Select(g => g.Id.Value).Concat(escapes.ListGoalIds())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var goalId = ResolveEscapeGoal(ids, payload.GoalPrefix, "escape-goal");
        if (!escapes.HasLanded(goalId))
            throw new OperatorLessonRejectedException($"escape-goal-not-landed {goalId}");
        var foundById = payload.FoundByGoalPrefix is null ? null :
            ResolveEscapeGoal(ids, payload.FoundByGoalPrefix, "escape-found-by-goal");
        if (payload.EvidenceReferences is null || payload.EvidenceReferences.Count == 0)
            throw new OperatorLessonRejectedException("escape-evidence-required <none>");
        var evidence = ResolveLessonEvidence(payload.EvidenceReferences, payload.WorkingDirectory,
            goalId, kernel, lessons, required: true);
        var record = new OperatorEscape(intent.Id, goalId, payload.Reason, evidence, foundById,
            intent.Actor, intent.ActorKind, intent.Channel, _utcNow());
        return !escapes.Store.TryAppendEscape(record, intent.Id);
    }

    private static string ResolveEscapeGoal(IReadOnlyList<string> ids, string? prefix, string cause)
    {
        string[] matches = string.IsNullOrWhiteSpace(prefix) ? [] :
            ids.Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new OperatorLessonRejectedException($"{cause}-not-found {prefix}");
        if (matches.Length != 1)
            throw new OperatorLessonRejectedException($"{cause}-ambiguous {prefix} matched {matches.Length}");
        return matches[0];
    }

    internal static bool HasGoalLandedEvent(string directory, string goalId)
    {
        var path = Path.Combine(directory, goalId + ".jsonl");
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("eventType", out var kind) && kind.GetString() == "GoalLanded" &&
                    root.TryGetProperty("goalId", out var id) &&
                    string.Equals(id.GetString(), goalId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return false;
    }
}
