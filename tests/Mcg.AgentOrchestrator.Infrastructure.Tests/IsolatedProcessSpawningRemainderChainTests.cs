using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: reads metadata and the checked-in manifest; controls change only local copies.
public sealed class IsolatedProcessSpawningRemainderChainTests
{
    private const string OriginalCollection = "IsolatedProcessSpawning";
    private const string SplitCollection = "IsolatedProcessSpawningRemainderB";
    private const string Remainder = "Remainder";
    private const double UnlistedSeconds = 5.0;
    private const double BudgetSeconds = 240.0;

    private static readonly Type[] MovedTypes =
    [
        typeof(LandingRebindEquivalenceTests),
        typeof(DeveloperStructuredNoChangeDeclarationTests),
        typeof(DeveloperAnsweredClarificationNoChangeDispatchTests),
        typeof(DeveloperStaleFindingNoChangeDispatchTests),
        typeof(OwnerPolicyApprovalRebaseTests),
        typeof(WorkerBuildCheckRecoveryDecisionTests),
        typeof(DeveloperUnusableEvidenceNoChangeDispatchTests),
        typeof(DeveloperDeferredNoChangeDispatchTests),
        typeof(ConfirmLegacyAcceptanceHolderSettlementScriptTests),
        typeof(ExecutorBuildCheckScriptTests),
        typeof(CandidateIdentityComputationTests),
        typeof(AcceptanceSharedApparatusRealProcessControlTests),
        typeof(AcceptanceMtpFilterResponseFileManagedTests)
    ];

    // Five-gate means: 9a0a3d15, a6926d29, c4014e44, 7dadc55c,
    // and operator 20261019032750611. Each row is summed test seconds for one class.
    private static readonly Dictionary<string, double> MeanSeconds = new(StringComparer.Ordinal)
    {
        [nameof(LandingRebindEquivalenceTests)] = 49.1,
        [nameof(DeveloperStructuredNoChangeDeclarationTests)] = 39.9,
        [nameof(DeveloperAnsweredClarificationNoChangeDispatchTests)] = 31.0,
        [nameof(DeveloperStaleFindingNoChangeDispatchTests)] = 15.8,
        [nameof(OwnerPolicyApprovalRebaseTests)] = 13.1,
        [nameof(WorkerBuildCheckRecoveryDecisionTests)] = 10.6,
        [nameof(DeveloperUnusableEvidenceNoChangeDispatchTests)] = 10.2,
        [nameof(DeveloperDeferredNoChangeDispatchTests)] = 9.7,
        [nameof(ConfirmLegacyAcceptanceHolderSettlementScriptTests)] = 8.2,
        [nameof(ExecutorBuildCheckScriptTests)] = 7.8,
        [nameof(CandidateIdentityComputationTests)] = 7.3,
        [nameof(AcceptanceSharedApparatusRealProcessControlTests)] = 6.3,
        [nameof(AcceptanceMtpFilterResponseFileManagedTests)] = 5.2,
        [nameof(PatchEquivalenceContextOnlyDriftTests)] = 123.7,
        [nameof(RemoteFocusedEvidenceShadowTests)] = 24.1,
        [nameof(RemoteFirstFocusedEvidenceScenarioTests)] = 22.7,
        [nameof(GoalOwnedLinesPatchEquivalenceTests)] = 10.0,
        [nameof(RemoteFocusedEvidenceShadowAgreementTests)] = 8.4,
        [nameof(RemoteFocusedEvidenceShadowHostBoundTests)] = 8.4,
        [nameof(RemoteFirstFocusedEvidenceHostBoundTests)] = 8.1,
        [nameof(ExecutorToolsDigestScriptTests)] = 3.5,
        [nameof(WorkerDispatchTestsReviewFindingFormatRepair)] = 3.3,
        [nameof(JobAccountingRunIdentityEvidenceTests)] = 3.2,
        [nameof(ManagedDiscoveryNonAsciiDiscoveryTests)] = 3.0,
        [nameof(Mcg.AgentOrchestrator.Infrastructure.Tests.AcceptanceGateFlakeInventoryGeneratorTests)] = 2.7,
        [nameof(CandidateIdentityConfiguredTrunkTests)] = 2.6,
        [nameof(GoalAcceptanceLaneIsolatedRootGuardTests)] = 1.5,
        [nameof(RemoteFocusedEvidenceShadowPhaseTests)] = 1.0,
        [nameof(RepositorySourceInventoryTests)] = 0.8,
        [nameof(DeferredNoChangeEvidenceClassSetChangeTests)] = 0.7,
        [nameof(WorkerBuildDiagnosticDefaultsTests)] = 0.5,
        [nameof(HumanInputResumeDispatchTests)] = 0.4,
        [nameof(DeveloperCompletionStructuralPreflightTestGuardsTests)] = 0.2,
        [nameof(ConductorDriverGoalEvidenceLeaseTests)] = 0.2,
        [nameof(EvidenceOnlyNoChangeAcceptanceTests)] = 0.1,
        [nameof(FailedGoalRetryApplicationDriverEquivalenceTests)] = 0.1,
        [nameof(AnsweredTesterBlockerRequeueIsolatedProcessSpawningConductorTests)] = 0.1,
        [nameof(AssemblyTempRedirectStartupCostTests)] = 0.1,
        [nameof(DistinctPendingEvidenceOnlyFindingsTests)] = 0.0,
        [nameof(DeveloperCompletionStructuralPreflightRetryFeedbackTests)] = 0.0,
        [nameof(PreReviewEvidenceCoverageTests)] = 0.0,
        [nameof(AutomaticRetryFeedbackControlTests)] = 0.0,
        [nameof(ConductorDriverRetryOnlyRemediationTests)] = 0.0,
        [nameof(DeveloperCompletionStructuralPreflightRetryFeedbackTestsClassCeiling)] = 0.0,
        [nameof(DispatchStartDecisionCoverageTests)] = 0.0
    };

    [Xunit.Fact]
    public void SplitCollectionHasParallelSharedFixtureAndExactMembers()
    {
        foreach (var type in MovedTypes)
            Xunit.Assert.Equal(SplitCollection, DeclaredCollection(type));
        var members = RunnableTypes().Where(type => DeclaredCollection(type) == SplitCollection).ToArray();
        Xunit.Assert.Equal(13, members.Length);
        Xunit.Assert.Equal(MovedTypes.Select(type => type.FullName).Order(StringComparer.Ordinal),
            members.Select(type => type.FullName).Order(StringComparer.Ordinal));

        var definitionType = typeof(IsolatedProcessSpawningRemainderBCollection);
        var definition = definitionType.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>();
        Xunit.Assert.NotNull(definition);
        Xunit.Assert.Equal(SplitCollection, definition.Name);
        Xunit.Assert.True(definitionType.IsPublic && definitionType.IsSealed);
        Xunit.Assert.False(definition.DisableParallelization);
        Xunit.Assert.Null(definitionType.GetCustomAttribute<ProcessLocalTestCollectionAttribute>());
        Xunit.Assert.True(typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>)
            .IsAssignableFrom(definitionType));
        var isolated = LaneIsolatedRootScanner.IsolatedCollections(definitionType.Assembly);
        Xunit.Assert.Contains(OriginalCollection, isolated);
        Xunit.Assert.Contains(SplitCollection, isolated);
    }

    [Xunit.Fact]
    public void SplitPreservesEveryClassLaneAndMovesOnlyRemainderMembers()
    {
        var types = RunnableTypes();
        Xunit.Assert.NotEmpty(types);
        var actual = Descriptors(types);
        foreach (var type in MovedTypes)
            Xunit.Assert.Equal(SplitCollection,
                Xunit.Assert.Single(actual, member => member.FullName == type.FullName).Collection);
        var baseline = actual.Select(member => member.Collection == SplitCollection
            ? member with { Collection = OriginalCollection } : member).ToArray();
        var lanes = LoadLanes();
        var resolvedActual = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, actual);
        var resolvedBaseline = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, baseline);
        foreach (var type in types)
        {
            var actualNames = LaneNames(resolvedActual, type.FullName!);
            var baselineNames = LaneNames(resolvedBaseline, type.FullName!);
            Xunit.Assert.True(actualNames.SequenceEqual(baselineNames, StringComparer.Ordinal),
                $"{type.FullName}: actual={{{string.Join(", ", actualNames)}}}, baseline={{{string.Join(", ", baselineNames)}}}");
        }
        foreach (var type in MovedTypes)
            Xunit.Assert.Equal(Remainder, Xunit.Assert.Single(LaneNames(resolvedActual, type.FullName!)));
    }

    [Xunit.Fact]
    public void EachRemainderChainFitsBudgetAndEveryMemberHasAMeasurement()
    {
        var (sums, unlisted) = Score(RemainderMembers(), MeanSeconds);
        Xunit.Assert.Empty(unlisted);
        Xunit.Assert.InRange(sums[SplitCollection], 214.2 - 0.05, 214.2 + 0.05);
        Xunit.Assert.InRange(sums[OriginalCollection], 229.4 - 0.05, 229.4 + 0.05);
        foreach (var (collection, seconds) in sums)
            Xunit.Assert.True(seconds <= BudgetSeconds, $"{collection}: {seconds} exceeds {BudgetSeconds} seconds.");
    }

    [Xunit.Fact]
    public void ControlsRejectMergedChainAndScoreEveryUnlistedMember()
    {
        var members = RemainderMembers();
        var (baseline, _) = Score(members, MeanSeconds);
        var merged = Score(members.Select(member => member with { Collection = OriginalCollection }), MeanSeconds);
        Xunit.Assert.Single(merged.Sums);
        Xunit.Assert.True(merged.Sums[OriginalCollection] > BudgetSeconds);

        var removedName = nameof(LandingRebindEquivalenceTests);
        var withoutRow = new Dictionary<string, double>(MeanSeconds, StringComparer.Ordinal);
        Xunit.Assert.True(withoutRow.Remove(removedName));
        var missing = Score(members, withoutRow);
        Xunit.Assert.Equal(removedName, Xunit.Assert.Single(missing.Unlisted));
        var landing = Xunit.Assert.Single(members, member => member.FullName == typeof(LandingRebindEquivalenceTests).FullName);
        var missingOnly = Score([landing], withoutRow);
        Xunit.Assert.Equal(UnlistedSeconds, missingOnly.Sums[SplitCollection]);
        Xunit.Assert.InRange(missing.Sums[SplitCollection] -
            (baseline[SplitCollection] - MeanSeconds[removedName] + UnlistedSeconds), -1e-9, 1e-9);

        var synthetic = new AcceptanceTestClassDescriptor("UnlistedRemainderChainControl", SplitCollection);
        var added = Score(members.Append(synthetic), MeanSeconds);
        Xunit.Assert.Equal(synthetic.FullName, Xunit.Assert.Single(added.Unlisted));
        Xunit.Assert.InRange(added.Sums[SplitCollection] - baseline[SplitCollection], 5.0 - 1e-9, 5.0 + 1e-9);
        Xunit.Assert.Equal(baseline[OriginalCollection], added.Sums[OriginalCollection]);
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(IsolatedProcessSpawningRemainderChainTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();

    private static string? DeclaredCollection(Type type) =>
        type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name;

    private static AcceptanceTestClassDescriptor[] Descriptors(Type[] types) =>
        types.Select(type => new AcceptanceTestClassDescriptor(type.FullName ?? type.Name, DeclaredCollection(type))).ToArray();

    private static string[] LaneNames(IReadOnlyList<AcceptanceTestLane> lanes, string fullName) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, fullName).Select(lane => lane.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static AcceptanceTestClassDescriptor[] RemainderMembers()
    {
        var descriptors = Descriptors(RunnableTypes());
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(LoadLanes(), descriptors);
        return descriptors.Where(member => member.Collection is OriginalCollection or SplitCollection &&
            AcceptanceLaneMembership.LanesIncluding(resolved, member.FullName).Any(lane => lane.Name == Remainder)).ToArray();
    }

    private static (Dictionary<string, double> Sums, SortedSet<string> Unlisted) Score(
        IEnumerable<AcceptanceTestClassDescriptor> members, IReadOnlyDictionary<string, double> table)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        var unlisted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var name = member.FullName[(Math.Max(member.FullName.LastIndexOf('.'), member.FullName.LastIndexOf('+')) + 1)..];
            if (!table.TryGetValue(name, out var seconds))
            {
                seconds = UnlistedSeconds;
                unlisted.Add(name);
            }
            var collection = member.Collection ?? throw new InvalidDataException($"Chain member {member.FullName} has no collection.");
            sums[collection] = sums.GetValueOrDefault(collection) + seconds;
        }
        return (sums, unlisted);
    }
}
