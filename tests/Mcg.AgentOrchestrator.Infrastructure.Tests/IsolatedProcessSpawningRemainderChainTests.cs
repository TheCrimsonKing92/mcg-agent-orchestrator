using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: reads metadata and the checked-in manifest; controls change only local copies.
public sealed class IsolatedProcessSpawningRemainderChainTests
{
    private const string OriginalCollection = "IsolatedProcessSpawning";
    private const string SplitCollection = "IsolatedProcessSpawningRemainderB";
    private const string DeferredContextCollection = "IsolatedProcessSpawningRemainderC";
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
        typeof(GoDeveloperNoChangeDispatchTests),
        typeof(ConfirmLegacyAcceptanceHolderSettlementScriptTests),
        typeof(ExecutorBuildCheckScriptTests),
        typeof(CandidateIdentityComputationTests),
        typeof(AcceptanceSharedApparatusRealProcessControlTests),
        typeof(AcceptanceMtpFilterResponseFileManagedTests)
    ];

    private static readonly Type[] DeferredContextTypes =
    [
        typeof(DeferredNoChangeContextAuthorityTestsIsolatedProcessSpawning),
        typeof(DeferredNoChangeContextDeliveryTestsIsolatedProcessSpawning),
        typeof(DeferredNoChangeContextSnapshotTestsIsolatedProcessSpawning)
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
        [nameof(GoDeveloperNoChangeDispatchTests)] = 9.7,
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

    // Operator-supplied samples for candidate 822ad7fbc9bd0e3020afecfb850fff6bdb7468c5.
    // Focused attempt: 7a589bfd-0-20261020040313965-ca162fac03304a5fae8d57ec42e8b279;
    // result SHA256 4E771721E88163AE3B31A37C11C9C066364FDF08FA208B05406FE7D83FA3BCA4,
    // TRX SHA256 1D9E5F1613BCCFDF45864285A4DF7C56770A0B3D322F7274E7E8D79C5668FD10.
    // Native gate: 7a589bfd-0-20261020053144365-f194d4986ac24b4b819a52bc2e708c9c;
    // infrastructure-tests-remainder.trx SHA256
    // 5BF2B083C890E158381EAD5E47CDF956D9DCC61BBF03AE48077923AF61946A74.
    // Both executed the same 44 passing cases. These are summed per-test seconds from two
    // different contexts, not five-gate means or collection wall durations. Plan with each
    // class's larger observed sum; neither existing chain can absorb the resulting 129.5 s.
    private static readonly Dictionary<string, (double Focused, double NativeGate)> DeferredContextSamples = new(StringComparer.Ordinal)
    {
        [nameof(DeferredNoChangeContextAuthorityTestsIsolatedProcessSpawning)] = (15.0496296, 78.2280556),
        [nameof(DeferredNoChangeContextDeliveryTestsIsolatedProcessSpawning)] = (2.9622477, 6.2484266),
        [nameof(DeferredNoChangeContextSnapshotTestsIsolatedProcessSpawning)] = (8.5897754, 45.0373822)
    };

    // Native Remainder gate on candidate f61d20bb4c3742c935285f18a6f542ee6f977f57:
    // attempt 51bc61b5-0-20261025132444961-2f3e478074d44823bd20397513c36679,
    // infrastructure-tests-remainder.trx SHA256
    // 82D561D215EB42B7175A8DBF57E8D921F6199ECA276947F89EEE80CED2F42917.
    // Sum of all seven passing scenario cases; a single gate sample, not a five-gate mean.
    private const double PermitReleaseScenarioGateSeconds = 10.3534388;

    // Native Remainder gate on candidate df82c71faab4ffdc49c3a9c515c7bce69c2c84a7:
    // attempt a5412d04-0-20261025190505535-c3847e1809814c6daf56dcf29dd54012,
    // infrastructure-tests-remainder.trx SHA256
    // 99B648ECA7B376AD5311AA4F3CA8D507FDEB6A4EAFE38897E050938473DA993F.
    // Sum of all nine passing classifier cases; a single gate sample, not a five-gate mean.
    private const double FocusedEvidenceInterruptionGateSeconds = 0.0111781;

    private static readonly IReadOnlyDictionary<string, double> PlanningSeconds = MeanSeconds
        .Concat(DeferredContextSamples.Select(sample => new KeyValuePair<string, double>(
            sample.Key, Math.Max(sample.Value.Focused, sample.Value.NativeGate))))
        .Append(new(nameof(RemoteFirstFocusedEvidencePermitReleaseScenarioTests), PermitReleaseScenarioGateSeconds))
        .Append(new(nameof(FocusedEvidenceInterruptionTests), FocusedEvidenceInterruptionGateSeconds))
        .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);

    [Xunit.Fact]
    public void SplitCollectionHasParallelSharedFixtureAndExactMembers()
    {
        foreach (var type in MovedTypes)
            Xunit.Assert.Equal(SplitCollection, DeclaredCollection(type));
        var members = RunnableTypes().Where(type => DeclaredCollection(type) == SplitCollection).ToArray();
        Xunit.Assert.Equal(14, members.Length);
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
    public void DeferredContextChainHasParallelSharedFixtureAndExactMembers()
    {
        foreach (var type in DeferredContextTypes)
            Xunit.Assert.Equal(DeferredContextCollection, DeclaredCollection(type));
        var members = RunnableTypes().Where(type => DeclaredCollection(type) == DeferredContextCollection).ToArray();
        Xunit.Assert.Equal(DeferredContextTypes.Select(type => type.FullName).Order(StringComparer.Ordinal),
            members.Select(type => type.FullName).Order(StringComparer.Ordinal));

        var definitionType = typeof(IsolatedProcessSpawningRemainderCCollection);
        var definition = definitionType.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>();
        Xunit.Assert.NotNull(definition);
        Xunit.Assert.Equal(DeferredContextCollection, definition.Name);
        Xunit.Assert.True(definitionType.IsPublic && definitionType.IsSealed);
        Xunit.Assert.False(definition.DisableParallelization);
        Xunit.Assert.Null(definitionType.GetCustomAttribute<ProcessLocalTestCollectionAttribute>());
        Xunit.Assert.True(typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>)
            .IsAssignableFrom(definitionType));
        Xunit.Assert.Contains(DeferredContextCollection, LaneIsolatedRootScanner.IsolatedCollections(definitionType.Assembly));
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
        var baseline = actual.Select(member => member.Collection is SplitCollection or DeferredContextCollection
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
        foreach (var type in MovedTypes.Concat(DeferredContextTypes))
            Xunit.Assert.Equal(Remainder, Xunit.Assert.Single(LaneNames(resolvedActual, type.FullName!)));
    }

    [Xunit.Fact]
    public void EachRemainderChainFitsBudgetAndEveryMemberHasAMeasurement()
    {
        var (sums, unlisted) = Score(RemainderMembers(), PlanningSeconds);
        Xunit.Assert.Empty(unlisted);
        Xunit.Assert.InRange(sums[SplitCollection], 223.9 - 0.05, 223.9 + 0.05);
        Xunit.Assert.InRange(sums[OriginalCollection],
            229.4 + PermitReleaseScenarioGateSeconds + FocusedEvidenceInterruptionGateSeconds - 0.05,
            229.4 + PermitReleaseScenarioGateSeconds + FocusedEvidenceInterruptionGateSeconds + 0.05);
        Xunit.Assert.InRange(sums[DeferredContextCollection], 129.5138644 - 1e-7, 129.5138644 + 1e-7);
        foreach (var (collection, seconds) in sums)
            Xunit.Assert.True(seconds <= BudgetSeconds, $"{collection}: {seconds} exceeds {BudgetSeconds} seconds.");
    }

    [Xunit.Fact]
    public void ControlsRejectMergedChainAndScoreEveryUnlistedMember()
    {
        var members = RemainderMembers();
        var (baseline, _) = Score(members, PlanningSeconds);
        var merged = Score(members.Select(member => member with { Collection = OriginalCollection }), PlanningSeconds);
        Xunit.Assert.Single(merged.Sums);
        Xunit.Assert.True(merged.Sums[OriginalCollection] > BudgetSeconds);

        var removedName = nameof(LandingRebindEquivalenceTests);
        var withoutRow = new Dictionary<string, double>(PlanningSeconds, StringComparer.Ordinal);
        Xunit.Assert.True(withoutRow.Remove(removedName));
        var missing = Score(members, withoutRow);
        Xunit.Assert.Equal(removedName, Xunit.Assert.Single(missing.Unlisted));
        var landing = Xunit.Assert.Single(members, member => member.FullName == typeof(LandingRebindEquivalenceTests).FullName);
        var missingOnly = Score([landing], withoutRow);
        Xunit.Assert.Equal(UnlistedSeconds, missingOnly.Sums[SplitCollection]);
        Xunit.Assert.InRange(missing.Sums[SplitCollection] -
            (baseline[SplitCollection] - PlanningSeconds[removedName] + UnlistedSeconds), -1e-9, 1e-9);

        var synthetic = new AcceptanceTestClassDescriptor("UnlistedRemainderChainControl", SplitCollection);
        var added = Score(members.Append(synthetic), PlanningSeconds);
        Xunit.Assert.Equal(synthetic.FullName, Xunit.Assert.Single(added.Unlisted));
        Xunit.Assert.InRange(added.Sums[SplitCollection] - baseline[SplitCollection], 5.0 - 1e-9, 5.0 + 1e-9);
        Xunit.Assert.Equal(baseline[OriginalCollection], added.Sums[OriginalCollection]);
    }

    [Xunit.Fact]
    public void ControlsRejectMergingDeferredContextIntoEitherExistingChainAndMissingItsMeasurement()
    {
        var members = RemainderMembers();
        foreach (var target in new[] { OriginalCollection, SplitCollection })
        {
            var merged = Score(members.Select(member => member.Collection == DeferredContextCollection
                ? member with { Collection = target } : member), PlanningSeconds);
            Xunit.Assert.True(merged.Sums[target] > BudgetSeconds);
        }

        foreach (var type in DeferredContextTypes)
        {
            var withoutRow = new Dictionary<string, double>(PlanningSeconds, StringComparer.Ordinal);
            Xunit.Assert.True(withoutRow.Remove(type.Name));
            var missing = Score(members, withoutRow);
            Xunit.Assert.Equal(type.Name, Xunit.Assert.Single(missing.Unlisted));
        }
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
        return descriptors.Where(member => member.Collection is OriginalCollection or SplitCollection or DeferredContextCollection &&
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
