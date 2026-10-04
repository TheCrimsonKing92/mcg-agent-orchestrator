using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class FrozenFactRulingBriefTests
{
    private const string DeveloperClosing =
        "Make only each ruling's allowed change and run its diff check on your own diff before reporting.";
    private const string ReviewerClosing =
        "For each ruling, read the candidate diff of each named test file: every changed line must sit inside an amended fact and match its allowed change, and every other fact in a frozen class must be unchanged; report any other edit as a blocking finding with stable id `frozen-fact-scope-<Class.Method>`.";

    [Theory]
    [InlineData(AgentRole.Developer, HumanWaitKind.PlannerPrerequisiteEvidence)]
    [InlineData(AgentRole.Reviewer, HumanWaitKind.PlannerPrerequisiteEvidence)]
    [InlineData(AgentRole.Planner, HumanWaitKind.PlannerPrerequisiteEvidence)]
    [InlineData(AgentRole.Researcher, HumanWaitKind.PlannerPrerequisiteEvidence)]
    [InlineData(AgentRole.Tester, HumanWaitKind.PlannerPrerequisiteEvidence)]
    [InlineData(AgentRole.Planner, HumanWaitKind.SpecClarification)]
    [InlineData(AgentRole.Developer, HumanWaitKind.SpecClarification)]
    [InlineData(AgentRole.Reviewer, HumanWaitKind.SpecClarification)]
    public void EveryRoleReceivesFullLongRulingWithItsRequestAndClosing(AgentRole role, HumanWaitKind kind)
    {
        var context = CreateGoal();
        var ruling = FrozenFactRulingTests.RulingOne() with { Basis = new string('x', 1_700) };
        var text = ruling.Render();
        Assert.True(text.Length > 1_500);
        var request = Request(context, "long ruling", kind);
        context.Kernel.SubmitHumanInput(request.Id, text);

        var task = context.Goal.Tasks.Single(task => task.RequiredRole == role);
        var brief = context.Kernel.BuildTaskBrief(context.Goal.Id, task.Id).Content;
        Assert.Contains("## Frozen-fact rulings", brief.ReplaceLineEndings("\n").Split('\n'));
        Assert.Contains(text.ReplaceLineEndings("\n"), brief.ReplaceLineEndings("\n"));
        Assert.Contains($"Request {request.Id.Value}:", brief);
        if (role == AgentRole.Developer) Assert.Contains(DeveloperClosing, brief);
        if (role == AgentRole.Reviewer) Assert.Contains(ReviewerClosing, brief);
        if (kind == HumanWaitKind.PlannerPrerequisiteEvidence && role != AgentRole.Planner)
            Assert.True(brief.IndexOf("## Frozen-fact rulings", StringComparison.Ordinal) >
                brief.IndexOf("## Answered Prerequisite Evidence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HumanWaitKind.PlannerPrerequisiteEvidence)]
    [InlineData(HumanWaitKind.SpecClarification)]
    public void FreeTextAnswersProduceNoRulingSection(HumanWaitKind kind)
    {
        var context = CreateGoal();
        var request = Request(context, "free text", kind);
        context.Kernel.SubmitHumanInput(request.Id, "Only the attention block changes; every other assertion stays.");
        foreach (var task in context.Goal.Tasks)
            Assert.DoesNotContain("## Frozen-fact rulings", context.Kernel.BuildTaskBrief(context.Goal.Id, task.Id).Content);
    }

    [Fact]
    public void CapKeepsWholeOldestRulingsAndNamesEveryOmittedRequest()
    {
        var context = CreateGoal();
        var oldest = Request(context, "oldest", HumanWaitKind.SpecClarification);
        var oldestText = (FrozenFactRulingTests.RulingOne() with { Basis = new string('a', 2_200) }).Render();
        context.Kernel.SubmitHumanInput(oldest.Id, oldestText);
        context.Clock.Advance();
        var next = Request(context, "next", HumanWaitKind.SpecClarification);
        var nextText = (FrozenFactRulingTests.RulingOne() with { Basis = new string('b', 2_200) }).Render();
        context.Kernel.SubmitHumanInput(next.Id, nextText);
        context.Clock.Advance();
        var last = Request(context, "last", HumanWaitKind.SpecClarification);
        var lastText = (FrozenFactRulingTests.RulingOne() with { Basis = "Last small ruling" }).Render();
        context.Kernel.SubmitHumanInput(last.Id, lastText);

        var section = string.Join(Environment.NewLine,
            FrozenFactRulingBriefSection.Render(context.Kernel.HumanInputRequests.Reverse(), context.Goal.Id, AgentRole.Reviewer));
        Assert.Contains(oldestText, section);
        Assert.DoesNotContain(nextText, section);
        Assert.DoesNotContain("Basis: " + new string('b', 100), section);
        Assert.DoesNotContain("Basis: Last small ruling", section);
        Assert.Contains($"Omitted over the 6,000-character cap: {next.Id.Value}, {last.Id.Value}.", section);
        Assert.EndsWith(ReviewerClosing + Environment.NewLine, section);
    }

    [Fact]
    public void SingleOversizedRulingIsNamedWithoutPartialContent()
    {
        var context = CreateGoal();
        var request = Request(context, "oversized", HumanWaitKind.SpecClarification);
        context.Kernel.SubmitHumanInput(request.Id,
            (FrozenFactRulingTests.RulingOne() with { Basis = new string('x', 6_001) }).Render());
        var section = string.Join(Environment.NewLine,
            FrozenFactRulingBriefSection.Render(context.Kernel.HumanInputRequests, context.Goal.Id, AgentRole.Developer));
        Assert.Contains($"Omitted over the 6,000-character cap: {request.Id.Value}.", section);
        Assert.DoesNotContain("Frozen-fact ruling v1", section);
        Assert.Contains(DeveloperClosing, section);
    }

    [Fact]
    public void UnansweredDismissedSupersededAndOtherGoalRulingsAreExcluded()
    {
        var context = CreateGoal();
        var text = FrozenFactRulingTests.RulingOne().Render();
        Request(context, "pending", HumanWaitKind.SpecClarification);
        var dismissed = Request(context, "dismissed", HumanWaitKind.SpecClarification);
        context.Kernel.DismissHumanInput(dismissed.Id);
        var superseded = Request(context, "superseded", HumanWaitKind.SpecClarification);
        context.Kernel.SubmitHumanInput(superseded.Id, text);
        var other = CreateGoal(context.Kernel, context.Clock);
        var otherRequest = Request(other, "other goal", HumanWaitKind.SpecClarification);
        other.Kernel.SubmitHumanInput(otherRequest.Id, text);
        var snapshot = context.Kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = snapshot.HumanInputRequests.Select(request => request.Id == superseded.Id.Value
                ? request with { SupersededByRequestId = otherRequest.Id.Value } : request).ToList()
        }, context.Clock);
        Assert.Empty(FrozenFactRulingBriefSection.Render(restored.HumanInputRequests, context.Goal.Id, AgentRole.Reviewer));
    }

    private static Context CreateGoal(AgentOrchestratorKernel? kernel = null, FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        kernel ??= new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Move the behavior pinned by a frozen fact",
            [new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner),
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer),
                new TaskSpec(TaskId.New(), "Research", AgentRole.Researcher),
                new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return new(kernel, goal, clock);
    }

    private static HumanInputRequest Request(Context context, string question, HumanWaitKind kind) =>
        context.Kernel.RequestHumanInputDeduplicated(context.Goal.Id, context.Goal.Tasks[0].Id, question, kind).Request;

    private sealed record Context(AgentOrchestratorKernel Kernel, Goal Goal, FakeClock Clock);
}
