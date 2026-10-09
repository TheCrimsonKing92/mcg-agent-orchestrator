using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Resolver-only coverage: read repository sources without mutating shared state or starting processes.
public sealed class FocusedEvidenceOverflowPackingTests
{
    private const string ClassPrefix = "FullyQualifiedName~";
    private static readonly string[] ClassNames =
    [
        nameof(AdvanceLoopTests),
        nameof(AutoReviewRetryConvergenceBriefBuilderTests),
        nameof(ChaosGateAssignedScopeIncompleteTests),
        nameof(ChaosGateDispatchDirtyWorktreeTests),
        nameof(ChaosGateDispatchNoChangeTests),
        nameof(CitedPriorEvidenceResolverTests),
        nameof(CliCommandTestsSubscriptionDispatchCommands),
        nameof(ConductorBatchLoopTestsReapingDetach),
        nameof(ConductorBatchLoopTestsWatchProgress),
        nameof(ConductorBatchLoopVerificationReconcileTests),
        nameof(ConductorDriverTests),
        nameof(ConductorDriverTestsDispatchRecovery),
        nameof(DirtyDispatchRecoveryViewTests),
        nameof(DispatchRecoveryPolicyTests),
        nameof(FailureTriageDecisionTests),
        nameof(GoalRefinementTests),
        nameof(InterruptedWorkCheckpointContinuationTests),
        nameof(LandingExecutorTests),
        nameof(PlannerEvidenceDispatchTests),
        nameof(PlannerOutputContractTests),
        nameof(PlannerSamplingDispatchTests),
        nameof(PreDispatchIntegrationNoChangeTests),
        nameof(RunGoalServiceProcessContractTests),
        nameof(RunGoalServiceTests),
        nameof(VerificationAndProcessLogTests),
        nameof(WorkerBuildEvidenceRequirementTests),
        nameof(WorkerContextRendererDispatchPathTests),
        nameof(WorkerDispatchBuildEvidenceClassificationTests),
        "WorkerDispatchCompletionClassifierTests",
        nameof(WorkerDispatchJobAccountingTests),
        nameof(WorkerDispatchTestsDispatchPreparation),
        nameof(WorkerDispatchTestsModelSelection)
    ];

    [Xunit.Fact]
    public void WideRequest_PacksBoundedChecksAndPreservesEverySelectionAndTarget()
    {
        Assert.Equal(32, ClassNames.Length);
        var filters = ClassNames.Select(name => ClassPrefix + name).ToArray();
        Assert.True(string.Join('|', filters).Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
        var targets = filters.Select(Target).ToArray();

        var checks = Resolve(targets, out var coverage);

        Assert.InRange(checks.Count, 2, 5);
        Assert.All(checks, check =>
        {
            Assert.Equal(AcceptancePolicyShardPlanner.InfrastructureTestsProject, check.Project);
            Assert.Equal("dotnet-test", check.Type);
            Assert.InRange(FilterText(check).Length, 1, GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
            Assert.Equal(string.Join('|', check.FocusedEvidenceTokens.Select(token => token.CanonicalToken)), FilterText(check));
        });
        Assert.Equal(filters.Order(StringComparer.Ordinal), checks
            .SelectMany(check => check.FocusedEvidenceTokens)
            .Select(token => token.CanonicalToken).Order(StringComparer.Ordinal));
        Assert.Equal(filters.Order(StringComparer.Ordinal), checks
            .SelectMany(check => check.FocusedEvidenceSelections)
            .Select(selection => Assert.Single(selection).CanonicalToken).Order(StringComparer.Ordinal));
        Assert.Equal(targets.Order(StringComparer.Ordinal), coverage.TargetToChecks
            .Select(mapping => mapping.Target).Order(StringComparer.Ordinal));
        foreach (var mapping in coverage.TargetToChecks)
        {
            var checkName = Assert.Single(mapping.CheckNames);
            var check = Assert.Single(checks, candidate => candidate.Name == checkName);
            Assert.Contains(check.FocusedEvidenceSelections,
                selection => Target(Assert.Single(selection).CanonicalToken) == mapping.Target);
        }
        Assert.Contains("Infrastructure.Tests=bounded-filter-overflow", coverage.ExecutionReason, StringComparison.Ordinal);
        Assert.DoesNotContain("compatible-same-project-batch", coverage.ExecutionReason, StringComparison.Ordinal);

        // Every closed group is full with respect to the next item in ordinal order.
        Assert.Equal(filters.Order(StringComparer.Ordinal), checks
            .SelectMany(check => check.FocusedEvidenceSelections)
            .Select(selection => Assert.Single(selection).CanonicalToken));
        for (var index = 0; index < checks.Count - 1; index++)
        {
            var nextFilter = Assert.Single(checks[index + 1].FocusedEvidenceSelections[0]).CanonicalToken;
            Assert.True((FilterText(checks[index]) + "|" + nextFilter).Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
        }
    }

    [Xunit.Fact]
    public void PermutedRequest_ProducesIdenticalChecksAndCoverageInTheSameOrder()
    {
        var targets = ClassNames.Select(name => Target(ClassPrefix + name)).ToArray();
        var forward = Resolve(targets, out var forwardCoverage);
        var reversed = Resolve(targets.Reverse(), out var reversedCoverage);

        Assert.Equal(forward.Count, reversed.Count);
        for (var index = 0; index < forward.Count; index++)
        {
            Assert.Equal(forward[index].Name, reversed[index].Name);
            Assert.Equal(forward[index].Project, reversed[index].Project);
            Assert.Equal(forward[index].Type, reversed[index].Type);
            Assert.Equal(forward[index].Runner, reversed[index].Runner);
            Assert.Equal(forward[index].Arguments, reversed[index].Arguments);
            Assert.Equal(forward[index].TimeoutMinutes, reversed[index].TimeoutMinutes);
            Assert.Equal(forward[index].FocusedEvidenceMatchedClasses.Select(match => match.CanonicalToken),
                reversed[index].FocusedEvidenceMatchedClasses.Select(match => match.CanonicalToken));
            for (var matchIndex = 0; matchIndex < forward[index].FocusedEvidenceMatchedClasses.Count; matchIndex++)
            {
                Assert.Equal(forward[index].FocusedEvidenceMatchedClasses[matchIndex].ClassNames,
                    reversed[index].FocusedEvidenceMatchedClasses[matchIndex].ClassNames);
            }
            Assert.Equal(forward[index].FocusedEvidenceTokens, reversed[index].FocusedEvidenceTokens);
            Assert.Equal(SelectionTexts(forward[index]), SelectionTexts(reversed[index]));
        }
        Assert.Equal(forwardCoverage.ExecutionMode, reversedCoverage.ExecutionMode);
        Assert.Equal(forwardCoverage.ExecutionReason, reversedCoverage.ExecutionReason);
        Assert.Equal(forwardCoverage.TargetToChecks.Select(mapping => mapping.Target),
            reversedCoverage.TargetToChecks.Select(mapping => mapping.Target));
        for (var index = 0; index < forwardCoverage.TargetToChecks.Count; index++)
        {
            Assert.Equal(forwardCoverage.TargetToChecks[index].CheckNames, reversedCoverage.TargetToChecks[index].CheckNames);
        }
    }

    [Xunit.Fact]
    public void BoundarySizedItems_StayWholeInSeparateChecks()
    {
        var filters = new[] { 'A', 'B' }.Select(character => ClassPrefix +
            new string(character, GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength - ClassPrefix.Length)).ToArray();
        Assert.All(filters, filter => Assert.Equal(1024, filter.Length));

        var checks = Resolve(filters.Reverse().Select(Target), out var coverage);

        Assert.Equal(2, checks.Count);
        Assert.Equal(filters, checks.Select(FilterText));
        Assert.All(checks, check => Assert.Equal(FilterText(check), Assert.Single(SelectionTexts(check))));
        Assert.All(coverage.TargetToChecks, mapping => Assert.Single(mapping.CheckNames));
        Assert.Contains("bounded-filter-overflow", coverage.ExecutionReason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MultiClauseItems_StayWholeWithOverlappingTokensMergedCanonically()
    {
        var longA = ClassPrefix + new string('A', 580);
        var longB = ClassPrefix + new string('B', 580);
        var filters = new[] { longA + "|" + ClassPrefix + "SharedTests", longB, ClassPrefix + "SharedTests" };
        var targets = filters.Select(Target).ToArray();

        var checks = Resolve(targets.Reverse(), out var coverage);

        Assert.Equal(2, checks.Count);
        Assert.Equal(filters.Order(StringComparer.Ordinal), checks.SelectMany(SelectionTexts).Order(StringComparer.Ordinal));
        Assert.Equal(filters.SelectMany(filter => filter.Split('|')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            checks.SelectMany(check => check.FocusedEvidenceTokens).Select(token => token.CanonicalToken)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(targets.Order(StringComparer.Ordinal), coverage.TargetToChecks.Select(mapping => mapping.Target).Order(StringComparer.Ordinal));
        Assert.All(coverage.TargetToChecks, mapping => Assert.Single(mapping.CheckNames));
        Assert.All(checks, check => Assert.True(FilterText(check).Length <= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength));
    }

    private static IReadOnlyList<AcceptanceManifestCheck> Resolve(
        IEnumerable<string> targets, out FocusedEvidenceCoverage coverage)
    {
        var accepted = GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            string.Join("; ", targets), new AcceptanceGateEngineSettings(), InfrastructureTestSupport.FindRepositoryRoot(),
            out var checks, out coverage, out var rejection);
        Assert.True(accepted, $"{rejection.Code}: {rejection.Detail}");
        Assert.NotEmpty(checks);
        return checks;
    }

    private static string Target(string filter) => "Infrastructure.Tests:" + filter;

    private static string FilterText(AcceptanceManifestCheck check)
    {
        var arguments = check.Arguments.ToArray();
        var index = Array.IndexOf(arguments, "--filter");
        Assert.InRange(index, 0, arguments.Length - 2);
        return arguments[index + 1];
    }

    private static IEnumerable<string> SelectionTexts(AcceptanceManifestCheck check) =>
        check.FocusedEvidenceSelections.Select(selection => string.Join('|', selection.Select(token => token.CanonicalToken)));
}
