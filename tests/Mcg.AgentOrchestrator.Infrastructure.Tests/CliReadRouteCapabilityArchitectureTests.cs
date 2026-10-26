using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Conditions;
using ArchUnitNET.Fluent.Extensions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Architecture = ArchUnitNET.Domain.Architecture;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

// Parallel-safe: immutable assembly inputs and one published architecture; no external state.
public sealed class CliReadRouteCapabilityArchitectureTests
{
    private static readonly Lazy<Architecture> SharedArchitecture = new(() =>
        SharedArchitectureCache.Get(
            typeof(CliReadOnlyCommandRunner).Assembly,
            typeof(IOrchestratorStateQueries).Assembly),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Xunit.Fact]
    public void ReadRoute_ContainsCommandSubjectsAndHasNoWriterDependencies()
    {
        var subjects = DeriveReadRouteSubjects(SharedArchitecture.Value);
        foreach (var expected in new[]
        {
            typeof(CliReadOnlyCommandRunner), typeof(CliInspectionQueryCommand),
            typeof(CliReadinessQueryCommand), typeof(GoalBoardCommand), typeof(CliTaskQueryCommand)
        })
            Xunit.Assert.Contains(subjects, subject => subject.FullName == expected.FullName);

        var failures = Failures(ReadRouteRule(subjects));
        Xunit.Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Xunit.Fact]
    public void WriterRunner_NegativeControlNamesSubjectAndTransactionalRepository()
    {
        var subject = SharedArchitecture.Value.Types.Single(type => type.FullName == typeof(CliPersistentStateRunner).FullName);
        var failures = Failures(ReadRouteRule([subject]));

        Xunit.Assert.NotEmpty(failures);
        Xunit.Assert.Contains(failures, result =>
            result.Description.Contains(typeof(CliPersistentStateRunner).FullName!, StringComparison.Ordinal)
            && result.Description.Contains(typeof(ITransactionalOrchestratorStateRepository).FullName!,
                StringComparison.Ordinal));
    }

    private static IType[] DeriveReadRouteSubjects(Architecture architecture)
    {
        var runner = architecture.Types.Single(type => type.FullName == typeof(CliReadOnlyCommandRunner).FullName);
        var roots = runner.Dependencies.Select(dependency => dependency.Target)
            .Where(type => type.Namespace.FullName == "Mcg.AgentOrchestrator.App.Cli")
            .Append(runner).Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        return architecture.Types.Where(type => roots.Contains(type.FullName)
            || roots.Any(root => type.FullName.StartsWith(root + "+", StringComparison.Ordinal)
                || type.FullName.StartsWith(root + "/", StringComparison.Ordinal))).ToArray();
    }

    private static ICanBeEvaluated ReadRouteRule(IType[] subjects)
    {
        var writerTypes = new[]
        {
            typeof(IOrchestratorStateRepository).FullName!,
            typeof(ITransactionalOrchestratorStateRepository).FullName!,
            typeof(IOrchestratorStateOutboxRepository).FullName!,
            typeof(SqliteOrchestratorStateRepository).FullName!
        }.ToHashSet(StringComparer.Ordinal);
        return Types().That().Are(subjects).Should().FollowCustomCondition(type =>
        {
            var forbidden = type.Dependencies.Select(dependency => dependency.Target.FullName)
                .Where(writerTypes.Contains).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            return new ConditionResult(type, forbidden.Length == 0,
                $"{type.FullName} depends on writer types: {string.Join(", ", forbidden)}");
        }, "depend only on state query capabilities, without writer types");
    }

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();
}
