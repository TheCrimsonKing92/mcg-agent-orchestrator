using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each read owns its kernel, console scene and temporary directory.
public sealed class OwnerQuestionAuthorDefaultTests
{
    [Fact]
    public void AuthorFormatRoundTripsAndRejectsNonAuthorOrIncompleteHolds()
    {
        const string question = "Which seam: existing or new: pick? Mention recommendation= with care.";
        const string recommendation = "Use the existing seam.\nKeep its contract.";
        var blocker = new ConductorAuthorOwnerQuestion("11111111", OperatorAnswerTargetKind.HumanInput,
            "abc123", question, recommendation, "model-ask-owner").Format();
        var hold = new GoalHoldState("hold", "author-owner-question", blocker, DateTimeOffset.UnixEpoch);

        var parsed = Assert.IsType<AuthorOwnerQuestionHold>(AuthorOwnerQuestionHold.TryParse(hold));
        Assert.Equal("HumanInput", parsed.TargetKind);
        Assert.Equal("abc123", parsed.TargetId);
        Assert.Equal("model-ask-owner", parsed.Reason);
        Assert.Equal(question, parsed.Question);
        Assert.Equal(recommendation, parsed.Recommendation);
        Assert.Null(AuthorOwnerQuestionHold.TryParse(hold with { Blocker = "question=Pick a lane?" }));
        Assert.Null(AuthorOwnerQuestionHold.TryParse(hold with { State = "steward-owner-question" }));
        Assert.Null(AuthorOwnerQuestionHold.TryParse(null));
        Assert.Null(AuthorOwnerQuestionHold.TryParse(hold with
        {
            Blocker = blocker.Replace(" recommendation=", " default=", StringComparison.Ordinal)
        }));
    }

    [Theory]
    [InlineData("HumanInput:id")]
    [InlineData("HumanInputid reason=r question=q recommendation=a")]
    [InlineData(":id reason=r question=q recommendation=a")]
    [InlineData("HumanInput: reason=r question=q recommendation=a")]
    [InlineData("HumanInput:id question=q recommendation=a")]
    [InlineData("HumanInput:id reason=r recommendation=a")]
    [InlineData("HumanInput:id reason= question=q recommendation=a")]
    [InlineData("HumanInput:id reason=r question= recommendation=a")]
    [InlineData("HumanInput:id reason=r question=q recommendation=")]
    [InlineData("HumanInput:id reason=r recommendation=a question=q")]
    public void MalformedFieldsReturnNull(string fields)
    {
        var hold = new GoalHoldState("hold", "author-owner-question",
            "author-owner-question item=" + fields, DateTimeOffset.UnixEpoch);
        Assert.Null(AuthorOwnerQuestionHold.TryParse(hold));
    }

    [Theory]
    [InlineData("model-ask-owner", "Reversible")]
    [InlineData("missing-evidence", "Reversible")]
    [InlineData("acceptance-weakening", "CriterionWeakening")]
    [InlineData("frozen-fact-ruling-incomplete", "CriterionWeakening")]
    [InlineData("frozen-fact-ruling-unsupported-target", "CriterionWeakening")]
    [InlineData("authority-widening", "Irreversible")]
    [InlineData("spend-beyond-budget", "Irreversible")]
    [InlineData("irreversible-action", "Irreversible")]
    [InlineData("external-disclosure", "Irreversible")]
    [InlineData("structural-coverage-permit-unavailable", "Irreversible")]
    [InlineData("never-seen-reason", "Irreversible")]
    public void ReasonsHaveConservativeReversibilityClasses(string reason, string expected) =>
        Assert.Equal(expected, AuthorOwnerQuestionHold.Classify(reason).ToString());

    [Theory]
    [InlineData("model-ask-owner", "reversible (Author reason: model-ask-owner)")]
    [InlineData("acceptance-weakening", "criterion-weakening (Author reason: acceptance-weakening)")]
    [InlineData("never-seen-reason", "irreversible (Author reason: never-seen-reason)")]
    public async Task MatchingAuthorHoldEnrichesOnlyItsRequest(string reason, string blastRadius)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
            scene.AddGoals();
            var kernel = scene.Harness.Kernel;
            var request = kernel.RequestHumanInput(new("11111111"), null, "Which seam?");
            var other = kernel.RequestHumanInput(new("11111111"), null, "Which other detail?");
            RaiseAuthorHold(scene, "11111111", request.Id.Value, reason);

            var snapshot = await new OwnerQuestionReadModel(scene.Harness.State, root).ReadAsync(CancellationToken.None);
            AssertUnanswered(request, other);
            Assert.Equal(2, snapshot.Live.Count);
            var live = Assert.Single(snapshot.Live, item => item.ItemId == request.Id.Value);
            Assert.Equal("Use the existing seam.", live.ProposedDefault);
            Assert.Equal(blastRadius, live.BlastRadius);
            var untargeted = Assert.Single(snapshot.Live, item => item.ItemId == other.Id.Value);
            Assert.Null(untargeted.ProposedDefault);
            Assert.Null(untargeted.BlastRadius);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("yes", "yes")]
    [InlineData(" ", "Use the existing seam.")]
    public async Task OwnMeaningfulDefaultTakesPrecedence(string ownDefault, string expected)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
            scene.AddGoals();
            var request = scene.Harness.Kernel.RequestHumanInput(new("11111111"), null, "Ship?",
                suggestedDefaultAnswer: ownDefault);
            RaiseAuthorHold(scene, "11111111", request.Id.Value, "model-ask-owner");

            var snapshot = await new OwnerQuestionReadModel(scene.Harness.State, root).ReadAsync(CancellationToken.None);
            AssertUnanswered(request);
            var live = Assert.Single(snapshot.Live);
            Assert.Equal(expected, live.ProposedDefault);
            Assert.Equal("reversible (Author reason: model-ask-owner)", live.BlastRadius);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task HoldAloneAddsNoDecisionAndClearingMatchingHoldRemovesOnlyEnrichment()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
            scene.AddGoals();
            var kernel = scene.Harness.Kernel;
            var request = kernel.RequestHumanInput(new("11111111"), null, "Which seam?");
            var reader = new OwnerQuestionReadModel(scene.Harness.State, root);
            var before = await reader.ReadAsync(CancellationToken.None);
            AssertUnanswered(request);
            RaiseAuthorHold(scene, "33333333", HumanInputRequestId.New().Value, "model-ask-owner");
            var after = await reader.ReadAsync(CancellationToken.None);
            AssertUnanswered(request);
            Assert.Equal(before.Live, after.Live);
            Assert.DoesNotContain(after.Live, item => item.GoalId == "33333333");

            RaiseAuthorHold(scene, "11111111", request.Id.Value, "model-ask-owner");
            var enriched = await reader.ReadAsync(CancellationToken.None);
            AssertUnanswered(request);
            Assert.Equal("Use the existing seam.", Assert.Single(enriched.Live).ProposedDefault);
            kernel.ClearGoalHold(new("11111111"));
            var cleared = await reader.ReadAsync(CancellationToken.None);
            AssertUnanswered(request);
            Assert.Equal(before.Live, cleared.Live);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-state")]
    public async Task NonMatchingHoldLeavesExistingDecisionUnchanged(string control)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
            scene.AddGoals();
            var kernel = scene.Harness.Kernel;
            var request = kernel.RequestHumanInput(new("11111111"), null, "Which seam?");
            var blocker = new ConductorAuthorOwnerQuestion("11111111",
                control == "wrong-kind" ? OperatorAnswerTargetKind.Clarification : OperatorAnswerTargetKind.HumanInput,
                request.Id.Value, request.Question, "Use the existing seam.", "model-ask-owner").Format();
            kernel.ObserveGoalHold(request.GoalId, control == "wrong-state" ? "other" : "author-owner-question",
                control == "malformed" ? "question=Pick a lane?" : blocker,
                scene.Harness.Clock.GetUtcNow(), TimeSpan.MaxValue, "control");

            var snapshot = await new OwnerQuestionReadModel(scene.Harness.State, root).ReadAsync(CancellationToken.None);
            AssertUnanswered(request);
            var live = Assert.Single(snapshot.Live);
            Assert.Equal(request.Id.Value, live.ItemId);
            Assert.Null(live.ProposedDefault);
            Assert.Null(live.BlastRadius);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void RaiseAuthorHold(OwnerConsoleActivityOutcomeTests.Scene scene,
        string goalId, string targetId, string reason)
    {
        var question = new ConductorAuthorOwnerQuestion(goalId, OperatorAnswerTargetKind.HumanInput,
            targetId, "Which seam?", "Use the existing seam.", reason);
        scene.Harness.Kernel.ObserveGoalHold(new(goalId), "author-owner-question", question.Format(),
            scene.Harness.Clock.GetUtcNow(), TimeSpan.MaxValue, $"author-owner-question:HumanInput:{targetId}");
    }

    private static void AssertUnanswered(params HumanInputRequest[] requests)
    {
        foreach (var request in requests)
        {
            Assert.False(request.IsCompleted);
            Assert.Empty(request.AnswerHistory);
        }
    }
}
