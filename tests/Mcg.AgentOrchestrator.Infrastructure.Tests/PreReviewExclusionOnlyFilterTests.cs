using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

// Parallel-safe: in-memory plans and kernel; all driver effects and logging are injected.
public sealed class PreReviewExclusionOnlyFilterTests
{
    private const string Candidate = "exclusion-candidate-sha";
    private const string Summary = "Controlled pre-review selection.";
    private const string Reason = "exclusion-only lane filter; the acceptance gate runs the full lane";
    private const string InfrastructureProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string CoreProject =
        "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";

    [Fact]
    public void ExclusionOnlyCheck_IsNotApplicable_AndRoutesToReviewer()
    {
        var filter = string.Join('&', Enumerable.Range(0, 70).Select(i => $"FullyQualifiedName!~OtherTestSuite{i}"));
        Assert.True(filter.Length > PreReviewFocusedRequestSplitter.MaxFocusedEvidenceFilterLength);
        var check = Check("Remainder", filter);
        var logs = new List<string>();
        var context = Build(logs, check);
        AssertNotApplicable(context, check.Name, logs);

        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var dispatches = 0;
        var retries = 0;
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => context,
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retries++;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ =>
            {
                dispatches++;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, message) => escalations.Add(message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, dispatches);
        Assert.Equal(0, focusedRuns);
        Assert.Equal(0, retries);
        Assert.Empty(escalations);
        Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(context.MappingReason, reviewer.PreReviewEvidenceReceipt?.MappingReason);
    }

    [Theory]
    [InlineData("FullyQualifiedName!~OtherTests")]
    [InlineData("(FullyQualifiedName!~OtherTests|Category!=Integration)&Category!=Slow")]
    public void ShortExclusionOnlyCheck_IsNotApplicable(string filter)
    {
        Assert.True(filter.Length < PreReviewFocusedRequestSplitter.MaxFocusedEvidenceFilterLength);
        var logs = new List<string>();
        var context = Build(logs, Check("short exclusions", filter));

        AssertNotApplicable(context, "short exclusions", logs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MixedPlan_KeepsPositiveRequestsUnchanged_AndMarksOnlyExclusionCheck(
        bool exclusionFirst, bool oversizedPositive)
    {
        var positiveFilter = oversizedPositive
            ? string.Join('|', Enumerable.Range(0, 70).Select(i => $"FullyQualifiedName~SomeCoreTests{i}"))
            : "FullyQualifiedName~SomeCoreTests";
        var positive = Check("positive core tests", positiveFilter, CoreProject);
        var exclusion = Check("Remainder", "FullyQualifiedName!~OtherTests");
        var baselineLogs = new List<string>();
        var baseline = Build(baselineLogs, positive);
        var logs = new List<string>();
        var mixed = Build(logs, exclusionFirst ? [exclusion, positive] : [positive, exclusion]);

        Assert.False(baseline.MappingNeedsInput);
        Assert.False(baseline.NoApplicableTests);
        Assert.NotEmpty(baseline.SelectedFocusedTests);
        Assert.Equal(oversizedPositive, baseline.SelectedFocusedTests.Count > 1);
        Assert.Equal(Summary, baseline.MappingReason);
        Assert.Empty(baselineLogs);
        Assert.False(mixed.MappingNeedsInput);
        Assert.False(mixed.NoApplicableTests);
        Assert.Equal(baseline.SelectedFocusedTests, mixed.SelectedFocusedTests);
        Assert.Equal(baseline.FocusedRequest, mixed.FocusedRequest);
        Assert.Equal($"{Summary} not-applicable check=\"Remainder\" reason=\"{Reason}\"", mixed.MappingReason);
        Assert.DoesNotContain(positive.Name, mixed.MappingReason);
        Assert.Equal($"PRE_REVIEW_CHECK_NOT_APPLICABLE candidate={Candidate} check=\"Remainder\" reason=\"{Reason}\"",
            Assert.Single(logs));
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("unsupported-project")]
    [InlineData("empty-filter")]
    public void ExistingMappingNeedsInputCases_AreUnchanged(string invalidCase)
    {
        var check = invalidCase switch
        {
            "missing-project" => new RepositoryTestImpactCheck("missing", ["dotnet", "test", "--filter", "FullyQualifiedName!~OtherTests"], "controlled"),
            "unsupported-project" => Check("unsupported", "FullyQualifiedName!~OtherTests", "tests/Widget.Tests/Widget.Tests.csproj"),
            _ => new RepositoryTestImpactCheck("empty", ["dotnet", "test", "--project", InfrastructureProject, "--filter"], "controlled")
        };
        var logs = new List<string>();
        var context = Build(logs, check);
        var expectedReason = invalidCase switch
        {
            "missing-project" => Summary,
            "unsupported-project" => "Mapped project is not supported by the focused evidence broker: tests/Widget.Tests/Widget.Tests.csproj",
            _ => $"Mapped test command has an empty --filter argument: {check.CommandLine}"
        };

        Assert.True(context.MappingNeedsInput);
        Assert.False(context.NoApplicableTests);
        Assert.Null(context.FocusedRequest);
        Assert.Equal(new[] { check.CommandLine }, context.SelectedFocusedTests);
        Assert.Equal(expectedReason, context.MappingReason);
        Assert.Empty(logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonExclusionFilters_KeepExistingBehavior(bool oversized)
    {
        var filter = oversized
            ? $"({string.Join('|', Enumerable.Range(0, 70).Select(i => $"FullyQualifiedName~PositiveTests{i}"))})&FullyQualifiedName~Required"
            : "FullyQualifiedName~PositiveTests&FullyQualifiedName!~OtherTests";
        var logs = new List<string>();
        var check = Check("positive selection", filter);
        var context = Build(logs, check);

        Assert.Equal(oversized, context.MappingNeedsInput);
        Assert.False(context.NoApplicableTests);
        Assert.Empty(logs);
        if (oversized)
        {
            Assert.True(filter.Length > PreReviewFocusedRequestSplitter.MaxFocusedEvidenceFilterLength);
            Assert.Null(context.FocusedRequest);
            Assert.Equal("Mapped test filter cannot be split into broker-safe positive clauses.", context.MappingReason);
            Assert.Equal(new[] { check.CommandLine }, context.SelectedFocusedTests);
        }
        else
        {
            Assert.Equal($"Infrastructure.Tests: {filter}", context.FocusedRequest);
            Assert.Equal(context.FocusedRequest, Assert.Single(context.SelectedFocusedTests));
            Assert.Equal(Summary, context.MappingReason);
        }
    }

    [Fact]
    public void MixedPlan_MappingFailureStillRequiresInput()
    {
        var logs = new List<string>();
        var invalid = Check("unsupported", "FullyQualifiedName~PositiveTests", "tests/Widget.Tests/Widget.Tests.csproj");
        var context = Build(logs, Check("Remainder", "FullyQualifiedName!~OtherTests"), invalid);

        Assert.True(context.MappingNeedsInput);
        Assert.False(context.NoApplicableTests);
        Assert.Null(context.FocusedRequest);
        Assert.StartsWith("Mapped project is not supported by the focused evidence broker:", context.MappingReason);
        Assert.Contains($"not-applicable check=\"Remainder\" reason=\"{Reason}\"", context.MappingReason);
        Assert.Single(logs);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("FullyQualifiedName!~OtherTests", true)]
    [InlineData("FullyQualifiedName!=SomeTests", true)]
    [InlineData("FullyQualifiedName!~One&FullyQualifiedName!~Two", true)]
    [InlineData(" ( FullyQualifiedName !~ One | Category != Integration ) & Category!=Slow ", true)]
    [InlineData("((Category!=Integration))", true)]
    [InlineData("FullyQualifiedName~PositiveTests", false)]
    [InlineData("Category=Integration", false)]
    [InlineData("FullyQualifiedName~PositiveTests&Category!=Slow", false)]
    [InlineData("Category!=Slow|FullyQualifiedName~PositiveTests", false)]
    [InlineData("(Category!=Slow", false)]
    [InlineData("Category!=Slow)", false)]
    [InlineData("Category!=Slow&", false)]
    [InlineData("|Category!=Slow", false)]
    [InlineData("Category!=Slow||Category!=Fast", false)]
    [InlineData("()", false)]
    [InlineData("Category!=Slow|()", false)]
    [InlineData("Category!=Slow(Category!=Fast)", false)]
    [InlineData("(Category!=Slow)(Category!=Fast)", false)]
    [InlineData("FullyQualifiedName!~", false)]
    [InlineData("Category!=Slow Category!=Fast", false)]
    [InlineData("FullyQualifiedName", false)]
    [InlineData("!Category=Slow", false)]
    [InlineData("Category<>Slow", false)]
    public void Detector_ClassifiesFilters(string? filter, bool expected)
    {
        Assert.Equal(expected, PreReviewExclusionOnlyFilter.IsExclusionOnly(filter));
    }

    private static RepositoryTestImpactCheck Check(string name, string filter, string project = InfrastructureProject) =>
        new(name, ["dotnet", "test", "--project", project, "--filter", filter], "Controlled mapped check.");

    private static PreReviewEvidenceContext Build(List<string> logs, params RepositoryTestImpactCheck[] checks) =>
        PreReviewEvidenceContextBuilder.Build(Candidate,
            RepositoryChangeClassifier.Classify(["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.cs"]),
            new RepositoryTestImpactPlan(true, false, Summary, checks), logs.Add);

    private static void AssertNotApplicable(PreReviewEvidenceContext context, string checkName, List<string> logs)
    {
        Assert.False(context.MappingNeedsInput);
        Assert.True(context.NoApplicableTests);
        Assert.Empty(context.SelectedFocusedTests);
        Assert.Null(context.FocusedRequest);
        Assert.Equal($"{Summary} not-applicable check=\"{checkName}\" reason=\"{Reason}\"", context.MappingReason);
        Assert.Equal($"PRE_REVIEW_CHECK_NOT_APPLICABLE candidate={Candidate} check=\"{checkName}\" reason=\"{Reason}\"",
            Assert.Single(logs));
    }
}
