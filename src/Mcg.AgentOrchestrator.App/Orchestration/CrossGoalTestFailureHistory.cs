using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Best-effort method-level failure history; a goal can never corroborate its own failure.</summary>
internal sealed class CrossGoalTestFailureHistory(
    string? censusPath, string? stateRoot, Func<IReadOnlyCollection<Goal>> goals)
{
    internal static string? TestKey(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || AcceptanceTestSourceResolver.ExtractClassName(identity) is not { } name)
            return null;
        var method = identity.Split('(', 2)[0].Split('.')[^1].Trim();
        return method.Length == 0 ? null : name + "." + method;
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<string>> Read(
        GoalId current, IReadOnlyList<string> identities, DateTimeOffset now)
    {
        var matches = identities.Select(TestKey).OfType<string>().Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        Guid.TryParse(current.Value, out var currentId);
        void Add(string? goalId, DateTimeOffset at, string? identity)
        {
            if (!Guid.TryParse(goalId, out var other) || other == currentId ||
                at > now || at < now.AddDays(-14) || TestKey(identity) is not { } key || !matches.TryGetValue(key, out var set)) return;
            set.Add(other.ToString("N"));
        }

        if (censusPath is not null)
            foreach (var record in new AcceptanceFailingTestIndex(censusPath).Read())
                if (record.Kind == AcceptanceFailingTestIndexKinds.GateFailure && !record.InsideChangedPaths)
                    Add(record.GoalId, record.RecordedAt, record.TestIdentity);
        if (stateRoot is not null)
        {
            try
            {
                foreach (var row in new AcceptanceLaneFlakeLedger(stateRoot).Read(since: now.AddDays(-14)))
                    foreach (var identity in row.FirstFailingTestIdentities)
                        Add(row.GoalId, row.RecordedAt, identity);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var goal in goals().Where(goal => goal.Id != current))
        {
            var testers = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Tester).Select(task => task.Id).ToHashSet();
            foreach (var evt in goal.Timeline.Where(evt => evt.TaskId is { } taskId && testers.Contains(taskId) &&
                         evt.Kind == ProgressKind.FindingEvidenceRunRecorded && evt.OccurredAt >= now.AddDays(-14) && evt.OccurredAt <= now))
            {
                PreTesterEvidenceEntry? entry;
                try { entry = PreTesterEvidenceIndexLines.Parse(evt.Message); }
                catch (Exception ex) when (ex is ArgumentException or UriFormatException) { continue; }
                if (entry is not { Outcome: "red" }) continue;
                foreach (var identity in entry.FailingTests) Add(goal.Id.Value, evt.OccurredAt, identity);
            }
        }
        return matches.Where(pair => pair.Value.Count > 0).ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(
        OrchestratorWorkspace? workspace, IReadOnlyCollection<Goal> goals, Goal goal, IReadOnlyList<string> identities, DateTimeOffset now) =>
        workspace is null ? new Dictionary<string, IReadOnlyList<string>>() :
            new CrossGoalTestFailureHistory(Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts",
                AcceptanceFailingTestIndex.FileName), workspace.ExecutionDirectory, () => goals).Read(goal.Id, identities, now);
}
