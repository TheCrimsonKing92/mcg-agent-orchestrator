using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorStewardBoundaryTests
{
    [Xunit.Fact]
    public void Steward_host_type_graph_has_no_operator_comms_or_discord_dependency()
    {
        var app = typeof(ConductorStewardHost).Assembly;
        var stewardTypes = app.GetTypes().Where(type =>
            type.Name.StartsWith("ConductorSteward", StringComparison.Ordinal) ||
            type.Name.StartsWith("IConductorSteward", StringComparison.Ordinal)).ToArray();
        Xunit.Assert.Contains(typeof(ConductorStewardHost), stewardTypes);
        foreach (var type in stewardTypes)
            Xunit.Assert.False(HasForbiddenReference(type), type.FullName);

        var operatorComms = Assembly.Load("Mcg.AgentOrchestrator.Infrastructure.OperatorComms");
        var negativeControl = operatorComms.GetTypes().First(type =>
            type.Name.Contains("StewardShadowRecommendationStore", StringComparison.Ordinal));
        Xunit.Assert.True(HasForbiddenReference(negativeControl));
    }

    private static bool HasForbiddenReference(Type type)
    {
        var referenced = new[] { type }
            .Concat(type.BaseType is null ? [] : [type.BaseType])
            .Concat(type.GetInterfaces())
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(field => field.FieldType))
            .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Select(property => property.PropertyType))
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType)));
        return referenced.Any(ForbiddenType);
    }

    private static bool ForbiddenType(Type type) =>
        type.Assembly.GetName().Name == "Mcg.AgentOrchestrator.Infrastructure.OperatorComms" ||
        type.Name.Contains("Discord", StringComparison.OrdinalIgnoreCase) ||
        (type.HasElementType && type.GetElementType() is { } element && ForbiddenType(element)) ||
        (type.IsGenericType && type.GetGenericArguments().Any(ForbiddenType));
}
