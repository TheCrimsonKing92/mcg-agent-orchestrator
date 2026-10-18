using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads checked-in configuration; negative controls mutate only in-memory lane copies.
public sealed class IsolatedProcessSpawningLaneSplitTests
{
    private const string LaneA = "Isolated process spawning A";
    private const string LaneB = "Isolated process spawning B";
    private const string Remainder = "Remainder";

    private static readonly Type[] LaneATypes =
    [
        typeof(WorkerBuildCheckOutputBoundsTests),
        typeof(PatchEquivalenceIntegratedMainParentTests),
        typeof(WorkerBuildCheckFreeSlotTests),
        typeof(AcceptanceRevalidationPlannerTests),
        typeof(WorkerBuildCheckRunHoldTests)
    ];

    private static readonly Type[] LaneBTypes =
    [
        typeof(WorkerBuildProjectScopeProcessSpawningTests),
        typeof(FocusedEvidenceUnionManagedTests),
        typeof(PatchEquivalenceIntegrationMergeTests),
        typeof(RemoteFirstFocusedEvidenceTests),
        typeof(CriterionEvidencePatchCarryTests),
        typeof(WorkerBuildCheckRecoveryTests),
        typeof(WorkerBuildCheckRecoveryTestsLockTimeout),
        typeof(WorkerBuildCheckRecoveryTestsEvidenceMissingLockTimeout)
    ];

    private static readonly string[] MovedSubstrings =
    [
        nameof(WorkerBuildCheckOutputBoundsTests),
        nameof(PatchEquivalenceIntegratedMainParentTests),
        nameof(WorkerBuildCheckFreeSlotTests),
        nameof(AcceptanceRevalidationPlannerTests),
        nameof(WorkerBuildCheckRunHoldTests),
        nameof(WorkerBuildProjectScopeProcessSpawningTests),
        nameof(FocusedEvidenceUnionManagedTests),
        nameof(PatchEquivalenceIntegrationMergeTests),
        nameof(RemoteFirstFocusedEvidenceTests),
        nameof(CriterionEvidencePatchCarryTests),
        nameof(WorkerBuildCheckRecoveryTests)
    ];

    // Expand these back to the original operands before constructing the pre-split reference.
    private static readonly Dictionary<string, string[]> CompactExclusions = new(StringComparer.Ordinal)
    {
        ["PatchEquivalenceIntegrat"] =
            [nameof(PatchEquivalenceIntegratedMainParentTests), nameof(PatchEquivalenceIntegrationMergeTests)],
        ["WorkerDispatchTestsSubscription"] =
            ["WorkerDispatchTestsSubscriptionPreflight", "WorkerDispatchTestsSubscriptionTerminalContract"],
        ["RealProcessShard"] = ["RealProcessShardAlphaSmokeTests", "RealProcessShardBetaSmokeTests"],
        ["DisposableTrialRoot"] = ["DisposableTrialRootTests", "DisposableTrialRootNegativeControlTests"],
        ["RunGoalService"] = ["RunGoalServiceTests", "RunGoalServiceProcessContractTests"],
        ["PermitWaitTests"] = ["StructuralCoveragePermitWaitTests", "StructuralCoverageVerifierPermitWaitTests"],
        ["ThreadPoolSaturationTests"] = ["GitCliThreadPoolSaturationTests", "PipeDrainThreadPoolSaturationTests"]
    };

    // Mean summed test seconds from TRX attempts d39a96cd-0-20261018143634873,
    // 1bc6e2ba-0-20261018141003319, operator-gate-20261018132531546,
    // 6a9b6a43-gate-20261018130319398 and 9ca89c71-0-20261018095002219.
    private static readonly Dictionary<string, double> MeanSeconds = new(StringComparer.Ordinal)
    {
        ["WorkerBuildCheckOutputBoundsTests"] = 181.5,
        ["WorkerBuildProjectScopeProcessSpawningTests"] = 136.1,
        ["FocusedEvidenceUnionManagedTests"] = 127.3,
        ["PatchEquivalenceIntegratedMainParentTests"] = 95.7,
        ["WorkerBuildCheckFreeSlotTests"] = 48.4,
        ["PatchEquivalenceIntegrationMergeTests"] = 48.4,
        ["AcceptanceRevalidationPlannerTests"] = 26.6,
        ["RemoteFirstFocusedEvidenceTests"] = 26.1,
        ["WorkerBuildCheckRunHoldTests"] = 19.9,
        ["CriterionEvidencePatchCarryTests"] = 17.1,
        ["WorkerBuildCheckRecoveryTestsLockTimeout"] = 15.9,
        ["DeveloperDeferredNoChangeDispatchTests"] = 14.0,
        ["LandingRebindEquivalenceTests"] = 12.9,
        ["DeveloperStaleFindingNoChangeDispatchTests"] = 12.6,
        ["RemoteFocusedEvidenceShadowTests"] = 12.5,
        ["DeveloperStructuredNoChangeDeclarationTests"] = 11.4,
        ["WorkerBuildCheckRecoveryDecisionTests"] = 10.6,
        ["PatchEquivalenceContextOnlyDriftTests"] = 10.4,
        ["DeveloperAnsweredClarificationNoChangeDispatchTests"] = 10.3,
        ["DeveloperUnusableEvidenceNoChangeDispatchTests"] = 9.6,
        ["RemoteFirstFocusedEvidenceScenarioTests"] = 7.7,
        ["WorkerBuildCheckRecoveryTests"] = 6.5,
        ["ConfirmLegacyAcceptanceHolderSettlementScriptTests"] = 5.4,
        ["AcceptanceSharedApparatusRealProcessControlTests"] = 5.0,
        ["OwnerPolicyApprovalRebaseTests"] = 4.7,
        ["GoalOwnedLinesPatchEquivalenceTests"] = 3.2,
        ["CandidateIdentityComputationTests"] = 3.0,
        ["JobAccountingRunIdentityEvidenceTests"] = 1.9,
        ["RemoteFocusedEvidenceShadowPhaseTests"] = 1.9,
        ["CandidateIdentityConfiguredTrunkTests"] = 1.8,
        ["WorkerBuildCheckRecoveryTestsEvidenceMissingLockTimeout"] = 1.5,
        ["AcceptanceGateFlakeInventoryGeneratorTests"] = 1.4,
        ["ManagedDiscoveryNonAsciiDiscoveryTests"] = 1.4,
        ["GoalAcceptanceLaneIsolatedRootGuardTests"] = 1.3,
        ["WorkerDispatchTestsReviewFindingFormatRepair"] = 1.3
    };

    [Xunit.Fact]
    public void SplitMembership_WrittenAndResolved_SelectsEachMovedClassExactlyOnce()
    {
        var lanes = LoadLanes();
        var resolved = Resolve(lanes, RunnableTypes());
        foreach (var membership in new[] { lanes, resolved })
        {
            foreach (var type in LaneATypes)
                AssertOnlyLane(membership, type, LaneA);
            foreach (var type in LaneBTypes)
                AssertOnlyLane(membership, type, LaneB);
            AssertOnlyLane(membership, typeof(GoalAcceptanceLaneIsolatedRootGuardTests), Remainder);
            AssertOnlyLane(membership, typeof(JobAccountingRunIdentityEvidenceTests), Remainder);
        }
    }

    [Xunit.Fact]
    public void CompactExclusions_ClassCensus_MatchesOnlyPreviouslyExcludedClasses()
    {
        var types = RunnableTypes();
        foreach (var (substring, originals) in CompactExclusions)
        {
            var originalMatches = types.Where(type => originals.Any(original =>
                    type.FullName!.Contains(original, StringComparison.OrdinalIgnoreCase)))
                .Select(type => type.FullName).ToArray();
            var compactMatches = types.Where(type =>
                    type.FullName!.Contains(substring, StringComparison.OrdinalIgnoreCase))
                .Select(type => type.FullName).ToArray();
            Xunit.Assert.NotEmpty(originalMatches);
            Xunit.Assert.Equal(originalMatches, compactMatches);
        }
    }

    [Xunit.Fact]
    public void MembershipDelta_InMemoryMutations_ReportOnlyTheAffectedClass()
    {
        var lanes = LoadLanes();
        var types = RunnableTypes();
        // Build once from the unmutated lanes, so a control cannot change its own reference.
        var reference = BuildReference(lanes);
        var baseline = MembershipReport(lanes, reference, types);
        Xunit.Assert.True(baseline.Count == 0, string.Join(Environment.NewLine, baseline.Values));

        var extraMember = lanes.Select(lane => lane.Name == LaneA
            ? lane with { Filter = lane.Filter + "|FullyQualifiedName~JobAccountingRunIdentityEvidenceTests" }
            : lane).ToArray();
        AssertOnlyReportDifference(baseline, MembershipReport(extraMember, reference, types),
            typeof(JobAccountingRunIdentityEvidenceTests));

        var missingExclusion = lanes.Select(lane => lane.Name == Remainder
            ? lane with { Filter = lane.Filter.Replace(
                "&FullyQualifiedName!~WorkerBuildCheckRunHoldTests", "", StringComparison.Ordinal) }
            : lane).ToArray();
        AssertOnlyReportDifference(baseline, MembershipReport(missingExclusion, reference, types),
            typeof(WorkerBuildCheckRunHoldTests));
    }

    [Xunit.Fact]
    public void SerialChain_TableBudgets_BoundEachLaneAndRejectTheReferenceRemainder()
    {
        var lanes = LoadLanes();
        var types = RunnableTypes();
        var resolved = Resolve(lanes, types);
        var reference = Resolve(BuildReference(lanes), types);

        Xunit.Assert.InRange(ChainSum(resolved, LaneA, types), 0, 400);
        Xunit.Assert.InRange(ChainSum(resolved, LaneB, types), 0, 400);
        Xunit.Assert.InRange(ChainSum(resolved, Remainder, types), 0, 200);
        Xunit.Assert.True(ChainSum(reference, Remainder, types) > 200,
            "The pre-split reference must exceed the residual serial-chain budget.");
    }

    [Xunit.Fact]
    public void SplitConfiguration_LoadedManifest_PreservesKeylessLanesAndPartitionCoverage()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var laneA = Xunit.Assert.Single(lanes, lane => lane.Name == LaneA);
        var laneB = Xunit.Assert.Single(lanes, lane => lane.Name == LaneB);
        Xunit.Assert.Equal(382.1, laneA.EstimatedSerialSeconds);
        Xunit.Assert.Equal(388.9, laneB.EstimatedSerialSeconds);
        Xunit.Assert.Equal(653.0, Xunit.Assert.Single(lanes, lane => lane.Name == Remainder).EstimatedSerialSeconds);
        foreach (var lane in new[] { laneA, laneB })
        {
            Xunit.Assert.Empty(lane.ExclusiveResourceKeys);
            Xunit.Assert.Empty(lane.OwnedCollections);
            Xunit.Assert.False(lane.RequiresBuildSystemChange);
            Xunit.Assert.EndsWith("&Category!=HostIntegration", lane.Filter, StringComparison.Ordinal);
        }

        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")));
        var partition = Xunit.Assert.Single(manifest.RootElement.GetProperty("engine")
            .GetProperty("localTestPartitions").EnumerateArray(),
            item => item.GetProperty("name").GetString() == Remainder);
        var laneNames = partition.GetProperty("laneNames").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Xunit.Assert.Contains(LaneA, laneNames);
        Xunit.Assert.Contains(LaneB, laneNames);
    }

    private static IReadOnlyList<AcceptanceTestLane> LoadLanes() =>
        AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;

    private static Type[] RunnableTypes() =>
        typeof(IsolatedProcessSpawningLaneSplitTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<AcceptanceTestLane> Resolve(
        IReadOnlyList<AcceptanceTestLane> lanes, Type[] types) =>
        AcceptanceLaneMembership.ResolveOwnedCollections(lanes, types.Select(type =>
            new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)).ToArray());

    private static void AssertOnlyLane(IReadOnlyList<AcceptanceTestLane> lanes, Type type, string expected) =>
        Xunit.Assert.Equal(expected, Xunit.Assert.Single(
            AcceptanceLaneMembership.LanesIncluding(lanes, type.FullName!)).Name);

    private static AcceptanceTestLane[] BuildReference(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        Xunit.Assert.Single(lanes, lane => lane.Name == LaneA);
        Xunit.Assert.Single(lanes, lane => lane.Name == LaneB);
        var remainder = Xunit.Assert.Single(lanes, lane => lane.Name == Remainder);
        var operands = remainder.Filter.Split('&').ToList();
        foreach (var (substring, originals) in CompactExclusions)
        {
            var compactOperand = "FullyQualifiedName!~" + substring;
            Xunit.Assert.Single(operands, item => item == compactOperand);
            var index = operands.IndexOf(compactOperand);
            operands.RemoveAt(index);
            operands.InsertRange(index, originals.Select(original => "FullyQualifiedName!~" + original));
        }
        foreach (var substring in MovedSubstrings)
        {
            var operand = "FullyQualifiedName!~" + substring;
            Xunit.Assert.Single(operands, item => item == operand);
            operands.Remove(operand);
        }
        return lanes.Where(lane => lane.Name != LaneA && lane.Name != LaneB)
            .Select(lane => lane.Name == Remainder
                ? lane with { Filter = string.Join('&', operands) }
                : lane).ToArray();
    }

    private static Dictionary<Type, string> MembershipReport(IReadOnlyList<AcceptanceTestLane> loaded,
        IReadOnlyList<AcceptanceTestLane> reference, Type[] types)
    {
        var resolvedLoaded = Resolve(loaded, types);
        var resolvedReference = Resolve(reference, types);
        var movedTargets = LaneATypes.Select(type => (Type: type, Lane: LaneA))
            .Concat(LaneBTypes.Select(type => (Type: type, Lane: LaneB)))
            .ToDictionary(item => item.Type, item => item.Lane);
        var report = new Dictionary<Type, string>();
        foreach (var type in types)
        {
            var loadedNames = LaneNames(resolvedLoaded, type);
            var referenceNames = LaneNames(resolvedReference, type);
            var matches = movedTargets.TryGetValue(type, out var target)
                ? referenceNames.SequenceEqual([Remainder]) && loadedNames.SequenceEqual([target])
                : loadedNames.SequenceEqual(referenceNames);
            if (!matches)
                report.Add(type, $"{type.Name}: loaded={{{string.Join(", ", loadedNames)}}} " +
                    $"reference={{{string.Join(", ", referenceNames)}}}");
        }
        return report;
    }

    private static string[] LaneNames(IReadOnlyList<AcceptanceTestLane> lanes, Type type) =>
        AcceptanceLaneMembership.LanesIncluding(lanes, type.FullName!)
            .Select(lane => lane.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static void AssertOnlyReportDifference(Dictionary<Type, string> baseline,
        Dictionary<Type, string> mutated, Type expected)
    {
        var difference = mutated.Keys.ToHashSet();
        difference.SymmetricExceptWith(baseline.Keys);
        Xunit.Assert.True(difference.SetEquals([expected]), string.Join(Environment.NewLine, mutated.Values));
    }

    private static double ChainSum(IReadOnlyList<AcceptanceTestLane> resolved, string laneName, Type[] types) =>
        types.Where(type => type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name ==
                "IsolatedProcessSpawning" && AcceptanceLaneMembership.LanesIncluding(resolved, type.FullName!)
                    .Any(lane => lane.Name == laneName))
            .Sum(type => MeanSeconds.GetValueOrDefault(type.Name));
}
