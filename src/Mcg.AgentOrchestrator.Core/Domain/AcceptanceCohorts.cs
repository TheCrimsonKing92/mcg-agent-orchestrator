using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCohortMemberBinding : IEquatable<AcceptanceCohortMemberBinding>
{
    public AcceptanceCohortMemberBinding(
        GoalId goalId,
        string branchRevision,
        string candidateRevision,
        IReadOnlyList<string> landingPaths,
        IReadOnlyList<string> resourceKeys)
    {
        ArgumentNullException.ThrowIfNull(landingPaths);
        ArgumentNullException.ThrowIfNull(resourceKeys);
        GoalId = goalId;
        BranchRevision = NormalizeRevision(branchRevision, nameof(branchRevision));
        CandidateRevision = NormalizeRevision(candidateRevision, nameof(candidateRevision));
        if (landingPaths.Count == 0)
        {
            throw new ArgumentException("A cohort member requires authoritative landing paths.", nameof(landingPaths));
        }
        if (resourceKeys.Count == 0)
        {
            throw new ArgumentException("A cohort member requires authoritative resource keys.", nameof(resourceKeys));
        }

        LandingPaths = Copy(landingPaths);
        ResourceKeys = Copy(resourceKeys);
    }

    public GoalId GoalId { get; }
    public string BranchRevision { get; }
    public string CandidateRevision { get; }
    public IReadOnlyList<string> LandingPaths { get; }
    public IReadOnlyList<string> ResourceKeys { get; }

    public bool Equals(AcceptanceCohortMemberBinding? other) =>
        other is not null &&
        GoalId == other.GoalId &&
        BranchRevision.Equals(other.BranchRevision, StringComparison.Ordinal) &&
        CandidateRevision.Equals(other.CandidateRevision, StringComparison.Ordinal) &&
        LandingPaths.SequenceEqual(other.LandingPaths, StringComparer.Ordinal) &&
        ResourceKeys.SequenceEqual(other.ResourceKeys, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as AcceptanceCohortMemberBinding);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GoalId);
        hash.Add(BranchRevision, StringComparer.Ordinal);
        hash.Add(CandidateRevision, StringComparer.Ordinal);
        foreach (var path in LandingPaths) hash.Add(path, StringComparer.Ordinal);
        foreach (var resource in ResourceKeys) hash.Add(resource, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static string NormalizeRevision(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 40 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A cohort revision must be a full 40-character Git SHA.", parameterName);
        }
        return normalized;
    }

    private static ReadOnlyCollection<string> Copy(IEnumerable<string> values) =>
        Array.AsReadOnly(values.ToArray());
}

public sealed class AcceptanceCohortIdentity : IEquatable<AcceptanceCohortIdentity>
{
    public const string Version = "cohort-v1";

    private AcceptanceCohortIdentity(
        IReadOnlyList<AcceptanceCohortMemberBinding> members,
        string observedMainRevision,
        string combinedTreeRevision,
        string manifestIdentity,
        string value)
    {
        Members = Array.AsReadOnly(members.ToArray());
        ObservedMainRevision = observedMainRevision;
        CombinedTreeRevision = combinedTreeRevision;
        ManifestIdentity = manifestIdentity;
        Value = value;
    }

    public IReadOnlyList<AcceptanceCohortMemberBinding> Members { get; }
    public string ObservedMainRevision { get; }
    public string CombinedTreeRevision { get; }
    public string ManifestIdentity { get; }
    public string Value { get; }

    public static AcceptanceCohortIdentity Create(
        IReadOnlyList<AcceptanceCohortMemberBinding> members,
        string observedMainRevision,
        string combinedTreeRevision,
        string manifestIdentity)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count != 2)
        {
            throw new ArgumentException("An acceptance cohort contains exactly two members.", nameof(members));
        }
        if (members[0].GoalId == members[1].GoalId)
        {
            throw new ArgumentException("An acceptance cohort requires two distinct goals.", nameof(members));
        }

        var main = AcceptanceCohortMemberBinding.NormalizeRevision(observedMainRevision, nameof(observedMainRevision));
        var tree = AcceptanceCohortMemberBinding.NormalizeRevision(combinedTreeRevision, nameof(combinedTreeRevision));
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestIdentity);
        var normalizedManifest = manifestIdentity.Trim();

        using var stream = new MemoryStream();
        WriteField(stream, Version);
        foreach (var member in members)
        {
            WriteField(stream, member.GoalId.Value);
            WriteField(stream, member.BranchRevision);
            WriteField(stream, member.CandidateRevision);
        }
        WriteField(stream, main);
        WriteField(stream, tree);
        WriteField(stream, normalizedManifest);
        var digest = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));

        return new AcceptanceCohortIdentity(
            members,
            main,
            tree,
            normalizedManifest,
            $"{Version}-{digest}");
    }

    public bool Equals(AcceptanceCohortIdentity? other) =>
        other is not null && Value.Equals(other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as AcceptanceCohortIdentity);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;

    private static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}

public enum AcceptanceCohortGateOutcome
{
    Passed,
    Failed,
    InfrastructureFailure,
    Invalidated
}

public enum AcceptanceCohortAttributionOutcome
{
    NotApplicable,
    FirstMemberFailed,
    SecondMemberFailed,
    BothMembersFailed,
    InteractionOnly,
    Indeterminate
}

public sealed record AcceptanceCohortPartitionReceipt(
    string ReceiptId,
    GoalId GoalId,
    int MemberOrdinal,
    string CandidateRevision,
    string ObservedMainRevision,
    string? TreeRevision,
    string ManifestIdentity,
    AcceptanceCohortGateOutcome Outcome,
    long ElapsedMilliseconds,
    IReadOnlyList<string> TestResultPaths);

public sealed record AcceptanceCohortReceipt(
    string ReceiptId,
    AcceptanceCohortIdentity Identity,
    AcceptanceCohortGateOutcome Outcome,
    DateTimeOffset CompletedAt,
    long GateElapsedMilliseconds,
    IReadOnlyList<string> FailedChecks,
    AcceptanceCohortAttributionOutcome Attribution = AcceptanceCohortAttributionOutcome.NotApplicable,
    bool ValidForLanding = false);
