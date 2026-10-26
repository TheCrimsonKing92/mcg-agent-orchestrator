using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: immutable fixture data, in-memory collaborators and a private temporary directory per refinement.
public sealed class VerificationOwnershipBriefSettlementTests
{
    private const string Paragraph = "Verification ownership: the Developer writes the tests and names them in the deferred list. The conductor runs all tests. The Reviewer checks the diff alone. Post-landing measurements never hold landing.";
    private const string ParagraphObjective = "Integrate the billing system\n\n" + Paragraph;
    private const string VerificationQuestion = "Who executes verification, and does the post-landing observation block acceptance?";
    private const string SettledPolicyPrefix = "Spec refiner fork settled by the brief's Verification ownership paragraph";

    [Theory]
    [InlineData("verification-ownership", VerificationQuestion)]
    [InlineData("verification-and-landing-ownership", "Who may execute verification, and does reviewer inspection require operator evidence?")]
    [InlineData("verification-execution-authority", "Who executes the new tests and the existing regression tests?")]
    [InlineData("test-runner-ownership", "Who runs the new tests?")]
    public void TrySettle_VerificationOwnership_ReturnsBriefParagraph(string topicKey, string question)
    {
        var settled = VerificationOwnershipBriefSettlement.TrySettle(
            "ownership-lifecycle", topicKey, question, ParagraphObjective, out var paragraph);

        Assert.True(settled);
        Assert.Equal(Paragraph, paragraph);
    }

    [Theory]
    [InlineData("ownership-lifecycle", "artifact-disposition", "Where do superseded artifacts go?", ParagraphObjective)]
    [InlineData("external-contract", "verification-ownership", VerificationQuestion, ParagraphObjective)]
    [InlineData("ownership-lifecycle", "verification-ownership", VerificationQuestion, "Integrate the billing system")]
    [InlineData("ownership-lifecycle", "verification-ownership", VerificationQuestion, "Integrate the billing system\n\nSee the Verification ownership: note below.")]
    [InlineData("ownership-lifecycle", "verification-ownership", VerificationQuestion, "Integrate the billing system\n\nVerification ownership:\n\nNext section.")]
    [InlineData("ownership-lifecycle", "verification-ownership", VerificationQuestion, "Verification ownership:\n\nVerification ownership: later text.")]
    public void TrySettle_NonQualifyingForkOrParagraph_ReturnsEmpty(
        string kind, string topicKey, string question, string objective)
    {
        var settled = VerificationOwnershipBriefSettlement.TrySettle(
            kind, topicKey, question, objective, out var paragraph);

        Assert.False(settled);
        Assert.Equal(string.Empty, paragraph);
    }

    [Theory]
    [InlineData("Intro\r\n\r\n  Verification ownership: first line.\r\nsecond line.\r\n\r\nAfter the gap.", "Verification ownership: first line. second line.")]
    [InlineData("Verification ownership: first line.\n# Heading\nAfter the heading.", "Verification ownership: first line.")]
    [InlineData("Verification ownership: first line.\n  ######\nAfter the heading.", "Verification ownership: first line.")]
    [InlineData("Verification ownership: first line.\n#ordinary text", "Verification ownership: first line. #ordinary text")]
    [InlineData("Verification ownership: first line.\n####### text", "Verification ownership: first line. ####### text")]
    [InlineData("Verification ownership:\ncontinuation.", "Verification ownership: continuation.")]
    [InlineData("  VERIFICATION OWNERSHIP: first line.  ", "VERIFICATION OWNERSHIP: first line.")]
    public void TrySettle_Extraction_NormalizesLinesAndStopsAtParagraphBoundary(
        string objective, string expected)
    {
        var settled = VerificationOwnershipBriefSettlement.TrySettle(
            " OWNERSHIP-LIFECYCLE ", "VERIFICATION-OWNERSHIP", VerificationQuestion,
            objective, out var paragraph);

        Assert.True(settled);
        Assert.Equal(expected, paragraph);
    }

    [Fact]
    public async Task RefineAsync_Incident_SettlesWithoutClarification()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var (service, kernel, goalId, collaboration) = BuildScenario(
                tempDirectory, ParagraphObjective, BuildRefinerJson(VerificationFork()));

            var result = await service.RefineAsync(kernel, goalId);

            Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
            Assert.Empty(collaboration.Items);
            Assert.Empty(result.Spec.OpenQuestions);
            var decision = Assert.Single(result.Spec.Decisions);
            Assert.Equal(VerificationQuestion, decision.Question);
            Assert.Equal(Paragraph, decision.Choice);
            Assert.Equal("Settled by the brief's Verification ownership paragraph (topic: verification-ownership).", decision.Rationale);
            var policy = Assert.Single(kernel.GetGoal(goalId).Timeline,
                item => item.Kind == ProgressKind.GoalPolicyDecision &&
                    item.Message == SettledPolicyPrefix + ": topic=verification-ownership");
            Assert.Equal(SettledPolicyPrefix + ": topic=verification-ownership", policy.Message);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RefineAsync_WithoutParagraph_RaisesVerificationClarification()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var (service, kernel, goalId, collaboration) = BuildScenario(
                tempDirectory, "Integrate the billing system", BuildRefinerJson(VerificationFork()));

            var result = await service.RefineAsync(kernel, goalId);

            Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
            var item = Assert.Single(collaboration.Items);
            Assert.Equal(CollaborationItemType.Clarification, item.Type);
            Assert.Equal($"spec-clarification:{goalId.Value}:verification-ownership", item.CorrelationKey);
            Assert.DoesNotContain(result.Spec.Decisions, decision => decision.Question == VerificationQuestion);
            Assert.DoesNotContain(kernel.GetGoal(goalId).Timeline,
                entry => entry.Message.StartsWith(SettledPolicyPrefix, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RefineAsync_ExternalContract_RaisesClarification()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var fork = VerificationFork() with { Kind = "external-contract" };
            var (service, kernel, goalId, collaboration) = BuildScenario(
                tempDirectory, ParagraphObjective, BuildRefinerJson(fork));

            var result = await service.RefineAsync(kernel, goalId);

            Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
            Assert.Equal(CollaborationItemType.Clarification, Assert.Single(collaboration.Items).Type);
            Assert.DoesNotContain(result.Spec.Decisions, decision => decision.Question == VerificationQuestion);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RefineAsync_MixedForks_SettlesOnlyVerification()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var unrelatedFork = VerificationFork() with
            {
                TopicKey = "artifact-disposition",
                Question = "Where do superseded artifacts go?",
                RefinerConfidence = "low"
            };
            var (service, kernel, goalId, collaboration) = BuildScenario(
                tempDirectory, ParagraphObjective, BuildRefinerJson(VerificationFork(), unrelatedFork));

            var result = await service.RefineAsync(kernel, goalId);

            Assert.Equal(RefinementOutcome.AwaitingClarification, result.Outcome);
            var item = Assert.Single(collaboration.Items);
            Assert.Equal(CollaborationItemType.Clarification, item.Type);
            Assert.Equal($"spec-clarification:{goalId.Value}:artifact-disposition", item.CorrelationKey);
            var decision = Assert.Single(result.Spec.Decisions, entry => entry.Question == VerificationQuestion);
            Assert.Equal(Paragraph, decision.Choice);
            Assert.Equal(item.CorrelationKey, Assert.Single(result.Spec.OpenQuestions).Id);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RefineAsync_DecideFork_PreservesForkChoice()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var fork = VerificationFork() with { BlastRadius = "low", Choice = "Conductor" };
            var (service, kernel, goalId, collaboration) = BuildScenario(
                tempDirectory, ParagraphObjective, BuildRefinerJson(fork));

            var result = await service.RefineAsync(kernel, goalId);

            Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
            Assert.Empty(collaboration.Items);
            var decision = Assert.Single(result.Spec.Decisions, entry => entry.Question == VerificationQuestion);
            Assert.Equal("Conductor", decision.Choice);
            Assert.DoesNotContain(kernel.GetGoal(goalId).Timeline,
                entry => entry.Message.StartsWith(SettledPolicyPrefix, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static SpecRefinementFork VerificationFork() => new(
        "ownership-lifecycle", "high", "high", VerificationQuestion, "", "Operator decision required.",
        "verification-ownership");

    private static string BuildRefinerJson(params SpecRefinementFork[] forks) => JsonSerializer.Serialize(new
    {
        behavioralContract = "Return the configured result.",
        acceptanceCriteria = new[] { "Returns the configured result." },
        verificationClass = "TestVerifiable",
        decisions = Array.Empty<object>(),
        forks = forks.Select(fork => new
        {
            kind = fork.Kind,
            topicKey = fork.TopicKey,
            refinerConfidence = fork.RefinerConfidence,
            blastRadius = fork.BlastRadius,
            question = fork.Question,
            choice = fork.Choice,
            rationale = fork.Rationale
        })
    });

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "verification-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static (
        GoalRefinementService Service,
        AgentOrchestratorKernel Kernel,
        GoalId GoalId,
        FakeCollaborationItemStore Collaboration) BuildScenario(
            string tempDirectory, string objective, string refinerJson)
    {
        var provider = new FakeSmokeProvider(text: refinerJson, providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        var collaboration = new FakeCollaborationItemStore();
        var service = new GoalRefinementService(
            registry,
            catalog,
            collaboration,
            new SpecRefinerPrecedentStore(Path.Combine(tempDirectory, "precedents.json")),
            rawOutputDirectory: Path.Combine(tempDirectory, "logs"));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(objective);
        return (service, kernel, goal.Id, collaboration);
    }
}
