using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ReviewerChangedExistingTestClassifierTests
{
    [Fact]
    public void RenderedRulingClassifiesAmendedFrozenRemovedAndUnruledFacts()
    {
        var (kernel, goal) = CreateGoal();
        var request = Answer(kernel, goal, "ruling", Ruling().Render());
        var entries = ReviewerChangedExistingTestClassifier.Classify(
            [Entry("A", "M1"), Entry("A", "M2"), Entry("B", "M3", removed: true), Entry("C", "M4")],
            kernel.HumanInputRequests, goal.Id);
        Assert.Collection(entries,
            entry => AssertClassification(entry, ChangedExistingTestClass.AllowedByRuling, request.Id.Value),
            entry => AssertClassification(entry, ChangedExistingTestClass.FrozenClassViolation, request.Id.Value),
            entry => AssertClassification(entry, ChangedExistingTestClass.FrozenClassViolation, request.Id.Value),
            entry => AssertClassification(entry, ChangedExistingTestClass.NoRuling, null));
    }

    [Fact]
    public void AmendmentAcrossRulingsWinsForChangesButRemovalOfFrozenFactAlwaysViolates()
    {
        var (kernel, goal) = CreateGoal();
        Answer(kernel, goal, "first", Ruling().Render());
        var second = Answer(kernel, goal, "second", (Ruling() with
        {
            FrozenClasses = ["Ns.A"],
            AmendedFacts = [new("Ns.A.M2", "tests/A.cs", "Change assertion")]
        }).Render());
        var entries = ReviewerChangedExistingTestClassifier.Classify(
            [Entry("A", "M2"), Entry("A", "M2", removed: true), Entry("A", "M1", removed: true)],
            kernel.HumanInputRequests.Reverse(), goal.Id);
        AssertClassification(entries[0], ChangedExistingTestClass.AllowedByRuling, second.Id.Value);
        Assert.Equal(ChangedExistingTestClass.FrozenClassViolation, entries[1].Class);
        Assert.Equal(ChangedExistingTestClass.FrozenClassViolation, entries[2].Class);
        Assert.NotNull(entries[1].RulingRequestId);
        Assert.NotNull(entries[2].RulingRequestId);
    }

    [Fact]
    public void IgnoresPendingDismissedSupersededForeignOtherKindAndUnparseableAnswers()
    {
        var (kernel, goal) = CreateGoal();
        kernel.RequestHumanInputDeduplicated(goal.Id, null, "pending", HumanWaitKind.SpecClarification);
        var dismissed = kernel.RequestHumanInputDeduplicated(goal.Id, null, "dismissed", HumanWaitKind.SpecClarification).Request;
        kernel.DismissHumanInput(dismissed.Id);
        var superseded = Answer(kernel, goal, "superseded", Ruling().Render());
        Answer(kernel, goal, "wrong kind", Ruling().Render(), HumanWaitKind.Other);
        Answer(kernel, goal, "free text", "Approved without a typed ruling");
        Answer(kernel, goal, "parked", "Goal parked: waiting for owner");
        var other = kernel.CreateGoal("Other goal", [new(TaskId.New(), "Review", AgentRole.Reviewer)]);
        var foreign = Answer(kernel, other, "foreign", Ruling().Render());
        var snapshot = kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = snapshot.HumanInputRequests.Select(request => request.Id == superseded.Id.Value
                ? request with { SupersededByRequestId = foreign.Id.Value } : request).ToList()
        }, new FixedClock());
        var entry = Assert.Single(ReviewerChangedExistingTestClassifier.Classify(
            [Entry("A", "M2")], restored.HumanInputRequests, goal.Id));
        AssertClassification(entry, ChangedExistingTestClass.NoRuling, null);
    }

    [Fact]
    public void PrerequisiteEvidenceIsSpecClarificationAndSimpleClassNamesMatch()
    {
        var (kernel, goal) = CreateGoal();
        var request = Answer(kernel, goal, "prerequisite", (Ruling() with { FrozenClasses = ["Ns.A", "Ns.B"] }).Render(),
            HumanWaitKind.PlannerPrerequisiteEvidence);
        var entry = Assert.Single(ReviewerChangedExistingTestClassifier.Classify(
            [Entry("B", "M3")], kernel.HumanInputRequests, goal.Id));
        AssertClassification(entry, ChangedExistingTestClass.FrozenClassViolation, request.Id.Value);
    }

    private static FrozenFactRuling Ruling() => new(["A", "B"],
        [new("A.M1", "tests/A.cs", "Update assertion")], "Changed contract", "Other facts", "Inspect diff", ["receipt"]);

    private static ChangedExistingTest Entry(string type, string method, bool removed = false) =>
        new(type, method, $"tests/{type}.cs", 1, 3, removed);

    private static void AssertClassification(ChangedExistingTest entry, ChangedExistingTestClass classification, string? requestId)
    {
        Assert.Equal(classification, entry.Class);
        Assert.Equal(requestId, entry.RulingRequestId);
    }

    private static HumanInputRequest Answer(AgentOrchestratorKernel kernel, Goal goal, string question, string text,
        HumanWaitKind kind = HumanWaitKind.SpecClarification)
    {
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, null, question, kind).Request;
        kernel.SubmitHumanInput(request.Id, text);
        return request;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var goal = kernel.CreateGoal("Classify frozen fact edits", [new(TaskId.New(), "Review", AgentRole.Reviewer)]);
        return (kernel, goal);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    }
}
