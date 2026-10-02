using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PromptRolloutWatchCoordinator(
    string repositoryRoot, PromptRolloutWatchStore store,
    Action<string> emit, IPromptRolloutGitContent? git = null)
{
    private readonly object _gate = new();
    private Dictionary<string, PromptRolloutWatch>? _watches;

    internal static PromptRolloutWatchCoordinator? CreateDefault(
        OrchestratorWorkspace? workspace, Action<string> emit) =>
        workspace is null ? null : new(workspace.ExecutionDirectory,
            new PromptRolloutWatchStore(Path.Combine(workspace.OrchestratorDirectory, PromptRolloutWatchStore.FileName)),
            emit);

    internal void NoteLanding(ConductorLandingReceipt receipt, DateTimeOffset landedAt) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(receipt.LandingSha) ||
            !receipt.ChangedFiles.Any(PromptRolloutPhraseStep.IsPromptPath)) return;
        var watches = Watches();
        if (watches.ContainsKey(receipt.LandingSha)) return;
        var phrases = PromptRolloutPhraseStep.FindNovelPhrases(repositoryRoot, receipt.LandingSha, receipt.ChangedFiles, git);
        if (phrases.Count == 0) return;
        Save(new(receipt.LandingSha, receipt.GoalId, landedAt, phrases, [], [], []));
    });

    internal void EvaluateTick(AgentOrchestratorKernel kernel) => Guard(() =>
    {
        var watches = Watches().Values.Where(watch => watch.State == "open").ToArray();
        if (watches.Length == 0) return;
        var events = kernel.Goals.SelectMany(goal =>
        {
            var roles = goal.Tasks.ToDictionary(task => task.Id, task => task.RequiredRole);
            return goal.Timeline.Where(evt => evt.TaskId is { } taskId &&
                roles.TryGetValue(taskId, out var role) && role is (AgentRole.Tester or AgentRole.Reviewer) &&
                evt.Kind is (ProgressKind.TaskFailed or ProgressKind.TaskRetried or ProgressKind.TaskCompleted));
        }).OrderBy(evt => evt.OccurredAt).ThenBy(evt => evt.GoalId.Value, StringComparer.Ordinal).ToArray();
        foreach (var watch in watches)
        {
            var goals = watch.MatchedGoalIds.ToList();
            var phrases = watch.MatchedPhrases.ToList();
            var rounds = watch.CountedRoundKeys.ToList();
            foreach (var evt in events.Where(evt => evt.GoalId.Value != watch.LandingGoalId && evt.OccurredAt > watch.LandedAt))
            {
                if (evt.Kind is (ProgressKind.TaskCompleted or ProgressKind.TaskFailed) && rounds.Count < 50)
                {
                    var key = $"{evt.GoalId.Value}|{evt.TaskId!.Value}|{evt.Kind}|{evt.OccurredAt.UtcTicks}";
                    if (!rounds.Contains(key, StringComparer.Ordinal)) rounds.Add(key);
                }
                if (evt.Kind is not (ProgressKind.TaskFailed or ProgressKind.TaskRetried)) continue;
                var matched = watch.Phrases.Where(phrase => evt.Message.Contains(phrase, StringComparison.Ordinal)).ToArray();
                if (matched.Length == 0) continue;
                if (!goals.Contains(evt.GoalId.Value, StringComparer.Ordinal)) goals.Add(evt.GoalId.Value);
                foreach (var phrase in matched)
                    if (!phrases.Contains(phrase, StringComparer.Ordinal)) phrases.Add(phrase);
            }
            var state = goals.Count >= 3 ? "fired" : rounds.Count >= 50 ? "exhausted" : "open";
            if (state == watch.State && goals.SequenceEqual(watch.MatchedGoalIds) &&
                phrases.SequenceEqual(watch.MatchedPhrases) && rounds.SequenceEqual(watch.CountedRoundKeys)) continue;
            // Persist closure before publishing: failed writes publish nothing, and a restart cannot re-fire.
            Save(watch with { MatchedGoalIds = goals, MatchedPhrases = phrases, CountedRoundKeys = rounds, State = state });
            if (state == "fired")
                emit($"PROMPT_ROLLOUT_SUSPECT landing={watch.LandingSha} goal={watch.LandingGoalId} " +
                    $"phrases={string.Join(',', phrases)} goals={string.Join(',', goals.Select(id => id[..Math.Min(8, id.Length)]))}");
        }
    });

    private Dictionary<string, PromptRolloutWatch> Watches() => _watches ??=
        store.Load(emit).ToDictionary(watch => watch.LandingSha, StringComparer.Ordinal);

    private void Save(PromptRolloutWatch watch)
    {
        store.Append(watch);
        Watches()[watch.LandingSha] = watch;
    }

    private void Guard(Action action)
    {
        lock (_gate)
        {
            try { action(); }
            catch (Exception ex)
            {
                // The advisory watch cannot stop a landing, canary, relaunch or conductor tick.
                try { emit($"PROMPT_ROLLOUT_WATCH result=skipped reason={ex.GetType().Name}"); }
                catch { /* The diagnostic sink is advisory too. */ }
            }
        }
    }
}
