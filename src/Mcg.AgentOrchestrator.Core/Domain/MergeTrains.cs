using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public sealed record MergeTrainMemberBinding(
    GoalId GoalId,
    string BranchRevision,
    string CandidateRevision,
    IReadOnlyList<string> LandingPaths,
    IReadOnlyList<string> ResourceKeys,
    ChangeRiskTier ChangeRiskTier,
    ConductorTransitionDecision AutoPromotionDisposition,
    string MergeStatus,
    string MergeReason,
    string? RebasedTrainHeadRevision = null)
{
    public MergeTrainMemberBinding WithRebasedHead(string revision) =>
        this with { RebasedTrainHeadRevision = NormalizeRevision(revision, nameof(revision)) };

    public static string NormalizeRevision(string value, string parameterName) =>
        AcceptanceCohortMemberBinding.NormalizeRevision(value, parameterName);
}

public sealed class MergeTrainIdentity
{
    public const string Version = "merge-train-v1";

    private MergeTrainIdentity(
        IReadOnlyList<MergeTrainMemberBinding> members,
        string observedMainRevision,
        string trainTreeRevision,
        string manifestIdentity,
        string value)
    {
        Members = Array.AsReadOnly(members.ToArray());
        ObservedMainRevision = observedMainRevision;
        TrainTreeRevision = trainTreeRevision;
        ManifestIdentity = manifestIdentity;
        Value = value;
    }

    public IReadOnlyList<MergeTrainMemberBinding> Members { get; }
    public string ObservedMainRevision { get; }
    public string TrainTreeRevision { get; }
    public string ManifestIdentity { get; }
    public string Value { get; }

    public static MergeTrainIdentity Create(
        IReadOnlyList<MergeTrainMemberBinding> members,
        string observedMainRevision,
        string trainTreeRevision,
        string manifestIdentity)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 2 or > 3 || members.Select(member => member.GoalId).Distinct().Count() != members.Count)
        {
            throw new ArgumentException("A merge train requires two or three distinct ordered members.", nameof(members));
        }
        if (members.Any(member => string.IsNullOrWhiteSpace(member.RebasedTrainHeadRevision)))
        {
            throw new ArgumentException("Every merge train member requires rebased-head provenance.", nameof(members));
        }

        var main = MergeTrainMemberBinding.NormalizeRevision(observedMainRevision, nameof(observedMainRevision));
        var tree = MergeTrainMemberBinding.NormalizeRevision(trainTreeRevision, nameof(trainTreeRevision));
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestIdentity);
        using var stream = new MemoryStream();
        foreach (var field in new[] { Version, main, tree, manifestIdentity.Trim() }
                     .Concat(members.SelectMany(member => new[]
                     {
                         member.GoalId.Value,
                         member.BranchRevision,
                         member.CandidateRevision,
                         member.RebasedTrainHeadRevision!
                     })))
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        return new MergeTrainIdentity(members, main, tree, manifestIdentity.Trim(), $"{Version}-{digest}");
    }
}

public enum MergeTrainGateOutcome
{
    Passed,
    Failed,
    InfrastructureFailure,
    Invalidated
}

public enum MergeTrainEjectionReason
{
    RebaseConflict,
    StaleBinding,
    MaterializationFailure,
    RedNewestMember
}

public sealed record MergeTrainEjection(
    GoalId GoalId,
    MergeTrainEjectionReason Reason,
    IReadOnlyList<string> ConflictPaths,
    string Detail);

public sealed record MergeTrainReceipt(
    string ReceiptId,
    MergeTrainIdentity Identity,
    MergeTrainGateOutcome Outcome,
    DateTimeOffset CompletedAt,
    long GateElapsedMilliseconds,
    IReadOnlyList<string> FailedChecks,
    int? GateExitCode,
    IReadOnlyList<string> GateTestResultPaths,
    bool ValidForLanding = false)
{
    public IReadOnlyList<AcceptanceCohortEvidenceArtifact> GateEvidenceArtifacts { get; init; } = [];

    public bool HasAuthoritativeLandingEvidence =>
        Outcome == MergeTrainGateOutcome.Passed &&
        ValidForLanding &&
        GateExitCode == 0 &&
        AcceptanceCohortGateEvidence.HasContentBoundEvidence(GateTestResultPaths, GateEvidenceArtifacts);
}
