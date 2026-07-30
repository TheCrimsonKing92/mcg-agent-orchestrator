using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

[JsonConverter(typeof(JsonStringEnumConverter<PreReviewEvidenceDisposition>))]
public enum PreReviewEvidenceDisposition
{
    Green,
    Red,
    NoApplicableTests,
    MappingNeedsInput
}

public sealed record PreReviewEvidenceCheckReceipt(
    string Name,
    string Command,
    bool Passed,
    int? ExitCode,
    string? ArtifactPath = null,
    IReadOnlyList<string>? TestResultPaths = null);

public sealed record PreReviewEvidenceReceipt(
    string GoalId,
    int ReviewerRound,
    string CandidateSha,
    IReadOnlyList<string> SelectedFocusedTests,
    PreReviewEvidenceDisposition Disposition,
    int PassedCheckCount,
    int FailedCheckCount,
    IReadOnlyList<PreReviewEvidenceCheckReceipt> Checks,
    IReadOnlyList<string> FailingTestIdentities,
    string MappingReason,
    string? EvidencePointer,
    DateTimeOffset RecordedAt)
{
    public bool MatchesCurrentCandidate(
        string goalId,
        string candidateSha,
        IReadOnlyList<string> selectedFocusedTests) =>
        string.Equals(GoalId, goalId, StringComparison.Ordinal) &&
        string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
        SelectedFocusedTests.SequenceEqual(selectedFocusedTests, StringComparer.Ordinal);

    public bool ContentEquals(PreReviewEvidenceReceipt other) =>
        string.Equals(GoalId, other.GoalId, StringComparison.Ordinal) &&
        ReviewerRound == other.ReviewerRound &&
        string.Equals(CandidateSha, other.CandidateSha, StringComparison.OrdinalIgnoreCase) &&
        SelectedFocusedTests.SequenceEqual(other.SelectedFocusedTests, StringComparer.Ordinal) &&
        Disposition == other.Disposition &&
        PassedCheckCount == other.PassedCheckCount &&
        FailedCheckCount == other.FailedCheckCount &&
        Checks.Count == other.Checks.Count &&
        Checks.Zip(other.Checks).All(pair => CheckContentEquals(pair.First, pair.Second)) &&
        FailingTestIdentities.SequenceEqual(other.FailingTestIdentities, StringComparer.Ordinal) &&
        string.Equals(MappingReason, other.MappingReason, StringComparison.Ordinal) &&
        string.Equals(EvidencePointer, other.EvidencePointer, StringComparison.OrdinalIgnoreCase);

    private static bool CheckContentEquals(
        PreReviewEvidenceCheckReceipt left,
        PreReviewEvidenceCheckReceipt right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Command, right.Command, StringComparison.Ordinal) &&
        left.Passed == right.Passed &&
        left.ExitCode == right.ExitCode &&
        string.Equals(left.ArtifactPath, right.ArtifactPath, StringComparison.OrdinalIgnoreCase) &&
        (left.TestResultPaths ?? []).SequenceEqual(
            right.TestResultPaths ?? [],
            StringComparer.OrdinalIgnoreCase);
}
