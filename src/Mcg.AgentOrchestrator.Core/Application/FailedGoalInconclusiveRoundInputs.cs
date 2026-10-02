using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>The candidate and evidence available to one Tester dispatch, frozen before it runs.</summary>
public sealed class FailedGoalInconclusiveRoundInputs : IEquatable<FailedGoalInconclusiveRoundInputs>
{
    public FailedGoalInconclusiveRoundInputs(string candidateIdentity, ImmutableArray<string> receiptIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateIdentity);
        CandidateIdentity = candidateIdentity;
        ReceiptIds = receiptIds.IsDefault ? [] : receiptIds.Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray();
    }

    public string CandidateIdentity { get; }
    public ImmutableArray<string> ReceiptIds { get; }

    public bool Equals(FailedGoalInconclusiveRoundInputs? other) =>
        other is not null && string.Equals(CandidateIdentity, other.CandidateIdentity, StringComparison.Ordinal) &&
        ReceiptIds.SequenceEqual(other.ReceiptIds, StringComparer.Ordinal);

    public override bool Equals(object? obj) => obj is FailedGoalInconclusiveRoundInputs other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CandidateIdentity, StringComparer.Ordinal);
        foreach (var id in ReceiptIds)
            hash.Add(id, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed record FailedGoalInconclusiveRoundPair(
    FailedGoalInconclusiveRoundInputs? Previous, FailedGoalInconclusiveRoundInputs Current)
{
    public bool InputsUnchanged => Previous is not null && Previous.Equals(Current);

    public string DescribeUnchanged(string? latestSummary) =>
        $"Candidate: {Current.CandidateIdentity}; receipt ids: " +
        $"{(Current.ReceiptIds.IsEmpty ? "none" : string.Join(", ", Current.ReceiptIds))}; " +
        $"latest current-round receipt: {latestSummary}";
}
