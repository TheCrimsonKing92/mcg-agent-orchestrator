using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class ConsoleCliExecutorArchitectureTests
{
    private const string ConsoleNamespace = "Mcg.AgentOrchestrator.App.OwnerConsole";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";
    private static readonly string[] ExecutorNames =
    [
        typeof(CliPersistentStateRunner).FullName!,
        typeof(CliCommandDispatcher).FullName!,
        typeof(CliCommandHandlers).FullName!
    ];

    // Parallel-safe: immutable inputs and one published architecture; no mutable external state.
    private static readonly Lazy<Architecture> SharedArchitecture = new(
        () => new ArchLoader().LoadAssemblies(typeof(AttentionAnswerHandlerAdapter).Assembly).Build(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Console_types_Executor_boundary_Has_no_dependencies()
    {
        var (subjects, rule) = NoExecutorDependencyRule(ConsoleNamespace);
        var consoleTypes = subjects.GetObjects(SharedArchitecture.Value).ToArray();

        Assert.Contains(consoleTypes, type => type.FullName == typeof(AttentionAnswerHandlerAdapter).FullName);
        Assert.Contains(consoleTypes, type => type.FullName == typeof(OwnerConsoleScreenController).FullName);
        Assert.Contains(consoleTypes, type => type.FullName == typeof(ExperimentDecideIntentSubmitter).FullName);
        Assert.All(consoleTypes, type => Assert.Equal(ConsoleNamespace, type.Namespace.FullName));

        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Cli_types_Executor_dependency_Control_names_persistent_runner()
    {
        var (_, rule) = NoExecutorDependencyRule(CliNamespace);
        var failures = Failures(rule);

        Assert.NotEmpty(failures);
        Assert.True(failures.Any(result => result.EvaluatedObject is IType type
                && type.FullName == typeof(CliPersistentStateRunner).FullName),
            failures.ToErrorMessage());
    }

    private static (IObjectProvider<IType> Subjects, ICanBeEvaluated Rule) NoExecutorDependencyRule(
        string subjectNamespace)
    {
        var subjects = Types().That().FollowCustomPredicate(
            type => type.Namespace.FullName.Equals(subjectNamespace, StringComparison.Ordinal),
            $"reside exactly in {subjectNamespace}");
        var forbidden = Types().That().FollowCustomPredicate(
            type => ExecutorNames.Any(name => type.FullName.Equals(name, StringComparison.Ordinal)
                || type.FullName.StartsWith(name + "+", StringComparison.Ordinal)
                || type.FullName.StartsWith(name + "/", StringComparison.Ordinal)),
            "are CLI executor types or their nested types");
        return (subjects, subjects.Should().NotDependOnAny(forbidden));
    }

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();
}
