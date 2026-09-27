using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeveloperDeferredNoChangeQualifierTests
{
    private static readonly string Candidate = new('b', 40);

    [Xunit.Fact]
    public void RetryWithRationaleAndDeferredClassesQualifies()
    {
        var (goal, developer) = Scenario();
        var accepted = Qualify(goal, developer, Output("AlphaTests, BetaTests"), out var outcome);

        Xunit.Assert.True(accepted);
        Xunit.Assert.Equal(Candidate, outcome.CandidateSha);
        Xunit.Assert.Equal(["AlphaTests", "BetaTests"], outcome.TestClasses);
        Xunit.Assert.StartsWith("NO_CHANGE:", outcome.Rationale, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RetryWithCrLfRationaleAndDeferredClassesQualifies()
    {
        var (goal, developer) = Scenario();
        var output = Output("AlphaTests").Replace("\n", "\r\n", StringComparison.Ordinal);

        var accepted = Qualify(goal, developer, output, out var outcome);

        Xunit.Assert.True(accepted);
        Xunit.Assert.Equal(["AlphaTests"], outcome.TestClasses);
        Xunit.Assert.Equal("NO_CHANGE: the candidate already has the repair.", outcome.Rationale);
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-retry")]
    [Xunit.InlineData("no-rationale")]
    [Xunit.InlineData("no-classes")]
    [Xunit.InlineData("blocker")]
    [Xunit.InlineData("dirty")]
    [Xunit.InlineData("new-commit")]
    [Xunit.InlineData("wrong-head")]
    public void MissingQualificationConditionKeepsTheOldRejectionPath(string missing)
    {
        var (goal, developer) = Scenario(retry: missing != "no-retry");
        var output = Output(missing == "no-classes" ? "conductor will verify" : "AlphaTests",
            rationale: missing != "no-rationale", blocker: missing == "blocker");
        var accepted = Qualify(goal, developer, output, out _,
            clean: missing != "dirty",
            relevantCommit: missing == "new-commit",
            head: missing == "wrong-head" ? new string('c', 40) : null);

        Xunit.Assert.False(accepted);
    }

    [Xunit.Fact]
    public void EveryFailingTestClassMustBeNamed()
    {
        var (goal, developer) = Scenario(
            "ACTIONABLE_CANDIDATE_RED candidate_sha=" + Candidate +
            "; failing_tests=Suite.AlphaTests.First,Suite.BetaTests.Second(case). Repair the source.");

        Xunit.Assert.False(Qualify(goal, developer, Output("AlphaTests"), out _));
        Xunit.Assert.True(Qualify(goal, developer, Output("AlphaTests, BetaTests"), out _));
    }

    private static (Goal Goal, TaskSpec Developer) Scenario(string? feedback = null, bool retry = true)
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement the retry.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Deferred no-change", [developer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (retry) kernel.RetryTask(goal.Id, developer.Id, feedback ?? "Retry current candidate.");
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", DateTimeOffset.UtcNow, BaseCommit: Candidate));
        return (goal, developer);
    }

    private static bool Qualify(
        Goal goal, TaskSpec developer, string output, out DeferredNoChangeOutcome outcome,
        bool clean = true, bool relevantCommit = false, string? head = null)
    {
        var catalog = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => catalog.ResolveProfile(dispatch.WorkerName),
            new SystemClock(), _ => false,
            _ => throw new FileNotFoundException());
        return DeveloperDeferredNoChangeQualifier.TryQualify(
            goal, developer, head ?? Candidate, clean, relevantCommit,
            output, string.Empty, classifier, out outcome);
    }

    private static string Output(string classes, bool rationale = true, bool blocker = false) =>
        (rationale ? "NO_CHANGE: the candidate already has the repair.\n" : string.Empty) +
        "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: deferred - " + classes +
        "\ncommit: none\nblockers: " + (blocker ? "source work remains" : "none") +
        "\nassigned_scope_complete: true\nmodel_fit: test/model - adequate - fixture\n" +
        "skills: none\nconfidence: high\nEND_WORKER_RESULT";
}
