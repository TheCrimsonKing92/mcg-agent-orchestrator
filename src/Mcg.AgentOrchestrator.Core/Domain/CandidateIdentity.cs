namespace Mcg.AgentOrchestrator.Core;

/// <summary>The goal change, its base dependencies, and the acceptance plan that judges it.</summary>
public sealed record CandidateIdentity
{
    public CandidateIdentity(string patchId, string baseClosureHash, string manifestIdentity)
    {
        PatchId = Normalize(patchId, nameof(patchId));
        BaseClosureHash = Normalize(baseClosureHash, nameof(baseClosureHash));
        ManifestIdentity = Normalize(manifestIdentity, nameof(manifestIdentity));
    }

    public string PatchId { get; }
    public string BaseClosureHash { get; }
    public string ManifestIdentity { get; }
    public string Canonical => $"candidate:v1:{PatchId}:{BaseClosureHash}:{ManifestIdentity}";

    public static bool AreSameCandidate(CandidateIdentity? left, CandidateIdentity? right) =>
        left is not null && right is not null && left == right;

    public static bool TryParse(string? value, out CandidateIdentity? identity)
    {
        identity = null;
        if (value is null || !value.StartsWith("candidate:v1:", StringComparison.Ordinal))
            return false;
        var parts = value.Split(':');
        if (parts.Length != 5 || parts.Skip(2).Any(string.IsNullOrWhiteSpace))
            return false;
        identity = new CandidateIdentity(parts[2], parts[3], parts[4]);
        return true;
    }

    public override string ToString() => Canonical;

    private static string Normalize(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Identity component cannot be empty.", name)
            : value.Trim().ToLowerInvariant();
}
