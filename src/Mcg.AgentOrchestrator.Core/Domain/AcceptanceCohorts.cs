using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCohortMemberBinding : IEquatable<AcceptanceCohortMemberBinding>
{
    public AcceptanceCohortMemberBinding(
        GoalId goalId,
        string branchRevision,
        string candidateRevision,
        IReadOnlyList<string> landingPaths,
        IReadOnlyList<string> resourceKeys,
        ChangeRiskTier changeRiskTier,
        ConductorTransitionDecision autoPromotionDisposition,
        string mergeStatus,
        string mergeReason)
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
        if (!Enum.IsDefined(changeRiskTier))
        {
            throw new ArgumentOutOfRangeException(nameof(changeRiskTier));
        }
        if (!Enum.IsDefined(autoPromotionDisposition))
        {
            throw new ArgumentOutOfRangeException(nameof(autoPromotionDisposition));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(mergeStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergeReason);

        LandingPaths = Copy(landingPaths);
        ResourceKeys = Copy(resourceKeys);
        ChangeRiskTier = changeRiskTier;
        AutoPromotionDisposition = autoPromotionDisposition;
        MergeStatus = mergeStatus.Trim();
        MergeReason = mergeReason.Trim();
    }

    public GoalId GoalId { get; }
    public string BranchRevision { get; }
    public string CandidateRevision { get; }
    public IReadOnlyList<string> LandingPaths { get; }
    public IReadOnlyList<string> ResourceKeys { get; }
    public ChangeRiskTier ChangeRiskTier { get; }
    public ConductorTransitionDecision AutoPromotionDisposition { get; }
    public string MergeStatus { get; }
    public string MergeReason { get; }

    public bool Equals(AcceptanceCohortMemberBinding? other) =>
        other is not null &&
        GoalId == other.GoalId &&
        BranchRevision.Equals(other.BranchRevision, StringComparison.Ordinal) &&
        CandidateRevision.Equals(other.CandidateRevision, StringComparison.Ordinal) &&
        LandingPaths.SequenceEqual(other.LandingPaths, StringComparer.Ordinal) &&
        ResourceKeys.SequenceEqual(other.ResourceKeys, StringComparer.Ordinal) &&
        ChangeRiskTier == other.ChangeRiskTier &&
        AutoPromotionDisposition == other.AutoPromotionDisposition &&
        MergeStatus.Equals(other.MergeStatus, StringComparison.Ordinal) &&
        MergeReason.Equals(other.MergeReason, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as AcceptanceCohortMemberBinding);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GoalId);
        hash.Add(BranchRevision, StringComparer.Ordinal);
        hash.Add(CandidateRevision, StringComparer.Ordinal);
        foreach (var path in LandingPaths) hash.Add(path, StringComparer.Ordinal);
        foreach (var resource in ResourceKeys) hash.Add(resource, StringComparer.Ordinal);
        hash.Add(ChangeRiskTier);
        hash.Add(AutoPromotionDisposition);
        hash.Add(MergeStatus, StringComparer.Ordinal);
        hash.Add(MergeReason, StringComparer.Ordinal);
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
    public const string Version = "cohort-v2";

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
            WriteField(stream, member.LandingPaths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var path in member.LandingPaths) WriteField(stream, path);
            WriteField(stream, member.ResourceKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var resource in member.ResourceKeys) WriteField(stream, resource);
            WriteField(stream, member.ChangeRiskTier.ToString());
            WriteField(stream, member.AutoPromotionDisposition.ToString());
            WriteField(stream, member.MergeStatus);
            WriteField(stream, member.MergeReason);
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
    int? GateExitCode,
    IReadOnlyList<string> GateTestResultPaths,
    AcceptanceCohortAttributionOutcome Attribution = AcceptanceCohortAttributionOutcome.NotApplicable,
    bool ValidForLanding = false)
{
    public bool HasAuthoritativeLandingEvidence =>
        Outcome == AcceptanceCohortGateOutcome.Passed &&
        ValidForLanding &&
        GateExitCode == 0 &&
        AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(GateTestResultPaths);
}

public static class AcceptanceCohortGateEvidence
{
    public static bool HasCoherentTrxEvidence(IReadOnlyList<string>? paths)
    {
        if (!HasNormalizedTestResultPaths(paths))
        {
            return false;
        }

        foreach (var path in paths!)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                if (!HasCoherentExecutedTrxEvidence(XDocument.Load(path, LoadOptions.None)))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasNormalizedTestResultPaths(IReadOnlyList<string>? paths)
    {
        if (paths is not { Count: > 0 } || paths.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        try
        {
            var normalized = paths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return paths.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasCoherentExecutedTrxEvidence(XDocument document)
    {
        XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var root = document.Root;
        if (root?.Name != trxNamespace + "TestRun")
        {
            return false;
        }

        var results = root.Element(trxNamespace + "Results");
        var unitResults = results?.Elements(trxNamespace + "UnitTestResult").ToArray() ?? [];
        var counters = root.Element(trxNamespace + "ResultSummary")?.Element(trxNamespace + "Counters");
        if (unitResults.Length == 0 || counters is null ||
            !TryReadNonNegativeCounter(counters, "total", out var total) ||
            !TryReadNonNegativeCounter(counters, "executed", out var executed) ||
            !TryReadNonNegativeCounter(counters, "passed", out var passed) ||
            !TryReadNonNegativeCounter(counters, "failed", out var failed))
        {
            return false;
        }

        var passedResults = unitResults.Count(result =>
            string.Equals((string?)result.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase));
        var failedResults = unitResults.Count(result =>
            string.Equals((string?)result.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase));
        return total > 0 &&
            executed > 0 &&
            executed <= total &&
            unitResults.Length == executed &&
            passedResults == passed &&
            failedResults == failed &&
            passed + failed == executed;
    }

    private static bool TryReadNonNegativeCounter(XElement counters, string name, out int value)
    {
        var raw = counters.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        return int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value) &&
            value >= 0;
    }
}
