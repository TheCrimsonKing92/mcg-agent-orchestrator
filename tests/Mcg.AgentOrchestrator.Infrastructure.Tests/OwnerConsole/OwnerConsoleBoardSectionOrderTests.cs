using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: immutable synthetic models, no terminal or external state.
public sealed class OwnerConsoleBoardSectionOrderTests
{
    [Fact]
    public void Arrange_AttentionTiers_PromotesDecisionsThenOnboardingStably()
    {
        var model = Model(["home", "a", "b", "c", "ordinary"],
            [Header("home"), Goal("home"), Header("a"), Goal("a"),
                Header("b", "onboarding"), Header("c", "onboarding"),
                Header("ordinary"), Goal("ordinary")],
            [Decision("goal-question", "a-goal"), Onboarding("onboarding:b:build")]);

        var arranged = OwnerConsoleBoardSection.Arrange(model);

        Assert.Equal(new[] { "a", "b", "c", "home", "ordinary" }, Projects(arranged));
        Assert.Equal(1, arranged.Board.Single(row => row.Project == "a" && row.IsDiagnostic).OwnerDecisions);
        Assert.Equal(arranged.Board, OwnerConsoleBoardSection.Arrange(arranged).Board);
        // Clearing attention returns to the scope baseline, not the previous attention order.
        var cleared = OwnerConsoleBoardSection.Arrange(arranged with { Decisions = [] });
        Assert.Equal(new[] { "b", "c", "home", "a", "ordinary" }, Projects(cleared));
        Assert.All(cleared.Board, row => Assert.Equal(0, row.OwnerDecisions));
    }

    [Fact]
    public void Arrange_StaleOrIneligibleDecisions_DoNotPromoteSections()
    {
        var model = Model(["home", "foreign", "dimmed", "onboarding"],
            [Header("home"), Goal("home"), Header("foreign"), Goal("foreign") with { IsForeign = true },
                Header("dimmed"), Goal("dimmed") with { Dimmed = true }, Header("onboarding", "onboarding")],
            [Decision("foreign-q", "foreign-goal"), Decision("dimmed-q", "dimmed-goal"),
                Onboarding("onboarding-stale:home:build"), Onboarding("malformed")]);

        var arranged = OwnerConsoleBoardSection.Arrange(model);

        Assert.Equal(new[] { "onboarding", "home", "foreign", "dimmed" }, Projects(arranged));
        Assert.All(arranged.Board, row => Assert.Equal(0, row.OwnerDecisions));
    }

    [Fact]
    public void Arrange_FooterPartition_RemainsAfterBodyAndKeepsBaselineOrder()
    {
        var model = Model(["empty-a", "home", "empty-b", "attention"],
            [Header("empty-a"), Header("home"), Goal("home"), Header("empty-b"),
                Header("attention"), Goal("attention")],
            [Decision("q", "attention-goal")]);

        var arranged = OwnerConsoleBoardSection.Arrange(model);

        Assert.Equal(new[] { "attention", "home", "empty-a", "empty-b" }, Projects(arranged));
        Assert.Equal(4, OwnerConsoleBoardSection.DisplayedRowCount(arranged.Board, true));
        Assert.Equal("stopped: empty-a, empty-b",
            string.Concat(OwnerConsoleBoardSection.Footer(arranged).Select(segment => segment.Text)));
    }

    [Fact]
    public void Arrange_MultipleDecisions_CountsItemsAndPreservesSingleProjectRows()
    {
        var model = Model(["home", "other"], [Header("home"), Goal("home"), Header("other"), Goal("other")],
            [Decision("q1", "home-goal"), Decision("q2", "home-goal")]);
        var header = OwnerConsoleBoardSection.Arrange(model).Board[0];
        Assert.Equal(2, header.OwnerDecisions);
        Assert.EndsWith(" · 2 decisions ──", OwnerConsoleBoardSection.HeaderLine(header));

        var single = model with { BoardScope = new("home", 1, "home", ["home"]), Board = [Header("home"), Goal("home")] };
        Assert.Equal(single.Board, OwnerConsoleBoardSection.Arrange(single).Board);
    }

    private static OwnerConsoleBoardRow Header(string project, string state = "stopped") =>
        new("", "", project, state, "", null, "") { Project = project, IsDiagnostic = true };

    private static OwnerConsoleBoardRow Goal(string project) =>
        new(project, "", project, "Active", "working", null, project + "-goal") { Project = project };

    private static OwnerConsoleDecision Decision(string id, string goal) =>
        new(id, 1, goal, goal, OwnerQuestionKind.HumanInput, "Choose", "Choose", null, null, null);

    private static OwnerConsoleDecision Onboarding(string id) =>
        new(id, 1, "", "", OwnerQuestionKind.Onboarding, "Configure", "Configure", null, null, null);

    private static OwnerConsoleViewModel Model(string[] projects, ImmutableArray<OwnerConsoleBoardRow> rows,
        ImmutableArray<OwnerConsoleDecision> decisions) =>
        new(new(false, 0, decisions.Length, 0, null, 0), decisions, rows, [],
            BoardScope: new("all", projects.Length, "home", projects));

    private static IEnumerable<string> Projects(OwnerConsoleViewModel model) =>
        model.Board.Where(row => row.IsDiagnostic).Select(row => row.Project);
}
