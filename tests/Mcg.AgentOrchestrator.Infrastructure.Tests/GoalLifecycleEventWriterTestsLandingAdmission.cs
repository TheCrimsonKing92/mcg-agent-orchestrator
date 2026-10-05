using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class GoalLifecycleEventWriterTestsLandingAdmission
{
    [Fact]
    public void WritesAdmissionFieldsAlongsideExistingBranches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-admission", Guid.NewGuid().ToString("N"));
        try
        {
            var id = GoalId.New();
            var writer = new GoalLifecycleEventWriter(directory);
            var receipt = LandingAdmissionReceipt.Evaluated(new string('a', 40), "main:" + new string('b', 40), ["git-mutation=src/path.cs"]);
            writer.AppendGoalLanded(id, "integration", "goal/example", receipt);
            using var document = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Path.Combine(directory, $"{id.Value}.jsonl"))));
            var line = document.RootElement;
            Assert.Equal("GoalLanded", line.GetProperty("eventType").GetString());
            Assert.Equal("integration", line.GetProperty("integrationBranch").GetString());
            Assert.Equal("goal/example", line.GetProperty("goalBranch").GetString());
            Assert.Equal("denylist-match-recorded", line.GetProperty("admissionRule").GetString());
            Assert.Equal(receipt.CandidateSha, line.GetProperty("admissionCandidateSha").GetString());
            Assert.Equal(receipt.DenylistSource, line.GetProperty("landingDenylistSource").GetString());
            Assert.Equal(receipt.DenylistMatches, line.GetProperty("landingDenylistMatches").EnumerateArray().Select(value => value.GetString()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DefaultInterfaceOverloadForwardsToLegacyFake()
    {
        var fake = new LegacyWriter();
        IGoalLifecycleEventWriter writer = fake;
        var id = GoalId.New();
        writer.AppendGoalLanded(id, "integration", "goal/example", LandingAdmissionReceipt.Recovered("candidate"));
        Assert.Equal((id, "integration", "goal/example"), fake.Landed);
    }
}

internal sealed class LegacyWriter : IGoalLifecycleEventWriter
{
    public (GoalId, string, string)? Landed { get; private set; }

    public void AppendTimelineEvent(ProgressEvent progressEvent) { }
    public void AppendGoalCreated(GoalId goalId, string objective) { }
    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
    public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> staleTopicKeys, string recoveryCommand) { }
    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
    public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string capturedAcceptanceCriteriaHash) { }
    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) => Landed = (goalId, integrationBranch, goalBranch);
    public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) { }
    public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) { }
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, GoalStatus status, string reason, string source) { }
    public void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) { }
    public void AppendCleanedUp(GoalId goalId) { }
    public void AppendProgressiveReviewGlanceReceipt(
        GoalId goalId,
        TaskId taskId,
        string trigger,
        string inputsHash,
        string verdict,
        string note,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        TimeSpan wallTime,
        string? model,
        string? profile) { }
    public void AppendProgressiveReviewGlanceGuardReceipt(
        GoalId goalId,
        TaskId taskId,
        ProgressiveReviewGlanceGuardReceipt receipt) { }
    public void AppendProgressiveReviewGlanceCircuitReceipt(
        GoalId goalId,
        TaskId taskId,
        ProgressiveReviewGlanceCircuitReceipt receipt) { }
    public void AppendProgressiveReviewGlanceSummary(
        GoalId goalId,
        int totalGlances,
        int onTrack,
        int concern,
        int fundamentalMisdirection,
        int invalid,
        int totalTokens) { }
}
