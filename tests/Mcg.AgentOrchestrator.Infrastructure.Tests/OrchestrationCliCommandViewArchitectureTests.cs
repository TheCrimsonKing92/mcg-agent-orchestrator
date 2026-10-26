using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class OrchestrationCliCommandViewArchitectureTests
{
    private const string OrchestrationNamespace = "Mcg.AgentOrchestrator.App.Orchestration";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";
    private const string ReadModelsNamespace = "Mcg.AgentOrchestrator.App.ReadModels";
    private const string ExemptRootType = OrchestrationNamespace + ".BoardFillCliGoalIntake";

    // Sanctioned in-process CLI intake, already exempt in ConductorCliExecutorArchitectureTests.
    private static readonly string[] ExemptRoots = [ExemptRootType];

    // Parallel-safe: immutable inputs and one published architecture; no mutable external state.
    private static readonly Lazy<Architecture> SharedArchitecture = new(
        () => SharedArchitectureCache.Get(typeof(OrphanFixtureReaper).Assembly),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Orchestration_types_Cli_namespace_boundary_Has_no_dependencies()
    {
        var (subjects, forbidden, rule) = CliBoundaryRule(ExemptRoots);
        var subjectTypes = subjects.GetObjects(SharedArchitecture.Value).ToArray();
        var forbiddenTypes = forbidden.GetObjects(SharedArchitecture.Value).ToArray();

        Assert.Contains(subjectTypes, type => type.FullName == typeof(ConductorExperimentWatch).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(StalledEpicAttention).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(DefaultStateMover).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(ConductorDriver).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(OrphanFixtureReaper).FullName);
        Assert.DoesNotContain(subjectTypes, type => OutermostTypeName(type) == ExemptRootType);
        Assert.All(subjectTypes, type => Assert.Equal(OrchestrationNamespace, type.Namespace.FullName));
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(RepoProcessCliCommand).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(ConsoleViews).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(CliPersistentStateRunner).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(CliExitException).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(EpicProgressReadModel).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(EpicPlanStatusReader).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(ExperimentReading).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(SystemConductorLockProbe).FullName);

        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Empty_exemptions_Control_names_exactly_board_fill_intake()
    {
        var (_, _, rule) = CliBoundaryRule([]);
        var failures = Failures(rule);

        Assert.NotEmpty(failures);
        var failingSubjects = failures
            .Select(result => Assert.IsAssignableFrom<IType>(result.EvaluatedObject))
            .Select(OutermostTypeName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(failingSubjects.SetEquals([ExemptRootType]), failures.ToErrorMessage());
    }

    [Fact]
    public void Moved_types_Namespace_placement_Matches_owner_layer()
    {
        System.Type[] readModels =
        [
            typeof(EpicPlanStatusReader), typeof(EpicPlanView), typeof(EpicPlanItemStatus),
            typeof(EpicPlanNextStep), typeof(EpicPlanGoalClaimSnapshot), typeof(EpicPlanSourceFacts),
            typeof(EpicOutcomeEvaluator), typeof(EpicProgressBucket), typeof(ExperimentReading),
            typeof(ExperimentGoals), typeof(ExperimentAlternatingGateReading), typeof(ExperimentGateAttempts),
            typeof(ExperimentGateStopCount), typeof(ExperimentHostLedgers), typeof(ExperimentMembership),
            typeof(ExperimentCohortReading), typeof(ExperimentTwinReading), typeof(ExperimentHoursToLand),
            typeof(ExperimentIncrementalStopCount), typeof(CliOwnerDigestRetryIntents)
        ];
        Assert.All(readModels, type => Assert.Equal(ReadModelsNamespace, type.Namespace));

        System.Type[] orchestration =
        [
            typeof(IConductorLockProbe), typeof(ConductorLockOwnership), typeof(SystemConductorLockProbe),
            typeof(CriticalDispatchLifecycleCheckpoint), typeof(CriticalDispatchLifecycleEventGate)
        ];
        Assert.All(orchestration, type => Assert.Equal(OrchestrationNamespace, type.Namespace));
        Assert.Equal(CliNamespace, typeof(GoalRefinementWorkOutcomeReporter).Namespace);
    }

    [Fact]
    public void ReadModels_types_Cli_dependencies_Residual_is_single_progress_call()
    {
        var pairs = SharedArchitecture.Value.Types
            .Where(type => type.Namespace.FullName.Equals(ReadModelsNamespace, StringComparison.Ordinal))
            .SelectMany(type => type.Dependencies
                .Where(dependency => IsInCliNamespace(dependency.Target))
                .Select(dependency => (Source: OutermostTypeName(type), Target: OutermostTypeName(dependency.Target))))
            .ToHashSet();

        Assert.True(pairs.SetEquals(
            [(typeof(EpicPlanStatusReader).FullName!, typeof(EpicProgressReadModel).FullName!)]),
            "ReadModels-to-Cli dependencies: " + string.Join(", ", pairs.OrderBy(pair => pair.Source)
                .ThenBy(pair => pair.Target).Select(pair => $"{pair.Source} -> {pair.Target}")));
    }

    private static (IObjectProvider<IType> Subjects, IObjectProvider<IType> Forbidden, ICanBeEvaluated Rule)
        CliBoundaryRule(IReadOnlyCollection<string> exemptRoots)
    {
        var subjects = Types().That().FollowCustomPredicate(
            type => type.Namespace.FullName.Equals(OrchestrationNamespace, StringComparison.Ordinal)
                && !exemptRoots.Contains(OutermostTypeName(type), StringComparer.Ordinal),
            "reside exactly in Orchestration outside the named exempt roots and their nested types");
        var forbidden = Types().That().FollowCustomPredicate(
            IsInCliNamespace,
            "reside in Cli or its descendant namespaces");
        return (subjects, forbidden, subjects.Should().NotDependOnAny(forbidden));
    }

    private static bool IsInCliNamespace(IType type) =>
        type.Namespace.FullName.Equals(CliNamespace, StringComparison.Ordinal)
        || type.Namespace.FullName.StartsWith(CliNamespace + ".", StringComparison.Ordinal);

    // Metadata separates declaring-type ancestors with '+' or '/'; use the exact root at any depth.
    private static string OutermostTypeName(IType type) => type.FullName.Split('+', '/')[0];

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();
}
