namespace Mcg.AgentOrchestrator.Core;

// The cost/quality tier a model binding sits in — the axis the orchestrator can route among:
// free local (Ollama) → cheap lab API (Haiku-class; API-billed, so OFF the subscription budget the
// cost gate guards) → capable paid (subscription CLI / capable API). This is the first-class hook
// for budget-aware routing (e.g. "use the cheap-API lane unless the scorecard says it's inadequate").
public enum ModelLane
{
    Local,
    CheapApi,
    Capable
}

// A model the orchestrator invokes for ITS OWN function — judging a diff, sampling N plans,
// summarizing context, a routing oracle — as distinct from a worker AgentDefinition that executes a
// tracked goal Task via dispatch. These never get assigned tasks, have no worktree/sandbox, and are
// not part of the SDLC role catalog. Identified by an OPEN-ENDED Purpose string so a new internal
// function needs no enum or taxonomy change (the lesson from cramming "Judge" into AgentRole).
public sealed record ModelFunctionBinding(
    string Purpose,
    ModelLane Lane,
    ModelProfile Model,
    string? Name = null,
    SubscriptionLaunchProfile? Subscription = null);

public sealed record ModelFunctionCatalog(IReadOnlyList<ModelFunctionBinding> Bindings)
{
    public static ModelFunctionCatalog Empty { get; } = new([]);

    public IReadOnlyList<ModelFunctionBinding> ForPurpose(string purpose) =>
        Bindings
            .Where(binding => string.Equals(binding.Purpose, purpose, StringComparison.OrdinalIgnoreCase))
            .ToList();
}

public static class ModelFunctionPurposes
{
    // Known orchestrator-internal model functions. A new internal use adds its own constant here —
    // no enum churn, no exclusion sets, no square peg.
    public const string AcceptanceJudge = "acceptance-judge";
    public const string SpecRefiner = "spec-refiner";
    public const string StewardTriage = "steward-triage";
}
