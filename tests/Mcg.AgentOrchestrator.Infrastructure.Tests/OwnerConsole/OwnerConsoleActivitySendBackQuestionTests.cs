using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: the scene owns state, time and the headless GUI; no shared files or processes.
public sealed class OwnerConsoleActivitySendBackQuestionTests
{
    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public async Task Activity_RecordedSendBackFinding_EndsWithFirstFindingLine(AgentRole role)
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        var goal = scene.Harness.AddGoal("11111111", "Search", role);
        var time = scene.Harness.Clock.GetUtcNow();
        var finding = new ReviewFinding("finding1", ReviewFindingState.Open,
            new("src/example.cs", "Example"), "The answer loses its evidence.\nMore detail.");
        var verification = new TaskVerificationSnapshot("review", ".", 1, "", "", time,
            MergedReviewFindings: [finding]);
        var future = verification with { CompletedAt = time.AddMinutes(1),
            MergedReviewFindings = [finding with { Description = "Future finding must not appear." }] };
        var snapshot = scene.Harness.Kernel.ExportSnapshot();
        scene.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            Tasks = item.Tasks.Select(task => task with { LastVerification = future,
                VerificationHistory = [verification, future] }).ToArray()
        }).ToArray() });
        await scene.Render([new(time, "goal-lifecycle", goal.Id.Value,
            "TaskFailed task=" + goal.Tasks[0].Id.Value + " outcome=finding")]);
        var line = Assert.Single(scene.View.ActivityLines);
        Assert.EndsWith(role + " sent Search back: The answer loses its evidence.", line);
        Assert.DoesNotContain("More detail", line);
        Assert.DoesNotContain("Future finding", line);
        Assert.DoesNotContain("a problem needs correction", line);
    }

    [Fact]
    public async Task Activity_NoRecordedFinding_KeepsSendBackFallback()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        var goal = scene.Harness.AddGoal("11111111", "Search", AgentRole.Tester);
        await scene.Render([new(scene.Harness.Clock.GetUtcNow(), "goal-lifecycle", goal.Id.Value,
            "TaskFailed task=" + goal.Tasks[0].Id.Value + " outcome=finding")]);
        Assert.EndsWith("Tester sent Search back: a problem needs correction", Assert.Single(scene.View.ActivityLines));
    }

    [Theory]
    [InlineData("model-ask-owner", "you")]
    [InlineData("MODEL-ASK-OWNER", "you")]
    [InlineData("model-ask-operator", "the operator")]
    [InlineData("model-ask-author", "the Author")]
    public async Task Activity_AuthorAndMatchingObservation_ShowsOneQuestionWithRecipient(string token, string recipient)
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        // The producer emits the observation before the author event; item identity binds them.
        await scene.Render([
            new(time, "goal-escalation", "11111111", "author-owner-question item=Goal:11111111 recipient=author question=Pick a lane?"),
            new(time.AddSeconds(1), "author", "11111111", "kind=ask-owner item=11111111:Goal:11111111 reason=" + token)]);
        var line = Assert.Single(scene.View.ActivityLines);
        Assert.Contains("11111111 question for " + recipient + ": Pick a lane?", line);
        Assert.DoesNotContain(token, line, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(time, Assert.Single(scene.Controller.Model!.Activity).Timestamp);
    }

    [Theory]
    [InlineData("model-ask-owner", "you")]
    [InlineData("model-ask-operator", "the operator")]
    [InlineData("unknown-code", "the Author")]
    public void Activity_AuthorWithoutObservation_KeepsOnePlainWordsRow(string token, string recipient)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "author", "11111111",
            "kind=ask-owner reason=" + token + " question=Choose a route?");
        var row = Assert.Single(OwnerActivityNarrator.Narrate([item], _ => "Search"));
        Assert.Equal("11111111 question for " + recipient + ": Choose a route?", row.Phrase);
        Assert.DoesNotContain(token, row.Phrase);
    }

    [Theory]
    [InlineData("owner", "you")]
    [InlineData("operator", "the operator")]
    [InlineData("author", "the Author")]
    public void Activity_ObservationWithoutAuthor_UsesItsRecipient(string token, string recipient)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "goal-escalation", "11111111",
            "author-owner-question recipient=" + token + " question=Choose a route?");
        Assert.Equal("11111111 question for " + recipient + ": Choose a route?",
            Assert.Single(OwnerActivityNarrator.Narrate([item], _ => "Search")).Phrase);
    }

    [Fact]
    public void Activity_QuestionsWithoutIds_FoldNearestPrecedingEventForSameAgent()
    {
        var time = DateTimeOffset.UnixEpoch;
        var rows = OwnerActivityNarrator.Narrate([
            new(time, "author", "11111111", "kind=ask-owner agent=a reason=model-ask-operator"),
            new(time.AddSeconds(1), "author", "11111111", "kind=ask-owner agent=b reason=model-ask-author"),
            new(time.AddSeconds(2), "author", "11111111", "kind=ask-owner agent=a reason=model-ask-owner"),
            new(time.AddSeconds(3), "goal-escalation", "11111111", "author-owner-question agent=a question=Latest?")], _ => "Search");
        Assert.Equal(3, rows.Count);
        Assert.Equal("11111111 question for you: Latest?", rows[0].Phrase);
        Assert.Equal(time.AddSeconds(3), rows[0].Timestamp);
        Assert.DoesNotContain(rows, row => row.Phrase.Contains("model-ask", StringComparison.Ordinal));
    }

    [Fact]
    public void Activity_DifferentQuestionIds_DoNotFoldUnrelatedQuestion()
    {
        var time = DateTimeOffset.UnixEpoch;
        var rows = OwnerActivityNarrator.Narrate([
            new(time, "author", "11111111", "kind=ask-owner question-id=q1 reason=model-ask-owner question=First?"),
            new(time.AddSeconds(1), "goal-escalation", "11111111", "author-owner-question question-id=q2 recipient=operator question=Second?")], _ => "Search");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Phrase == "11111111 question for you: First?");
        Assert.Contains(rows, row => row.Phrase == "11111111 question for the operator: Second?");
    }

    [Fact]
    public async Task Activity_QuestionInDecisions_ShowsOnlyNeedsYouRow()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        scene.Harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Choose a route?"));
        var time = scene.Harness.Clock.GetUtcNow();
        await scene.Render([
            new(time, "author", "11111111", "kind=ask-owner item=11111111:Goal:11111111 reason=model-ask-owner"),
            new(time.AddSeconds(1), "goal-escalation", "11111111", "author-owner-question item=Goal:11111111 question=Choose a route?")]);
        Assert.Contains("Needs you: 11111111 Choose a route?", Assert.Single(scene.View.ActivityLines));
    }
}
