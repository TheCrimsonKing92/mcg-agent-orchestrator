using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class ConductorCliExecutorArchitectureTests
{
    private const string OrchestrationNamespace = "Mcg.AgentOrchestrator.App.Orchestration";
    private const string ExemptRootType = "Mcg.AgentOrchestrator.App.Orchestration.BoardFillCliGoalIntake";

    // Parallel-safe: immutable inputs and one published architecture; no mutable external state.
    private static readonly Lazy<Architecture> SharedArchitecture = new(
        () => new ArchLoader().LoadAssemblies(typeof(BoardFillCliGoalIntake).Assembly).Build(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Orchestration_types_Only_exempt_intake_depends_on_executors()
    {
        var (subjects, rule) = ExecutorRule([ExemptRootType]);
        var orchestrationTypes = subjects.GetObjects(SharedArchitecture.Value).ToArray();

        Assert.Contains(orchestrationTypes, type => type.FullName == typeof(ConductorContinuitySupervisor).FullName);
        Assert.Contains(orchestrationTypes, type => type.FullName == typeof(PendingStoreSetupGate).FullName);
        Assert.Contains(orchestrationTypes, type => type.FullName == typeof(WorkspaceStoreSetup).FullName);
        Assert.All(orchestrationTypes, type => Assert.Equal(OrchestrationNamespace, type.Namespace.FullName));

        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Empty_exemptions_Control_names_exactly_board_fill_intake()
    {
        var (_, rule) = ExecutorRule([]);
        var failures = Failures(rule);

        Assert.NotEmpty(failures);
        var failingSubjects = failures
            .Select(result => Assert.IsAssignableFrom<IType>(result.EvaluatedObject))
            .Where(type => type.FullName == ExemptRootType || OutermostTypeName(type) != ExemptRootType)
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(failingSubjects.SetEquals([ExemptRootType]), failures.ToErrorMessage());
    }

    private static (IObjectProvider<IType> Subjects, ICanBeEvaluated Rule) ExecutorRule(
        IReadOnlyCollection<string> exemptRoots)
    {
        var subjects = Types().That().FollowCustomPredicate(
            type => type.Namespace.FullName.Equals(OrchestrationNamespace, StringComparison.Ordinal)
                && !exemptRoots.Contains(OutermostTypeName(type), StringComparer.Ordinal),
            "reside exactly in Orchestration outside the named exempt roots and their nested types");
        string[] executorRoots =
        [
            typeof(CliPersistentStateRunner).FullName!,
            typeof(CliCommandDispatcher).FullName!,
            typeof(CliCommandHandlers).FullName!
        ];
        var forbidden = Types().That().FollowCustomPredicate(
            type => executorRoots.Contains(OutermostTypeName(type), StringComparer.Ordinal),
            "are CLI executors or types nested inside them");

        return (subjects, subjects.Should().NotDependOnAny(forbidden));
    }

    // Metadata separates declaring-type ancestors with '+' or '/'; use the exact root at any depth.
    private static string OutermostTypeName(IType type) => type.FullName.Split('+', '/')[0];

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();
}
