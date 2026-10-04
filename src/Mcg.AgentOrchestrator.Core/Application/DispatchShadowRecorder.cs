namespace Mcg.AgentOrchestrator.Core;

public sealed class DispatchShadowRecorder
{
    public static DispatchShadowRecorder Default { get; } = new(DispatchTaskClassifier.Classify);
    private readonly Func<string?, IEnumerable<string>?, DispatchTaskClass> _classify;

    public DispatchShadowRecorder(Func<string?, IEnumerable<string>?, DispatchTaskClass> classify) =>
        _classify = classify ?? throw new ArgumentNullException(nameof(classify));

    public DispatchShadowDecision Record(AgentRole role, string? objective, string? description,
        IEnumerable<string>? changedPaths, string? provider, string? model, string? effort)
    {
        try
        {
            var taskClass = _classify($"{objective}\n{description}", changedPaths);
            return ShadowCascadePolicy.Decide(role, taskClass, provider, model, effort);
        }
        catch (Exception)
        {
            // Shadow evidence is optional and must never prevent execution.
            return new(DispatchTaskClass.Unrecorded, provider, model, effort,
                ShadowCascadePolicy.PolicyVersion, false);
        }
    }
}
