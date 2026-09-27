using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleQuestionPushTests
{
    [Fact]
    public async Task EscalationPushesClarificationAndAnswerUsesItemIdOnce()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        var session = harness.Session();
        await session.StartAsync(null, CancellationToken.None);
        harness.Questions.Items.Add(new OwnerQuestion("clarification-1", goal.Id.Value,
            OwnerQuestionKind.Clarification, "May this touch billing?", "billing", "high"));

        await session.HandleEventAsync(new OwnerConductEvent(harness.Clock.GetUtcNow(),
            "goal-escalation", goal.Id.Value, "waiting"), CancellationToken.None);
        await session.HandleCommandAsync("answer 1 yes", CancellationToken.None);

        Assert.Contains("[1]", harness.Output.Text);
        Assert.Contains("May this touch billing?", harness.Output.Text);
        Assert.Contains("blast radius: billing", harness.Output.Text);
        Assert.Equal([("clarification-1", "yes")], harness.Answers.Calls);
    }

    [Fact]
    public async Task ExternalAnswerRetiresNumberAndUnknownNumberDoesNotSubmit()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        harness.Questions.Items.Add(new OwnerQuestion("clarification-1", goal.Id.Value,
            OwnerQuestionKind.Clarification, "May this touch billing?"));
        var session = harness.Session();
        await session.StartAsync(null, CancellationToken.None);

        harness.Questions.Items.Clear();
        var priorLength = harness.Output.Text.Length;
        await session.HandleCommandAsync("board", CancellationToken.None);
        Assert.DoesNotContain("[1]", harness.Output.Text[priorLength..]);
        await session.HandleCommandAsync("answer 1 yes", CancellationToken.None);
        await session.HandleCommandAsync("answer 9 yes", CancellationToken.None);

        Assert.Contains("question 1 is no longer open", harness.Output.Text);
        Assert.Contains("no question 9", harness.Output.Text);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task StewardEscalationIsViewOnlyAndNeitherAnswerCommandSubmits()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        var session = harness.Session();
        await session.StartAsync(null, CancellationToken.None);
        harness.Questions.Items.Add(new OwnerQuestion("steward-1", goal.Id.Value,
            OwnerQuestionKind.StewardHold, "Should this goal be retried?"));

        await session.HandleEventAsync(new OwnerConductEvent(harness.Clock.GetUtcNow(),
            "goal-escalation", goal.Id.Value, "waiting"), CancellationToken.None);
        await session.HandleCommandAsync("answer 1 yes", CancellationToken.None);
        await session.HandleCommandAsync("accept 1", CancellationToken.None);

        Assert.Contains("[1]", harness.Output.Text);
        Assert.Contains("Should this goal be retried?", harness.Output.Text);
        Assert.Contains("view only", harness.Output.Text);
        Assert.Contains($"retry --goal {goal.Id.Value}", harness.Output.Text);
        Assert.Contains($"adjudicate --goal {goal.Id.Value}", harness.Output.Text);
        Assert.Equal(2, harness.Output.Text.Split("Steward questions are answered through goal verbs for now").Length - 1);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public void ClarificationFieldParsingAcceptsCrLf()
    {
        Assert.Equal("May this touch billing?", OwnerQuestionReadModel.Field(
            "Question: May this touch billing?\r\nFork kind: scope\r\n", "Question:"));
        Assert.Equal("billing", OwnerQuestionReadModel.Field("text\r\nBlast radius: billing\r\n", "Blast radius:"));
        Assert.Equal("high", OwnerQuestionReadModel.Field("Refiner confidence: high\r\n", "Refiner confidence:"));
    }
}
