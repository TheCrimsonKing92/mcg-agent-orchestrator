namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class CompletionBuildOriginRejectedException : InvalidOperationException
{
    public CompletionBuildOriginRejectedException(string predicateName, string observed, string expected)
        : base($"Completion build operation origin identity is unproven: predicate {predicateName} failed; observed {Bound(observed)}; expected {Bound(expected)}.")
    {
        PredicateName = predicateName;
        Observed = Bound(observed);
        Expected = Bound(expected);
    }

    public string PredicateName { get; }
    public string Observed { get; }
    public string Expected { get; }

    internal static string Bound(string? value)
    {
        if (value is null) return "<null>";
        if (string.IsNullOrWhiteSpace(value)) return "<blank>";
        return value.Length <= 200 ? value : value[..197] + "...";
    }
}
