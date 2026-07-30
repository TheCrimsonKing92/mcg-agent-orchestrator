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
    public bool Matches(string goalId, int reviewerRound, string candidateSha, IReadOnlyList<string> selectedFocusedTests) =>
        string.Equals(GoalId, goalId, StringComparison.Ordinal) &&
        ReviewerRound == reviewerRound &&
        string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
        SelectedFocusedTests.SequenceEqual(selectedFocusedTests, StringComparer.Ordinal);
}
