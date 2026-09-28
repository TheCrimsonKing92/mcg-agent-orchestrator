using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorStewardEvidenceBundleLessonsTests
{
    [Xunit.Fact]
    public void Bundle_lists_only_matching_active_lessons_and_marks_cap()
    {
        using var store = new ConductorLessonTestStore();
        store.Add("retired", ["steward"], retire: true);
        store.Add("unrelated", ["author"]);
        store.Add("case-specific", ["steward:planneroutputcontractrejected"],
            minute: 20, situation: "Planner citation fails", rule: "Check tracked paths");
        for (var index = 0; index < 6; index++)
            store.Add($"general-{index}", ["steward"], minute: index + 1);
        var trigger = Trigger();
        var selected = new ConductorLessonSelector(store.Path)
            .Select(ConductorLessonSelector.StewardTags(trigger.Kind));

        var bundle = ConductorStewardEvidenceBundle.Build(trigger, store.Path, new NoFiles(),
            (_, _) => false, selected);

        Xunit.Assert.Contains("## Operator lessons", bundle);
        Xunit.Assert.Contains("case-specific | situation: Planner citation fails | rule: Check tracked paths", bundle);
        Xunit.Assert.DoesNotContain("retired |", bundle);
        Xunit.Assert.DoesNotContain("unrelated |", bundle);
        Xunit.Assert.Contains("(truncated: 2 more matching lessons omitted)", bundle);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Round_uses_none_when_store_is_missing_or_unreadable(bool unreadable)
    {
        using var store = new ConductorLessonTestStore();
        if (unreadable) File.WriteAllText(store.Path, "not a SQLite database");
        var notes = new List<string>();
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorStewardModelRound(
            Path.Combine(Path.GetDirectoryName(store.Path)!, "steward-rounds"),
            (value, _) =>
            {
                request = value;
                return Task.FromResult(new WorkerProcessRunResult(0, "{}", ""));
            }, new NoFiles(), (_, _) => false,
            lessons: new ConductorLessonSelector(store.Path, notes.Add));

        await round.DispatchAsync(Trigger(), Path.GetDirectoryName(store.Path)!, CancellationToken.None);

        Xunit.Assert.Contains("## Operator lessons" + Environment.NewLine + "none", request!.StandardInput);
        Xunit.Assert.Single(notes);
    }

    private static ConductorStewardTrigger Trigger() => new("goal", "task", "candidate",
        ConductorStewardTriggerKind.PlannerOutputContractRejected,
        new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), "", "", [], []);

    private sealed class NoFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];
    }
}
