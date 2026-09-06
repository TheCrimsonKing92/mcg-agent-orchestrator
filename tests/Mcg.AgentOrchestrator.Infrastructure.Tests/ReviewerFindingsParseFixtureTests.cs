using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

using static ConductorDriverTests;

public sealed class ReviewerFindingsParseFixtureTests
{
    [Xunit.Theory]
    [Xunit.InlineData(
        "b2f52d39-0d99c73d-20260905153941.out.txt",
        "8AC1151066CDE3EA5D5B2BD1CAA9271838126E7B89D49392CB293E0F14F7B977")]
    [Xunit.InlineData(
        "ae54b5eb-e9ea0560-20260905170025.out.txt",
        "57085A40E6DA99AFECC7B22C04E01798408A3DEE5C5A22997710A46DF0AA08D6")]
    public void RetainedReviewerStdoutParsesOrRecordsTheRejectedFragment(
        string fileName,
        string expectedSourceSha256)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ReviewerFindingsParse", fileName);
        var stdout = File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n');
        Xunit.Assert.Equal(
            expectedSourceSha256,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stdout))));

        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            DateTimeOffset.Parse("2026-09-05T18:05:00Z"),
            StandardOutputPath: path,
            WorkerResultPresent: true);

        if (WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out _))
        {
            Xunit.Assert.Contains(round.Findings, finding =>
                finding.State == ReviewFindingState.Open && finding.Severity == FindingSeverity.Blocking);
            return;
        }

        Xunit.Assert.False(WorkerResultBlockers.TryFindReviewFindingRound(
            verification,
            out _,
            out var diagnostic));
        Xunit.Assert.True(WorkerResultBlockers.TryReadReviewFindingFragments(
            verification,
            out var findingsFragment,
            out _));
        var (kernel, goal) = SoftwareGoal("Retained Reviewer stdout diagnostic fixture");
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        DispatchTask(kernel, goal, reviewer, "review");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);

        var note = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Rejected Reviewer structured finding result", StringComparison.Ordinal)));
        Xunit.Assert.Contains(diagnostic, note.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(findingsFragment![..Math.Min(80, findingsFragment.Length)], note.Message, StringComparison.Ordinal);
    }
}
