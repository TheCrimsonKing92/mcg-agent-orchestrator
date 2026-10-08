using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateReadyCandidateProjectorTests
{
    private const string BranchRevision = "1111111111111111111111111111111111111111";
    private const string MainRevision = "2222222222222222222222222222222222222222";
    private const string OtherBranchRevision = "3333333333333333333333333333333333333333";
    private const string OtherMainRevision = "4444444444444444444444444444444444444444";
    private static readonly GoalId GoalId = new("a0d88a2a72a94a1297c295d1e027d08c");

    [Fact]
    public void ReadyFacts_ReturnImmutableRevisionBoundProjection()
    {
        var scope = new List<string>
        {
            "src\\Mcg.AgentOrchestrator.Core\\Feature.cs",
            "docs/readme.md"
        };
        var harness = new Harness(scope: scope);

        var ready = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            harness.Projector.Project(ReadyInput()));

        Assert.Equal(GoalId, ready.Projection.GoalId);
        Assert.Equal(BranchRevision, ready.Projection.BranchRevision);
        Assert.Equal(MainRevision, ready.Projection.MainRevision);
        Assert.Equal(GoalLifecycleState.Verified, ready.Projection.LifecycleState);
        Assert.Equal(GateReadyVerificationState.Satisfied, ready.Projection.VerificationState);
        Assert.Equal(ChangeRiskTier.DocsOnly, ready.Projection.ChangeRiskTier);
        Assert.Equal(ConductorTransitionDecision.Auto, ready.Projection.AutoPromotionDisposition);
        Assert.Equal(
            ["docs/readme.md", "src/Mcg.AgentOrchestrator.Core/Feature.cs"],
            ready.Projection.LandingPaths);
        Assert.Equal(["ownership:shared-infrastructure"], ready.Projection.ResourceKeys);
        Assert.Equal(GateReadyMergeStatus.Clean, ready.Projection.MergeEvidence.Status);
        Assert.Equal(GateReadyMergeReason.NoConflictsDetected, ready.Projection.MergeEvidence.Reason);
        Assert.Equal(BranchRevision, harness.MergeBranchRevision);
        Assert.Equal(MainRevision, harness.MergeMainRevision);
        Assert.Equal(2, harness.RevisionReadCount);
        Assert.Equal(1, harness.ScopeReadCount);
        Assert.Equal(1, harness.MergeReadCount);
    }

    [Fact]
    public void ProjectionStructure_HoldsNoMutableGoalState()
    {
        var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            new Harness().Projector.Project(ReadyInput())).Projection;

        Assert.False(ContainsGoalState(projection.GetType(), []));
        Assert.DoesNotContain(
            projection.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType == typeof(Goal) ||
                property.PropertyType == typeof(TaskSpec) ||
                property.PropertyType == typeof(GoalVerificationGate));
    }

    [Fact]
    public void Collections_AreDefensivelyCopiedUnmodifiableAndDeterministic()
    {
        var source = new List<string>
        {
            "tests/ZetaTests.cs",
            "src\\Feature.cs",
            "tests/ZetaTests.cs"
        };
        var first = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            new Harness(scope: source).Projector.Project(ReadyInput())).Projection;

        source.Clear();
        var second = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            new Harness(scope: ["tests/ZetaTests.cs", "src\\Feature.cs"])
                .Projector.Project(ReadyInput())).Projection;

        Assert.Equal(["src/Feature.cs", "tests/ZetaTests.cs"], first.LandingPaths);
        Assert.Equal(first, second);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.LandingPaths).Add("src/Mutation.cs"));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.ResourceKeys).Add("ownership:mutation"));
    }

    [Theory]
    [InlineData(GoalLifecycleState.Verifying, true, "LifecycleNotReady")]
    [InlineData(GoalLifecycleState.Verified, false, "GateNotReady")]
    public void ReadinessFailures_AreTypedExclusions(
        GoalLifecycleState lifecycleState,
        bool gateSatisfied,
        string expected)
    {
        var result = new Harness().Projector.Project(
            ReadyInput() with
            {
                LifecycleState = lifecycleState,
                VerificationGateSatisfied = gateSatisfied
            });

        Assert.Equal(
            expected,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result).Reason.ToString());
    }

    [Theory]
    [InlineData(null, MainRevision)]
    [InlineData("not-a-sha", MainRevision)]
    [InlineData(BranchRevision, null)]
    [InlineData(BranchRevision, "not-a-sha")]
    public void InitialRevisionFailures_AreUnknown(
        string? branchRevision,
        string? mainRevision)
    {
        var harness = new Harness(revisions: [new(branchRevision, mainRevision)]);

        AssertExcluded(
            harness.Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.RevisionUnknown);
        Assert.Equal(0, harness.ScopeReadCount);
        Assert.Equal(0, harness.MergeReadCount);
    }

    [Theory]
    [InlineData(OtherBranchRevision, MainRevision)]
    [InlineData(BranchRevision, OtherMainRevision)]
    public void ChangedRevisionReread_IsStale(string branchRevision, string mainRevision)
    {
        var harness = new Harness(revisions:
        [
            new(BranchRevision, MainRevision),
            new(branchRevision, mainRevision)
        ]);

        AssertExcluded(
            harness.Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.RevisionStale);
    }

    [Theory]
    [InlineData(null, MainRevision)]
    [InlineData(BranchRevision, null)]
    [InlineData("invalid", MainRevision)]
    [InlineData(BranchRevision, "invalid")]
    public void InvalidRevisionReread_IsUnknown(string? branchRevision, string? mainRevision)
    {
        var harness = new Harness(revisions:
        [
            new(BranchRevision, MainRevision),
            new(branchRevision, mainRevision)
        ]);

        AssertExcluded(
            harness.Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.RevisionUnknown);
    }

    [Theory]
    [InlineData(null, null, "RiskUnknown")]
    [InlineData(ChangeRiskTier.DocsOnly, null, "RiskUnknown")]
    [InlineData(ChangeRiskTier.Security, ConductorTransitionDecision.Escalate, "RiskNotAutoPromotable")]
    public void RiskFailures_AreTypedExclusions(
        ChangeRiskTier? riskTier,
        ConductorTransitionDecision? disposition,
        string expected)
    {
        var result = new Harness().Projector.Project(
            ReadyInput() with
            {
                ChangeRiskTier = riskTier,
                AutoPromotionDisposition = disposition
            });

        Assert.Equal(
            expected,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result).Reason.ToString());
    }

    [Fact]
    public void FailedAuthoritativeScope_DoesNotPromoteAdvisoryPaths()
    {
        var harness = new Harness(
            scope: ["src/advisory-only.cs"],
            scopeSucceeded: false);

        AssertExcluded(
            harness.Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.ScopeResolutionFailed);
        Assert.Equal(0, harness.MergeReadCount);
    }

    [Theory]
    [MemberData(nameof(EmptyScopes))]
    public void EmptyAuthoritativeScope_IsExcluded(IReadOnlyList<string> scope)
    {
        AssertExcluded(
            new Harness(scope: scope).Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.ScopeEmpty);
    }

    public static TheoryData<IReadOnlyList<string>> EmptyScopes => new()
    {
        Array.Empty<string>(),
        new[] { " ", "\t" }
    };

    [Fact]
    public void NonEmptyPathsWithoutAuthoritativeResourceKeys_AreExcluded()
    {
        AssertExcluded(
            new Harness(scope: ["docs/cohort.md"]).Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.ResourcesEmpty);
    }

    [Fact]
    public void MergeConflict_IsTypedExclusion()
    {
        AssertExcluded(
            new Harness(mergeIsClean: false).Projector.Project(ReadyInput()),
            GateReadyCandidateExclusionReason.MergeConflict);
    }

    [Fact]
    public void MergeConflict_CarriesExactReportedPathsInOrderAndCopiesThem()
    {
        var paths = new List<string> { "src/B.cs", "src/a.cs" };
        var projector = new GateReadyCandidateProjector(
            _ => new(BranchRevision, MainRevision),
            _ => new(true, ["src/Mcg.AgentOrchestrator.Core/Feature.cs"]),
            (_, _, _) => new(false, paths));

        var exclusion = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(projector.Project(ReadyInput()));
        paths.Clear();

        Assert.Equal(GateReadyCandidateExclusionReason.MergeConflict, exclusion.Reason);
        Assert.Equal(new[] { "src/B.cs", "src/a.cs" }, exclusion.ConflictPaths);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)exclusion.ConflictPaths).Clear());
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("merge")]
    public void DelegateFailures_FailClosedWithoutEscaping(string failingDelegate)
    {
        var harness = new Harness(failingDelegate: failingDelegate);

        var exception = Record.Exception(() => harness.Projector.Project(ReadyInput()));
        Assert.Null(exception);
        var result = harness.Projector.Project(ReadyInput());
        AssertExcluded(
            result,
            failingDelegate switch
            {
                "revision" => GateReadyCandidateExclusionReason.RevisionUnknown,
                "scope" => GateReadyCandidateExclusionReason.ScopeResolutionFailed,
                _ => GateReadyCandidateExclusionReason.MergeIndeterminate
            });
    }

    [Fact]
    public void Normalization_IsIdenticalToExistingParallelCandidateOwner()
    {
        var files = new[]
        {
            "docs\\guide.md",
            "src/Mcg.AgentOrchestrator.Infrastructure/Feature.cs",
            "tests/FeatureTests.cs"
        };
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            new AgentOrchestratorKernel(),
            AgentCatalog.Default().Agents,
            "Normalization parity");
        var existing = ConductorParallelAcceptanceCandidate.Create(goal, 0, files);
        var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            new Harness(scope: files).Projector.Project(ReadyInput())).Projection;

        Assert.Equal(existing.ScopePaths, projection.LandingPaths);
        Assert.Equal(existing.ResourceKeys, projection.ResourceKeys);
    }

    [Fact]
    public void Contract_IsClosedAndHasNoMutationOrCohortPorts()
    {
        Assert.Equal(
            ["Excluded", "Ready"],
            typeof(GateReadyCandidateProjectionResult)
                .GetNestedTypes(BindingFlags.NonPublic)
                .Select(type => type.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["_readMergeTree", "_readRevisions", "_resolveLandingScope"],
            typeof(GateReadyCandidateProjector)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(field => field.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static GateReadyCandidateInput ReadyInput() => new(
        GoalId,
        GoalLifecycleState.Verified,
        VerificationGateSatisfied: true,
        ChangeRiskTier.DocsOnly,
        ConductorTransitionDecision.Auto);

    private static void AssertExcluded(
        GateReadyCandidateProjectionResult result,
        GateReadyCandidateExclusionReason expected)
    {
        var exclusion = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result);
        Assert.Equal(expected, exclusion.Reason);
        Assert.Empty(exclusion.ConflictPaths);
    }

    private static bool ContainsGoalState(Type type, HashSet<Type> visited)
    {
        if (type == typeof(Goal) || type == typeof(TaskSpec) || type == typeof(GoalVerificationGate))
        {
            return true;
        }
        if (!visited.Add(type))
        {
            return false;
        }
        if (type.IsArray)
        {
            return ContainsGoalState(type.GetElementType()!, visited);
        }
        if (type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsGoalState(argument, visited)))
        {
            return true;
        }
        if (type.Namespace != typeof(GateReadyCandidateProjection).Namespace)
        {
            return false;
        }

        return type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => ContainsGoalState(field.FieldType, visited));
    }

    private sealed class Harness
    {
        private readonly Queue<GateReadyCandidateRevisionPair> _revisions;
        private readonly IReadOnlyList<string> _scope;
        private readonly bool _scopeSucceeded;
        private readonly bool _mergeIsClean;
        private readonly string? _failingDelegate;

        public Harness(
            IReadOnlyList<string>? scope = null,
            bool scopeSucceeded = true,
            bool mergeIsClean = true,
            IReadOnlyList<GateReadyCandidateRevisionPair>? revisions = null,
            string? failingDelegate = null)
        {
            _scope = scope ?? ["src/Mcg.AgentOrchestrator.Core/Feature.cs"];
            _scopeSucceeded = scopeSucceeded;
            _mergeIsClean = mergeIsClean;
            _failingDelegate = failingDelegate;
            _revisions = new Queue<GateReadyCandidateRevisionPair>(
                revisions ??
                [
                    new(BranchRevision, MainRevision),
                    new(BranchRevision, MainRevision)
                ]);
            Projector = new GateReadyCandidateProjector(
                ReadRevisions,
                ResolveScope,
                ReadMergeTree);
        }

        public GateReadyCandidateProjector Projector { get; }
        public int RevisionReadCount { get; private set; }
        public int ScopeReadCount { get; private set; }
        public int MergeReadCount { get; private set; }
        public string? MergeBranchRevision { get; private set; }
        public string? MergeMainRevision { get; private set; }

        private GateReadyCandidateRevisionPair ReadRevisions(GoalId _)
        {
            RevisionReadCount++;
            if (_failingDelegate == "revision")
            {
                throw new InvalidOperationException("revision unavailable");
            }

            return _revisions.Count > 1 ? _revisions.Dequeue() : _revisions.Peek();
        }

        private GateReadyLandingScopeObservation ResolveScope(GoalId _)
        {
            ScopeReadCount++;
            if (_failingDelegate == "scope")
            {
                throw new InvalidOperationException("scope unavailable");
            }

            return new GateReadyLandingScopeObservation(
                _scopeSucceeded,
                _scope,
                _scopeSucceeded ? null : "authoritative resolution failed");
        }

        private GateReadyMergeTreeObservation ReadMergeTree(
            GoalId _,
            string branchRevision,
            string mainRevision)
        {
            MergeReadCount++;
            MergeBranchRevision = branchRevision;
            MergeMainRevision = mainRevision;
            if (_failingDelegate == "merge")
            {
                throw new InvalidOperationException("merge unavailable");
            }

            return new GateReadyMergeTreeObservation(_mergeIsClean);
        }
    }
}
