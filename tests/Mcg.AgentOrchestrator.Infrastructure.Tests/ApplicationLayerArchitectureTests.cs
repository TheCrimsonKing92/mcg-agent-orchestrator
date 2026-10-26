using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using Mcg.AgentOrchestrator.App.Cli;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class ApplicationLayerArchitectureTests
{
    private const string ApplicationNamespace = "Mcg.AgentOrchestrator.App.Application";
    private const string CliNamespace = "Mcg.AgentOrchestrator.App.Cli";
    private const string RenderingNamespace = "Mcg.AgentOrchestrator.App.Rendering";

    // Parallel-safe: immutable inputs and one published architecture; no mutable external state.
    private static readonly Lazy<Architecture> SharedArchitecture = new(
        () => SharedArchitectureCache.Get(typeof(GoalMonitoringQuery).Assembly),
        LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Application_types_Presentation_boundary_Has_no_dependencies()
    {
        var (subjects, rule) = NoDependencyRule(ApplicationNamespace, CliNamespace, RenderingNamespace);
        var applicationTypes = subjects.GetObjects(SharedArchitecture.Value).ToArray();

        Assert.Contains(applicationTypes, type => type.FullName == typeof(GoalMonitoringQuery).FullName);
        Assert.Contains(applicationTypes, type => type.FullName == typeof(AgentDefinitionFactory).FullName);
        Assert.Contains(applicationTypes, type => type.FullName == typeof(GoalDispatchOperations).FullName);
        Assert.All(applicationTypes, type => Assert.Equal(ApplicationNamespace, type.Namespace.FullName));

        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    [Fact]
    public void Cli_types_Application_dependency_Control_names_command_handlers()
    {
        var (_, rule) = NoDependencyRule(CliNamespace, ApplicationNamespace);
        var failures = Failures(rule);

        Assert.NotEmpty(failures);
        Assert.True(failures.Any(result => result.EvaluatedObject is IType type
                && type.FullName == typeof(CliCommandHandlers).FullName
                && result.Description.Contains(nameof(CliCommandHandlers), StringComparison.Ordinal)),
            failures.ToErrorMessage());
    }

    private static (IObjectProvider<IType> Subjects, ICanBeEvaluated Rule) NoDependencyRule(
        string subjectNamespace,
        params string[] forbiddenNamespaces)
    {
        var subjects = Types().That().FollowCustomPredicate(
            type => type.Namespace.FullName.Equals(subjectNamespace, StringComparison.Ordinal),
            $"reside exactly in {subjectNamespace}");
        var forbidden = Types().That().FollowCustomPredicate(
            type => forbiddenNamespaces.Contains(type.Namespace.FullName, StringComparer.Ordinal),
            $"reside exactly in {string.Join(", ", forbiddenNamespaces)}");
        return (subjects, subjects.Should().NotDependOnAny(forbidden));
    }

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();
}
