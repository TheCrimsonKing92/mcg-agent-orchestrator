using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorStewardCaseG(
    Func<GoalId, bool>? hasLiveAcceptanceAttempt,
    Func<GoalId, bool>? inUnreconciledGroupedGate,
    Func<Goal, bool>? hasLandingEvidence,
    Func<Goal, string?>? head)
{
    internal static readonly TimeSpan QuietThreshold = TimeSpan.FromMinutes(30);

    internal static bool IsStranded(Goal goal, TaskSpec task, DateTimeOffset now) =>
        goal.Status == GoalStatus.Verifying &&
        goal.Tasks.All(item => item.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
        LastCompleted(goal)?.Id == task.Id &&
        goal.Timeline.Count > 0 &&
        now - goal.Timeline.Max(item => item.OccurredAt) >= QuietThreshold;

    internal IReadOnlyList<ConductorStewardTrigger> Detect(Goal goal, DateTimeOffset now)
    {
        var task = LastCompleted(goal);
        if (task is null || !IsStranded(goal, task, now) || hasLiveAcceptanceAttempt is null ||
            inUnreconciledGroupedGate is null || hasLandingEvidence is null || head is null)
            return [];
        string? sha;
        try
        {
            if (hasLiveAcceptanceAttempt(goal.Id) || inUnreconciledGroupedGate(goal.Id) ||
                hasLandingEvidence(goal)) return [];
            sha = head(goal);
        }
        catch { return []; }
        if (string.IsNullOrWhiteSpace(sha)) return [];

        var quietSince = goal.Timeline.Max(item => item.OccurredAt);
        var counts = string.Join(", ", Enum.GetValues<WorkTaskStatus>()
            .Select(status => $"{status}={goal.Tasks.Count(item => item.Status == status)}"));
        var evidence = FormattableString.Invariant(
            $"Goal {goal.Id.Value}: quiet-since={quietSince:O}; minutes quiet={Math.Floor((now - quietSince).TotalMinutes)}; threshold={QuietThreshold.TotalMinutes} minutes; tasks [{counts}]; no live acceptance attempt; no unreconciled grouped gate; no landing evidence.");
        var output = task.LastVerification?.AuthoritativeStandardOutput ??
            task.LastVerification?.StandardOutput ?? string.Empty;
        var start = output.LastIndexOf("WORKER_RESULT:", StringComparison.Ordinal);
        var end = start < 0 ? -1 : output.IndexOf("END_WORKER_RESULT", start, StringComparison.Ordinal);
        var workerResult = start < 0 ? output : end < 0 ? output[start..] :
            output[start..(end + "END_WORKER_RESULT".Length)];
        return [new ConductorStewardTrigger(goal.Id.Value, task.Id.Value, sha,
            ConductorStewardTriggerKind.VerifyingWithoutLiveGate, quietSince, evidence, workerResult,
            [$"quiet-since={quietSince.ToString("O", CultureInfo.InvariantCulture)}", $"branch-head={sha}", "steward-case=G"],
            goal.RefinedSpec?.AcceptanceCriteria.ToArray() ?? [])];
    }

    internal static string ComposeNote(ConductorStewardTrigger trigger, string workerResult) =>
        ConductorStewardCaseDAdmission.ComposeText(trigger.Evidence, workerResult);

    internal static bool Admits(Goal goal, TaskSpec task, AdjudicateOperatorIntentPayload payload,
        DateTimeOffset now)
    {
        var cases = (payload.EvidenceReferences ?? [])
            .Where(reference => reference?.StartsWith("steward-case=", StringComparison.Ordinal) == true).ToArray();
        return string.Equals(payload.Shape?.Trim(), "reopen-regate", StringComparison.OrdinalIgnoreCase) &&
            cases.Length == 1 && cases[0] == "steward-case=G" &&
            payload.Precedent?.StartsWith("steward-case=G trigger=", StringComparison.Ordinal) == true &&
            IsStranded(goal, task, now);
    }

    private static TaskSpec? LastCompleted(Goal goal) => goal.Tasks
        .Select((task, index) => (Task: task, Index: index))
        .Where(item => item.Task.Status == WorkTaskStatus.Completed)
        .OrderBy(item => item.Task.LastVerification?.CompletedAt ?? DateTimeOffset.MinValue)
        .ThenBy(item => item.Index)
        .LastOrDefault().Task;
}
