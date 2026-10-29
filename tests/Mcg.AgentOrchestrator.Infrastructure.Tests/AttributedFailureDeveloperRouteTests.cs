using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Every fixture owns its kernel and temporary paths; no shared process or repository state.
public sealed class AttributedFailureDeveloperRouteTests
{
    internal const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string Main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Marker = "ATTRIBUTED_FAILURE_ROUTE ";
    internal static readonly string[] CohortTests =
    [
        "WorkflowDecisionCoverageRatchetTests.EveryAllowListEntry_StillNamesAnUndecidedSite",
        "WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember"
    ];

    [Fact]
    public void CohortProducerParsesPartitionAndReproducedTests()
    {
        var (kernel, first, second) = CohortGoals();
        var verdict = ApplyCohort(kernel, first, second);
        Assert.True(verdict.ShouldRecord);
        var failure = Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(second.LatestAcceptanceFailure));
        Assert.Equal("cohort", failure.Source);
        Assert.Equal("partition-1", failure.ReceiptId);
        Assert.Equal(Candidate, failure.CandidateSha);
        Assert.Equal(CohortTests, failure.FailingTests);
    }

    [Theory]
    [InlineData("ownership", "train-ownership")]
    [InlineData("reach", "train-reach")]
    [InlineData("impact", null)]
    public void TrainProducerParsesOnlyOwnershipAndReach(string source, string? expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            ApplyTrain(kernel, goal, root, source);
            var parsed = AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure);
            if (expected is null) Assert.Null(parsed);
            else
            {
                Assert.NotNull(parsed);
                Assert.Equal(expected, parsed.Source);
                Assert.Equal("train-red", parsed.ReceiptId);
                Assert.Equal(Candidate, parsed.CandidateSha);
                Assert.Equal(new[] { "AlphaTests", "ZebraTests" }, parsed.FailingTests);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("solo acceptance failed")]
    public void OtherProducerEvidenceDeclines(string evidence)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, evidence: evidence);
        Assert.Null(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure));
    }

    [Fact]
    public void FollowerChargeRecordDeclines()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        kernel.BeginGoalAcceptanceVerification(goal.Id, "Reconciling follower gate receipt follower-red.");
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["follower-gate"],
            "Follower gate receipt follower-red failed after its leader landed exactly.", Candidate, Main));
        Assert.Null(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure));
    }

    [Fact]
    public void RepeatBoundUsesWholeTokensAndIsSharedAcrossSources()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var developer = goal.Tasks.Single();
        var failure = new AttributedFailure("cohort", "partition-1", Candidate, ["Tests.Fails"]);
        var first = AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate);
        kernel.RetryTaskAutomatically(goal.Id, developer.Id,
            $"{Marker}source=cohort candidate_sha={Candidate}suffix receipt_id=r signature={first.Signature}", RetryCause.NewTestFinding);
        PassVerification(kernel, goal, developer);
        Assert.True(AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate).IsRoute);
        kernel.RetryTaskAutomatically(goal.Id, developer.Id,
            $"{Marker}source=cohort candidate_sha={Candidate} receipt_id=r signature={first.Signature}suffix", RetryCause.NewTestFinding);
        PassVerification(kernel, goal, developer);
        var train = AttributedFailureDeveloperRoute.Decide(goal, failure with { Source = "train-reach" }, Candidate);
        Assert.True(train.IsRoute);
        kernel.RetryTaskAutomatically(goal.Id, developer.Id, train.Feedback!, RetryCause.NewTestFinding);
        PassVerification(kernel, goal, developer);
        Assert.Equal("already-routed", AttributedFailureDeveloperRoute.Decide(goal, failure with { Source = "tester-receipt" }, Candidate).DeclineReason);
    }

    [Fact]
    public void FeedbackPreservesCriteriaAndCanonicalSignature()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var criteria = new[] { "Keep the exact behavior `A(B)`.", "Preserve Unicode: α and punctuation." };
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, criteria, VerificationClass.TestVerifiable, [], []));
        var failure = new AttributedFailure("cohort", "partition-1", Candidate, ["ZTests.Fails", "ATests.Fails", "ZTests.Fails"]);
        var decision = AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate);
        Assert.True(decision.IsRoute);
        Assert.Equal(goal.Tasks.Single().Id, decision.DeveloperTaskId);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Candidate + "\nATests.Fails,ZTests.Fails"))).ToLowerInvariant()[..16];
        Assert.Equal(expected, decision.Signature);
        var lines = decision.Feedback!.Split(Environment.NewLine);
        Assert.Equal($"{Marker}source=cohort candidate_sha={Candidate} receipt_id=partition-1 signature={expected}", lines[0]);
        var match = Regex.Match(lines[1], @"failing_tests=(?<identities>[^\s;]+)");
        Assert.True(match.Success);
        Assert.Equal(new[] { "ATests.Fails", "ZTests.Fails" }, match.Groups["identities"].Value.Split(','));
        Assert.Equal(criteria, lines.Skip(2).Take(criteria.Length));
        var steps = new[] { "1. Read the named failing tests", "2. Fix the code, or fix the test", "3. List every failing test's class" };
        var positions = steps.Select(step => decision.Feedback.IndexOf(step, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2]);
        Assert.Contains("never weaken, loosen or remove an assertion to make it pass.", decision.Feedback);
        Assert.Equal(expected, AttributedFailureDeveloperRoute.Decide(goal, failure with { FailingTests = ["ATests.Fails", "ZTests.Fails"] }, Candidate).Signature);
        Assert.NotEqual(expected, AttributedFailureDeveloperRoute.Decide(goal, failure with { FailingTests = ["Other.Fails"] }, Candidate).Signature);
        Assert.NotEqual(expected, AttributedFailureDeveloperRoute.Decide(goal, failure with { CandidateSha = Main }, Main).Signature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tests.Has space")]
    [InlineData("Tests.Has,comma")]
    [InlineData("Tests.Has;semicolon")]
    [InlineData("Tests.M(role: Planner")]
    [InlineData("Tests.M(a: 1")]
    [InlineData("b: 2)")]
    [InlineData("Tests.M[role: Planner")]
    [InlineData("Tests.M(role: Planner) suffix")]
    [InlineData("Tests.M(role: Planner\n)")]
    [InlineData("Tests.M(role: (Planner)")]
    [InlineData("Tests.M(a: 1, b: 2)")]
    [InlineData("Tests.M(role: Planner;)")]
    public void UnrepresentableIdentityDeclines(string identity)
    {
        var goal = CompletedGoal(new AgentOrchestratorKernel());
        Assert.False(AttributedFailureDeveloperRoute.Decide(goal, new("cohort", "receipt", Candidate, [identity]), Candidate).IsRoute);
    }

    [Theory]
    [InlineData("Tests.M(role: Planner)")]
    [InlineData("Tests.M[role: Developer]")]
    public void CompleteParameterizedIdentityRoutes(string identity)
    {
        var goal = CompletedGoal(new AgentOrchestratorKernel());
        var decision = AttributedFailureDeveloperRoute.Decide(goal,
            new("cohort", "receipt", Candidate, [identity]), Candidate);
        Assert.True(decision.IsRoute);
        Assert.Contains("failing_tests=" + identity + Environment.NewLine, decision.Feedback);
    }

    [Fact]
    public void MissingFailureHeadAndCommittedDeveloperDecline()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel, committed: false);
        var failure = new AttributedFailure("cohort", "r", Candidate, ["Tests.Fails"]);
        Assert.False(AttributedFailureDeveloperRoute.Decide(goal, null, Candidate).IsRoute);
        Assert.False(AttributedFailureDeveloperRoute.Decide(goal, failure, null).IsRoute);
        Assert.False(AttributedFailureDeveloperRoute.Decide(goal, failure, Main).IsRoute);
        Assert.False(AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate).IsRoute);
        Assert.False(AttributedFailureDeveloperRoute.Decide(goal, failure with { FailingTests = [] }, Candidate).IsRoute);
    }

    internal static Goal CompletedGoal(AgentOrchestratorKernel kernel, string? root = null, bool committed = true)
    {
        var goal = kernel.CreateGoal("Implement the attributed fix", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        // Repository scenarios supply real heads; in-memory fixtures need durable output across snapshots.
        DispatchTask(kernel, goal, goal.Tasks.Single(), workingDirectory: root ?? Path.GetTempPath(), baseCommit: root is null ? Main : null);
        if (committed && root is null) kernel.RecordDispatchResultCommit(goal.Id, goal.Tasks.Single().Id, Candidate);
        kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
            new TaskVerificationRecord("fixture", root ?? Path.GetTempPath(), 0, "ok", "", DateTimeOffset.UtcNow, HasCommittedChanges: committed));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return goal;
    }

    internal static (AgentOrchestratorKernel Kernel, Goal First, Goal Second) CohortGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        return (kernel, CompletedGoal(kernel), CompletedGoal(kernel));
    }

    internal static ConductorAcceptanceCohortAttributionVerdict ApplyCohort(
        AgentOrchestratorKernel kernel, Goal first, Goal second, bool interactionOnly = false)
    {
        var bindings = new[] { Binding(first, Main), Binding(second, Candidate) };
        var identity = AcceptanceCohortIdentity.Create(bindings, Main, new string('e', 40), "manifest-v1");
        var cohort = new AcceptanceCohortReceipt("combined-red", identity, AcceptanceCohortGateOutcome.Failed,
            DateTimeOffset.UtcNow, 1, ["combined"], 1, [], interactionOnly
                ? AcceptanceCohortAttributionOutcome.InteractionOnly : AcceptanceCohortAttributionOutcome.SecondMemberFailed)
        {
            AttributedMembers = interactionOnly ? [] : [new(second.Id, 1, Candidate, CohortTests.Reverse().ToArray())]
        };
        var partition = new AcceptanceCohortPartitionReceipt("partition-1", second.Id, 1, Candidate, Main,
            new string('d', 40), "manifest-v1", AcceptanceCohortGateOutcome.Failed, 1, [])
        { FailedChecks = CohortTests, FailingTestIdentities = CohortTests };
        return ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, second, partition, Candidate, Main, false,
            (goal, checks, branch, main, attributions) => kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions), _ => { });
    }

    private static AcceptanceCohortMemberBinding Binding(Goal goal, string candidate) =>
        new(goal.Id, candidate, candidate, ["docs/" + goal.Id.Value + ".md"], ["resource:" + goal.Id.Value],
            ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    internal static void ApplyTrain(AgentOrchestratorKernel kernel, Goal goal, string root, string source)
    {
        var member = new MergeTrainMemberBinding(goal.Id, Candidate, Candidate, ["src/Changed.cs"], ["resource"], ChangeRiskTier.DocsOnly,
            ConductorTransitionDecision.Auto, "ready", "fixture", RebasedTrainHeadRevision: Candidate);
        var other = member with { GoalId = GoalId.New() };
        var receipt = new MergeTrainReceipt("train-red", MergeTrainIdentity.Create([member, other], Main, new string('d', 40), "manifest-v1"),
            MergeTrainGateOutcome.Failed, DateTimeOffset.UtcNow, 1, ["gate"], 1,
            MergeTrainReachAttributionTests.Trx(root, "ZebraTests.Fails", "AlphaTests.Fails"));
        MergeTrainAttributedFailure.Apply(receipt, member, goal, source, 2, [], _ => { },
            (target, checks, branch, main, attributions, baseline) => kernel.RecordAcceptanceFailure(target.Id, checks, branch, main, attributions, baseline),
            kernel.RouteRecordedAcceptanceFailure);
    }

    internal static void RecordCohortFailure(AgentOrchestratorKernel kernel, Goal goal, string candidate = Candidate,
        string? evidence = null, AcceptanceFailureCause cause = AcceptanceFailureCause.NotClassified)
    {
        kernel.RecordAcceptanceFailure(goal.Id, CohortTests, candidate, Main,
            CohortTests.Select(test => new AcceptanceCheckAttribution(test, AcceptanceFailureOrigin.Introduced,
                evidence ?? $"cohort-attribution=combined-red cohort-partition=partition-1 reproduced={string.Join(',', CohortTests)}", cause)).ToArray());
        if (goal.Status == GoalStatus.Verified)
            Assert.True(kernel.RouteRecordedAcceptanceFailure(goal.Id, "Attributed failure recorded."));
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
    }

    internal static ProgressEvent[] MarkerRetries(Goal goal) => goal.Timeline
        .Where(evt => evt.Kind == ProgressKind.TaskRetried && evt.Message.StartsWith(Marker, StringComparison.Ordinal)).ToArray();

    internal sealed class RouteHarness(AgentOrchestratorKernel kernel, string candidate = Candidate)
    {
        internal string CurrentCandidate { get; set; } = candidate;
        internal List<TaskId> Dispatches { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal FailedGoalRecoveryDecision? RecoveryEffectDecision { get; private set; }
        internal ConductorDriver Configure(Func<Goal, string, FocusedEvidenceRunResult>? evidence = null)
        {
            TaskId? retryTarget = null;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(true, false, false, false, false, false),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CurrentCandidate),
                dispatchAndStart: goal =>
                {
                    var task = goal.Tasks.Single(item => item.Id == retryTarget);
                    Dispatches.Add(task.Id);
                    DispatchTask(kernel, goal, task);
                    var dispatch = task.LastDispatch!;
                    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new(12345, dispatch.Command, dispatch.WorkingDirectory,
                        "fixture.stdout", "fixture.stderr", "fixture.exit", DateTimeOffset.UtcNow, null, null));
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goal, task, message, round, cause) =>
                {
                    if (message.StartsWith(Marker, StringComparison.Ordinal))
                    {
                        Assert.Null(round);
                        Assert.Equal(RetryCause.NewTestFinding, cause);
                        Assert.Equal(AgentRole.Developer, kernel.GetGoal(goal).Tasks.Single(item => item.Id == task).RequiredRole);
                    }
                    var retried = kernel.RetryTaskAutomatically(goal, task, message, cause, retryRoundKind: round);
                    retryTarget = task;
                    return retried;
                },
                runFocusedEvidence: evidence,
                recordFindingEvidenceRequest: (goal, task, message) => kernel.RecordFindingEvidenceRequest(goal, task, message),
                recordFindingEvidenceRun: (goal, task, message) => kernel.RecordFindingEvidenceRun(goal, task, message),
                recordFindingEvidenceOutcome: (goal, task, stableId, outcome, receipt) => kernel.RecordFindingEvidenceOutcome(goal, task, stableId, outcome, receipt),
                recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
                beforeFailedGoalRecoveryEffect: (_, decision) => RecoveryEffectDecision = decision,
                writeEscalation: (_, _, reason) => Escalations.Add(reason));
            driver.CohortAttributionRevisionReader = _ => new(CurrentCandidate, Main);
            return driver;
        }
    }
}
