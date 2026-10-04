using System.Collections.Frozen;

namespace Mcg.AgentOrchestrator.Core;

// A presentation snapshot of catalog membership, never a model-selection policy.
public sealed record BoundModelSet
{
    public IReadOnlySet<string> ModelNames { get; }
    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    private BoundModelSet(IEnumerable<string> names, bool isAvailable, string? reason)
    {
        ModelNames = names.Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(ModelSegment).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        IsAvailable = isAvailable;
        UnavailableReason = reason;
    }

    public static BoundModelSet Available(IEnumerable<string> names) => new(names, true, null);
    public static BoundModelSet Unavailable(string reason) => new([], false, reason);

    public static BoundModelSet FromCatalogs(
        IEnumerable<AgentDefinition> agents,
        ModelFunctionCatalog modelFunctions) =>
        Available(agents.SelectMany(agent => new[] { agent.Model.ModelName, agent.ComplexModel?.ModelName })
            .Concat(modelFunctions.Bindings.SelectMany(binding =>
                new[] { binding.Model.ModelName, binding.Subscription?.ModelAlias }))
            .OfType<string>());

    public bool Contains(string name) => ModelNames.Contains(ModelSegment(name));

    private static string ModelSegment(string name) => name[(name.LastIndexOf('/') + 1)..];
}
