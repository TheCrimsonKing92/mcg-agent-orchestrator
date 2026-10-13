using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class ReviewFindingSeverityValidationTests
{
    private static ReviewFinding Finding(string id, FindingSeverity severity, bool malformed = false) =>
        new(id, ReviewFindingState.Open, new("src/Target.cs", "Run"), "Check the guard", severity,
            FindingCategory.Correctness,
            malformed ? new FindingEvidenceRequest([], Mutation: new("src/Target.cs", "true", "false")) : null);

    [Fact]
    public void AdvisoryMutationIsDroppedWhileValidBlockingFindingSurvives()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            Finding("optional", FindingSeverity.Advisory, malformed: true),
            Finding("blocker", FindingSeverity.Blocking)
        });
        var result = ReviewFindingConvergence.ParseJson(json, "[]");

        Assert.Null(result.FatalDiagnostic);
        Assert.Equal("blocker", Assert.Single(result.Round.Findings).StableId);
        var warning = Assert.Single(result.AdvisoryRejections);
        Assert.Equal(0, warning.ItemIndex);
        Assert.Equal("optional", warning.StableId);
        Assert.Equal(FindingSeverity.Advisory, warning.Severity);
        Assert.Equal("mutation requires negative_control 'revert-src'", warning.Reason);
    }

    [Theory]
    [InlineData("\"severity\":\"advisory\",", false)]
    [InlineData("\"severity\":1,", false)]
    [InlineData("", true)]
    [InlineData("\"severity\":\"unknown\",", true)]
    [InlineData("\"severity\":999,", true)]
    [InlineData("\"severity\":\"999\",", true)]
    [InlineData("\"severity\":\"blocking, advisory\",", true)]
    [InlineData("\"severity\":\"advisory, blocking\",", true)]
    [InlineData("\"severity\":\"advisory, advisory\",", true)]
    [InlineData("\"severity\":null,", true)]
    [InlineData("\"severity\":{},", true)]
    [InlineData("\"severity\":\"blocking\",\"Severity\":\"advisory\",", true)]
    public void InvalidItemRequiresUnambiguousAdvisorySeverity(string severity, bool fatal)
    {
        var json = "[{" + severity + "\"stable_id\":\"invalid\",\"state\":\"bogus\"}]";
        var result = ReviewFindingConvergence.ParseJson(json, "[]");
        Assert.Equal(fatal, result.FatalDiagnostic is not null);
        Assert.Equal(fatal ? 0 : 1, result.AdvisoryRejections.Count);
        Assert.Empty(result.Round.Findings);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("\"999\"")]
    [InlineData("-1")]
    [InlineData("\"-1\"")]
    [InlineData("\"unknown\"")]
    [InlineData("\"blocking, advisory\"")]
    [InlineData("\"advisory, blocking\"")]
    [InlineData("\"advisory, advisory\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void UnknownSeverityOnValidFindingDefaultsToBlocking(string severity)
    {
        var json = FindingJsonWithSeverity(severity);
        var result = ReviewFindingConvergence.ParseJson(json, "[]");

        Assert.Null(result.FatalDiagnostic);
        Assert.Empty(result.AdvisoryRejections);
        var finding = Assert.Single(result.Round.Findings);
        Assert.Equal("unknown-severity", finding.StableId);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
        Assert.Equal(finding, Assert.Single(ReviewFindings.GetOpenBlockingFindings(result.Round.Findings, [])));
    }

    [Theory]
    [InlineData("999", true)]
    [InlineData("\"999\"", true)]
    [InlineData("999", false)]
    [InlineData("\"999\"", false)]
    [InlineData("\"blocking, advisory\"", true)]
    [InlineData("\"blocking, advisory\"", false)]
    [InlineData("\"advisory, blocking\"", true)]
    [InlineData("\"advisory, blocking\"", false)]
    [InlineData("\"advisory, advisory\"", true)]
    [InlineData("\"advisory, advisory\"", false)]
    public void UndefinedSeverityBlocksPassingCompletion(string severity, bool dispatchCompletion)
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        RecordJson(kernel, goal, reviewer, FindingJsonWithSeverity(severity), "pass", dispatchCompletion);

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        var finding = Assert.Single(reviewer.LastVerification.MergedReviewFindings!);
        Assert.Equal("unknown-severity", finding.StableId);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
        Assert.Equal(finding, Assert.Single(ReviewFindings.GetOpenBlockingFindings(
            reviewer.LastVerification.MergedReviewFindings!, goal.EffectiveAcceptanceCriteriaCorrections)));
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskCompleted);
    }

    [Theory]
    [InlineData("\"blocking\"", FindingSeverity.Blocking)]
    [InlineData("\"BLOCKING\"", FindingSeverity.Blocking)]
    [InlineData("0", FindingSeverity.Blocking)]
    [InlineData("\"0\"", FindingSeverity.Blocking)]
    [InlineData("\"advisory\"", FindingSeverity.Advisory)]
    [InlineData("\"ADVISORY\"", FindingSeverity.Advisory)]
    [InlineData("1", FindingSeverity.Advisory)]
    [InlineData("\"1\"", FindingSeverity.Advisory)]
    public void SingleRecognizedSeverityPreservesValidFinding(string severity, FindingSeverity expected)
    {
        var result = ReviewFindingConvergence.ParseJson(FindingJsonWithSeverity(severity), "[]");

        Assert.Null(result.FatalDiagnostic);
        Assert.Empty(result.AdvisoryRejections);
        var finding = Assert.Single(result.Round.Findings);
        Assert.Equal("unknown-severity", finding.StableId);
        Assert.Equal(expected, finding.Severity);
    }

    [Theory]
    [InlineData("\"blocking, advisory\"", true)]
    [InlineData("\"blocking, advisory\"", false)]
    [InlineData("\"advisory, blocking\"", true)]
    [InlineData("\"advisory, blocking\"", false)]
    public void AmbiguousSeverityOnMalformedMutationRemainsFatal(string severity, bool dispatchCompletion)
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        var json = FindingJsonWithSeverity(severity, malformed: true);
        var parsed = ReviewFindingConvergence.ParseJson(json, "[]");
        Assert.NotNull(parsed.FatalDiagnostic);
        Assert.Empty(parsed.AdvisoryRejections);
        RecordJson(kernel, goal, reviewer, json, "pass", dispatchCompletion);

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(ReviewFindingConvergence.FindingFormatViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        Assert.Contains("mutation requires negative_control 'revert-src'",
            reviewer.LastVerification.ReviewFindingContractViolation.Message);
        Assert.Empty(reviewer.LastVerification.MergedReviewFindings!);
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskCompleted);
    }

    [Theory]
    [InlineData("null", "[]")]
    [InlineData("{}", "[]")]
    [InlineData("[", "[]")]
    [InlineData("[]", "null")]
    [InlineData("[]", "[null]")]
    [InlineData("[]", "[{\"file\":\"missing-region\"}]")]
    [InlineData("[null]", "[]")]
    public void InvalidRoundEnvelopeRemainsFatal(string findings, string anchors)
    {
        Assert.False(ReviewFindingConvergence.TryParseJson(findings, anchors, out _, out var diagnostic));
        Assert.NotEmpty(diagnostic);
    }

    [Theory]
    [InlineData(FindingSeverity.Advisory, false)]
    [InlineData(FindingSeverity.Blocking, true)]
    public void DuplicateGroupCannotHideBlockingFinding(FindingSeverity secondSeverity, bool fatal)
    {
        var json = JsonSerializer.Serialize(new[]
        {
            Finding("duplicate", FindingSeverity.Advisory), Finding("duplicate", secondSeverity),
            Finding("unrelated", FindingSeverity.Blocking)
        });
        var result = ReviewFindingConvergence.ParseJson(json, "[]");
        Assert.Equal(fatal, result.FatalDiagnostic is not null);
        Assert.Equal("unrelated", Assert.Single(result.Round.Findings).StableId);
        Assert.Equal(fatal ? 0 : 2, result.AdvisoryRejections.Count);
    }

    [Fact]
    public void PassingReviewCompletesAndPersistsAdvisoryWarning()
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        Record(kernel, goal, reviewer, [Finding("optional", FindingSeverity.Advisory, malformed: true)], "pass");

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Empty(reviewer.LastVerification.MergedReviewFindings!);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).Goals.Single();
        var warning = Assert.Single(restored.Timeline.Where(evt => evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote && evt.Message.StartsWith("Dropped malformed Reviewer advisory finding:")));
        Assert.Contains("item_index=0", warning.Message);
        Assert.Contains("stable_id=optional", warning.Message);
        Assert.Contains("severity=advisory", warning.Message);
        Assert.Contains("mutation requires negative_control 'revert-src'", warning.Message);
    }

    [Fact]
    public void ValidBlockerStillBlocksAlongsideRejectedAdvisory()
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        Record(kernel, goal, reviewer,
            [Finding("optional", FindingSeverity.Advisory, malformed: true),
             Finding("blocker", FindingSeverity.Blocking), Finding("valid-advisory", FindingSeverity.Advisory)],
            "needs-work");

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(new[] { "blocker", "valid-advisory" },
            reviewer.LastVerification.MergedReviewFindings!.Select(finding => finding.StableId));
        Assert.Equal("blocker", Assert.Single(ReviewFindings.GetOpenBlockingFindings(
            reviewer.LastVerification.MergedReviewFindings!, goal.EffectiveAcceptanceCriteriaCorrections)).StableId);
        Assert.Contains(goal.Timeline, evt => evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith("Dropped malformed Reviewer advisory finding:") &&
            evt.Message.Contains("stable_id=optional"));
    }

    [Fact]
    public void DroppedAdvisoryCannotResolvePriorBlockingLedgerEntry()
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        Record(kernel, goal, reviewer, [Finding("prior", FindingSeverity.Blocking)], "needs-work");
        kernel.RetryTask(goal.Id, reviewer.Id, "review again");
        Record(kernel, goal, reviewer,
            [Finding("prior", FindingSeverity.Advisory, malformed: true) with { State = ReviewFindingState.Resolved }], "pass");

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(ReviewFindingConvergence.OmittedOpenFindingViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        var prior = Assert.Single(reviewer.LastVerification!.MergedReviewFindings!);
        Assert.Equal("prior", prior.StableId);
        Assert.Equal(FindingSeverity.Blocking, prior.Severity);
        Assert.Equal(ReviewFindingState.Open, prior.State);
    }

    [Theory]
    [InlineData(true, ReviewFindingState.Open)]
    [InlineData(false, ReviewFindingState.Open)]
    [InlineData(true, ReviewFindingState.Resolved)]
    [InlineData(false, ReviewFindingState.Resolved)]
    public void RejectedCarriedAdvisoryAllowsPassWithoutLedgerTransition(
        bool dispatchCompletion, ReviewFindingState submittedState)
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        var carried = Finding("carried", FindingSeverity.Advisory);
        Record(kernel, goal, reviewer, [carried], "pass");
        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        kernel.RetryTask(goal.Id, reviewer.Id, "review again");
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.Goals.Single();
        reviewer = goal.Tasks.Single();
        var valid = Finding("valid-advisory", FindingSeverity.Advisory) with
        {
            Location = new("src/Other.cs", "Run")
        };
        Record(kernel, goal, reviewer,
            [Finding("carried", FindingSeverity.Advisory, malformed: true) with { State = submittedState }, valid],
            "pass", dispatchCompletion);

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(new[] { carried, valid }, reviewer.LastVerification.MergedReviewFindings!);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).Goals.Single();
        Assert.Equal(new[] { carried, valid }, restored.Tasks.Single().LastVerification!.MergedReviewFindings!);
        var warning = Assert.Single(restored.Timeline, evt => evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith("Dropped malformed Reviewer advisory finding:", StringComparison.Ordinal));
        Assert.Contains("stable_id=carried", warning.Message);
        Assert.Contains("mutation requires negative_control 'revert-src'", warning.Message);
    }

    [Fact]
    public void RejectedCarriedAdvisoryCannotCoverOmittedPriorBlocker()
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        var carried = Finding("carried", FindingSeverity.Advisory);
        var blocker = Finding("blocker", FindingSeverity.Blocking) with { Location = new("src/Blocker.cs", "Run") };
        Record(kernel, goal, reviewer, [carried, blocker], "needs-work");
        kernel.RetryTask(goal.Id, reviewer.Id, "review again");
        Record(kernel, goal, reviewer, [Finding("carried", FindingSeverity.Advisory, malformed: true)], "pass");

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var violation = reviewer.LastVerification!.ReviewFindingContractViolation!;
        Assert.Equal(ReviewFindingConvergence.OmittedOpenFindingViolationCode, violation.Code);
        Assert.Equal("blocker", Assert.Single(violation.IdentityMismatches!).PriorStableId);
        Assert.Equal(new[] { blocker, carried }, reviewer.LastVerification.MergedReviewFindings!);
    }

    [Fact]
    public void BlockingFormatFailurePreservesAcceptedLedger()
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        Record(kernel, goal, reviewer, [Finding("prior", FindingSeverity.Blocking)], "needs-work");
        kernel.RetryTask(goal.Id, reviewer.Id, "review again");
        Record(kernel, goal, reviewer, [Finding("malformed", FindingSeverity.Blocking, malformed: true)], "pass");

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(ReviewFindingConvergence.FindingFormatViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        Assert.Equal("prior", Assert.Single(reviewer.LastVerification.MergedReviewFindings!).StableId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassingVerdictCannotCompleteMalformedBlockerWithEmptyAcceptedLedger(bool dispatchCompletion)
    {
        var (kernel, goal, reviewer) = ReviewGoal();
        Record(kernel, goal, reviewer, [Finding("malformed", FindingSeverity.Blocking, malformed: true)],
            "pass", dispatchCompletion);

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(ReviewFindingConvergence.FindingFormatViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        Assert.Empty(reviewer.LastVerification.MergedReviewFindings!);
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskCompleted);
    }

    [Fact]
    public void MapperIsolatesAdvisoryAndKeepsSystemOwnedAnchorAndOutcomeAuthority()
    {
        var valid = Finding("blocker", FindingSeverity.Blocking) with
        {
            EvidenceOutcome = new FindingEvidenceOutcome(true, "forged-receipt")
        };
        var json = JsonSerializer.Serialize(new[]
        {
            Finding("optional", FindingSeverity.Advisory, malformed: true), valid
        });
        var authoritative = new ReviewFindingLocation("src/System.cs", "Verified");
        var output = "WORKER_RESULT:\nfindings: " + json +
            "\ntouched_anchors: [{\"file\":\"src/Worker.cs\",\"region\":\"Claimed\"}]\nEND_WORKER_RESULT";
        var verification = new TaskVerificationRecord("review", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewFindingTouchedAnchors: [authoritative]);

        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round,
            out var diagnostic, out var warnings, out var violation), diagnostic);
        Assert.Null(violation);
        Assert.Single(warnings);
        Assert.Equal(authoritative, Assert.Single(round.TouchedAnchors));
        Assert.Null(Assert.Single(round.Findings).EvidenceOutcome);
    }

    private static (AgentOrchestratorKernel, Goal, TaskSpec) ReviewGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review findings", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Severity-aware findings", [reviewer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (kernel, goal, reviewer);
    }

    private static void Record(AgentOrchestratorKernel kernel, Goal goal, TaskSpec reviewer,
        IReadOnlyList<ReviewFinding> findings, string verdict, bool dispatchCompletion = true)
        => RecordJson(kernel, goal, reviewer, JsonSerializer.Serialize(findings), verdict, dispatchCompletion);

    private static string FindingJsonWithSeverity(string severity, bool malformed = false)
    {
        var finding = JsonSerializer.SerializeToElement(Finding("unknown-severity", FindingSeverity.Blocking, malformed));
        return "[{" + string.Join(",", finding.EnumerateObject().Select(property =>
            JsonSerializer.Serialize(property.Name) + ":" +
            (property.Name == "severity" ? severity : property.Value.GetRawText()))) + "}]";
    }

    private static void RecordJson(AgentOrchestratorKernel kernel, Goal goal, TaskSpec reviewer,
        string findingsJson, string verdict, bool dispatchCompletion = true)
    {
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id,
            new("reviewer", "review", "C:\\tmp", DateTimeOffset.UtcNow));
        var output = string.Join("\n", "WORKER_RESULT:", "files: none", "commands: none", "tests: pass",
            "commit: none", "blockers: none", $"verdict: {verdict}",
            "findings: " + findingsJson, "touched_anchors: []", "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord("review", "C:\\tmp", 0, output, "",
            DateTimeOffset.UtcNow, WorkerResultPresent: true);
        if (dispatchCompletion)
            kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
        else
            kernel.RecordTaskVerification(goal.Id, reviewer.Id, verification);
    }
}
