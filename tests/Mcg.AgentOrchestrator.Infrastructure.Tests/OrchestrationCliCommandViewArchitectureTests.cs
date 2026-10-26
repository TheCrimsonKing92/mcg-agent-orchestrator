using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class OrchestrationCliCommandViewArchitectureTests
{
    private const string OrchestrationNamespace = "Mcg.AgentOrchestrator.App.Orchestration";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";

    // Parallel-safe: immutable inputs and one published architecture; no mutable external state.
    private static readonly Lazy<Architecture> SharedArchitecture = new(
        () => new ArchLoader().LoadAssemblies(typeof(OrphanFixtureReaper).Assembly).Build(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Orchestration_types_Cli_command_view_boundary_Has_no_dependencies()
    {
        var (subjects, forbidden, rule) = NoCommandViewDependencyRule(OrchestrationNamespace);
        var subjectTypes = subjects.GetObjects(SharedArchitecture.Value).ToArray();
        var forbiddenTypes = forbidden.GetObjects(SharedArchitecture.Value).ToArray();

        Assert.Contains(subjectTypes, type => type.FullName == typeof(ConductorGroupedGateAttemptCoordinator).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(OrphanFixtureReaper).FullName);
        Assert.Contains(subjectTypes, type => type.FullName == typeof(ConductorWatchProgressReporter).FullName);
        Assert.All(subjectTypes, type => Assert.Equal(OrchestrationNamespace, type.Namespace.FullName));
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(RepoProcessCliCommand).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(CliConductorCommand).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(ConsoleViews).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(GoalTextView).FullName);
        Assert.Contains(forbiddenTypes, type => type.FullName == typeof(StatusAgentReadView).FullName);
        Assert.DoesNotContain(forbiddenTypes, type => type.FullName == typeof(CliPersistentStateRunner).FullName);

        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Cli_types_Command_view_dependency_Control_names_handlers()
    {
        var (_, _, rule) = NoCommandViewDependencyRule(CliNamespace);
        var failures = Failures(rule);

        Assert.NotEmpty(failures);
        Assert.True(failures.Any(result => result.EvaluatedObject is IType type
                && type.FullName == typeof(CliCommandHandlers).FullName),
            failures.ToErrorMessage());
    }

    private static (IObjectProvider<IType> Subjects, IObjectProvider<IType> Forbidden, ICanBeEvaluated Rule)
        NoCommandViewDependencyRule(string subjectNamespace)
    {
        var subjects = Types().That().FollowCustomPredicate(
            type => type.Namespace.FullName.Equals(subjectNamespace, StringComparison.Ordinal),
            $"reside exactly in {subjectNamespace}");
        var forbidden = Types().That().FollowCustomPredicate(
            type => IsCliCommandOrView(type.FullName),
            "are CLI command or view classes or their nested types");
        return (subjects, forbidden, subjects.Should().NotDependOnAny(forbidden));
    }

    private static bool IsCliCommandOrView(string fullName)
    {
        const string prefix = CliNamespace + ".";
        if (!fullName.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var name = fullName[prefix.Length..];
        var nestedSeparator = name.IndexOfAny(['+', '/']);
        if (nestedSeparator >= 0) name = name[..nestedSeparator];
        return name.EndsWith("Command", StringComparison.Ordinal)
            || name.EndsWith("TextView", StringComparison.Ordinal)
            || name.EndsWith("ReadView", StringComparison.Ordinal)
            || name.EndsWith("Views", StringComparison.Ordinal);
    }

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();
}
