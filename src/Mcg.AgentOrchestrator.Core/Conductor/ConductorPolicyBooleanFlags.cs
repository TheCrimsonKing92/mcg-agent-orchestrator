using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>The policy record's boolean constructor members define the experiment flag domain.</summary>
public static class ConductorPolicyBooleanFlags
{
    private static readonly IReadOnlyDictionary<string, PropertyInfo> Properties =
        new ReadOnlyDictionary<string, PropertyInfo>(typeof(ConductorAutonomyPolicy).GetConstructors().Single()
            .GetParameters().Where(parameter => parameter.ParameterType == typeof(bool))
            .ToDictionary(parameter => JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!),
                parameter => typeof(ConductorAutonomyPolicy).GetProperty(parameter.Name!)!, StringComparer.Ordinal));

    public static IReadOnlyCollection<string> Names { get; } = Array.AsReadOnly(Properties.Keys.ToArray());

    public static bool IsAllowed(string? propertyName) => propertyName is not null && Properties.ContainsKey(propertyName);

    public static bool Read(ConductorAutonomyPolicy policy, string propertyName) =>
        Properties.TryGetValue(propertyName, out var property) ? (bool)property.GetValue(policy)! :
            throw new ArgumentException($"property-not-allowlisted {propertyName}");
}
