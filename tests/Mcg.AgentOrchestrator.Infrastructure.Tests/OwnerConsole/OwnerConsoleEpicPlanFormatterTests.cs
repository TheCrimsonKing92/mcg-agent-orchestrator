using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

// Parallel-safe: immutable plan fixtures and pure formatting, no terminal or store state.
public sealed class OwnerConsoleEpicPlanFormatterTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-08T22:00:00Z");

    [Fact]
    public void StepPlanShowsOrderedStatusesNewestFiveDecisionsAndNextStep()
    {
        EpicPlanItemStatus[] items = [Step(3, "Final review"), Step(1, "Agree scope", done: true), Step(2, "Build view")];
        // Deliberately shuffled: the formatter must choose and order the newest five.
        EpicPlanDecision[] decisions = [Decision(2), Decision(7), Decision(1), Decision(4), Decision(6), Decision(3), Decision(5)];
        var detail = Detail(items, decisions: decisions);
        var lines = OwnerConsoleEpicFormatter.DetailLines(detail, 200).ToArray();

        Assert.Contains("Bar: Owner sees the complete plan", lines);
        Assert.Equal(["1. Agree scope — done", "2. Build view — open", "3. Final review — open"],
            ItemLines(lines));
        Assert.Equal(new[] { 7, 6, 5, 4, 3 }.Select(id =>
            $"  {At.AddHours(id).ToLocalTime():yyyy-MM-dd HH:mm} by Owner: Choice {id}"),
            Section(lines, "Decisions (newest first):", "Next step:").Where(line => line.Contains(" by Owner:")));
        Assert.DoesNotContain(lines, line => line.Contains("Choice 1") || line.Contains("Choice 2"));
        Assert.Contains("  2 older decisions: see epic-plan epic", lines);
        Assert.Contains("Next step: " + EpicPlanNextStep.Compute(items).Text, lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("Stalled", StringComparison.Ordinal));
    }

    [Fact]
    public void SlicePlanUsesProvidedStatusesAndReasonsAndSharedNextStep()
    {
        EpicPlanItemStatus[] items =
        [
            Slice(3, EpicPlanSliceStatus.Ready, "Ready slice"),
            Slice(1, EpicPlanSliceStatus.Landed, "Landed slice"),
            Slice(4, EpicPlanSliceStatus.Failed, "Failed slice", "Vendor rejected the request"),
            Slice(2, EpicPlanSliceStatus.InFlight, "Active slice", "goal1234 Developer")
        ];
        var lines = OwnerConsoleEpicFormatter.DetailLines(Detail(items), 200);

        Assert.Equal([
            "1. Landed slice — Landed",
            "2. Active slice — In flight (goal1234 Developer)",
            "3. Ready slice — Ready",
            "4. Failed slice — Failed (Vendor rejected the request)"
        ], ItemLines(lines));
        Assert.Contains("Next step: " + EpicPlanNextStep.Compute(items).Text, lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("Stalled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StalledFlagIsFirstInPlanOnlyWhenSharedResultIsStalled(bool stalled)
    {
        EpicPlanItemStatus[] items = [Step(2, "Scope agreed", done: true),
            Slice(1, stalled ? EpicPlanSliceStatus.Blocked : EpicPlanSliceStatus.Ready, "Build view")];
        var lines = OwnerConsoleEpicFormatter.DetailLines(Detail(items), 100).ToArray();
        Assert.Equal(stalled, EpicPlanNextStep.Compute(items).IsStalled);
        if (stalled)
        {
            Assert.Equal(EpicPlanNextStep.StalledText, lines[Array.IndexOf(lines, "Epic plan:") + 1]);
            Assert.True(Array.IndexOf(lines, EpicPlanNextStep.StalledText) < Array.IndexOf(lines, "1. Build view — Blocked"));
            Assert.Single(lines.Where(line => line == EpicPlanNextStep.StalledText));
            Assert.DoesNotContain(lines, line => line.StartsWith("Next step:", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(lines, line => line.StartsWith("Stalled", StringComparison.Ordinal));
            Assert.Contains("Next step: " + EpicPlanNextStep.Compute(items).Text, lines);
        }
    }

    [Fact]
    public void UnplannedMembersShowHumanKindsAndIdPrefixes()
    {
        PortfolioEpicMember[] members = [new("epic", PortfolioMemberKind.Goal, "goal1234-long-id", At, "test"),
            new("epic", PortfolioMemberKind.BacklogItem, "back1234-long-id", At, "test")];
        var lines = OwnerConsoleEpicFormatter.DetailLines(Detail([Step(1, "Review")], members: members), 100);
        Assert.Equal(["  goal goal1234", "  backlog item back1234"],
            Section(lines, "Not in plan:", "In flight:").Where(line => line.Length > 0));
        Assert.DoesNotContain("long-id", string.Join(" ", lines));
    }

    [Fact]
    public void FullyPlannedEpicShowsNoUnplannedMembers()
    {
        var lines = OwnerConsoleEpicFormatter.DetailLines(Detail([Step(1, "Review")]), 100);
        Assert.Equal(["  (none)"], Section(lines, "Not in plan:", "In flight:").Where(line => line.Length > 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPlanKeepsDescriptionAndAllGoalSections(bool absentView)
    {
        var detail = Detail([]);
        if (absentView) detail = detail with { Plan = null };
        var lines = OwnerConsoleEpicFormatter.DetailLines(detail, 100);
        Assert.Contains("(no plan)", lines);
        Assert.Contains("Purpose: make work visible.", lines);
        Assert.Contains("In flight:", lines);
        Assert.Contains("Landed in window:", lines);
        Assert.Contains("Failed:", lines);
        Assert.Contains("Parked:", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("Next step:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("Stalled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    public void NarrowPlanPreservesFullBarStepAndDecisionText(int width)
    {
        const string bar = "The owner can see every planned slice and decision while tracking all work through to completion";
        const string step = "Review the complete console view with the owner and record any remaining concerns before moving forward";
        const string decision = "We agreed to show the complete plan and preserve every word when reading it in a narrow console window";
        var detail = Detail([Step(1, step)], bar, [new(1, At, decision, "Owner")]);
        var lines = OwnerConsoleEpicFormatter.DetailLines(detail, width);
        var planLines = Section(lines, "Epic plan:", "In flight:").ToArray();
        Assert.NotEmpty(planLines);
        Assert.All(planLines, line => Assert.True(line.Length <= width && line.GetColumns() <= width, line));
        var joined = string.Join(" ", planLines);
        Assert.Contains(bar, joined);
        Assert.Contains(step, joined);
        Assert.Contains(decision, joined);
    }

    [Fact]
    public void DecisionWithoutAuthorShowsTimeAndTextWithoutEmptyAuthorField()
    {
        var lines = OwnerConsoleEpicPlanFormatter.Lines(Detail([], decisions: [new(1, At, "Scope agreed", null)]), 100);
        Assert.Contains($"  {At.ToLocalTime():yyyy-MM-dd HH:mm}: Scope agreed", lines);
        Assert.DoesNotContain(lines, line => line.Contains(" by ", StringComparison.Ordinal));
    }

    private static OwnerConsoleEpicViewModel.Detail Detail(IReadOnlyList<EpicPlanItemStatus> items,
        string bar = "Owner sees the complete plan", IReadOnlyList<EpicPlanDecision>? decisions = null,
        IReadOnlyList<PortfolioEpicMember>? members = null) =>
        new(new("epic", "Epic", null, At, "test", At, "test", "Purpose: make work visible."), [], [], [], [],
            new(new("epic", bar, items.Select(item => item.Item).ToArray(), decisions ?? []), items, members ?? []));

    private static EpicPlanItemStatus Step(int position, string text, bool done = false) =>
        new(new(position, EpicPlanItemKind.Step, null, text, done), null, text);
    private static EpicPlanItemStatus Slice(int position, EpicPlanSliceStatus status, string subject, string? reason = null) =>
        new(new(position, EpicPlanItemKind.Slice, "backlog", null, false), status, subject, reason);
    private static EpicPlanDecision Decision(int id) => new(id, At.AddHours(id), $"Choice {id}", "Owner");
    private static IEnumerable<string> ItemLines(IEnumerable<string> lines) =>
        lines.Where(line => line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.');
    private static IEnumerable<string> Section(IEnumerable<string> lines, string heading, string nextHeading) =>
        lines.SkipWhile(line => line != heading).Skip(1).TakeWhile(line => !line.StartsWith(nextHeading, StringComparison.Ordinal));
}
