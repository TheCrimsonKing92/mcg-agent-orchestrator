using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalOperationJournalGatePassedEvidenceTests : IDisposable
{
    private const string BranchRevision = "1111111111111111111111111111111111111111";
    private const string MainRevision = "2222222222222222222222222222222222222222";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mcg-gate-passed-evidence", Guid.NewGuid().ToString("N"));
    private readonly Goal _goal = new AgentOrchestratorKernel().CreateGoal("Gate-passed evidence");

    public GoalOperationJournalGatePassedEvidenceTests() => Directory.CreateDirectory(_root);

    [Xunit.Fact]
    public void GatePassedOnly_HasNoLandingOrRecordEvidence()
    {
        WriteGatePassed();

        var journal = GoalOperationJournal.Read(_root, _goal.Id);

        var entry = Assert.Single(journal.LatestByOperation);
        Assert.Equal(GoalOperationStatus.Completed, entry.Status);
        Assert.Equal("gate-passed", entry.AcceptanceOutcome);
        Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.False(GoalOperationJournal.HasCompletedRecordEvidence(journal));
    }

    [Xunit.Fact]
    public void GatePassedThenLandingAndRecording_IndependentEvidenceStillCounts()
    {
        WriteGatePassed();
        GoalOperationJournal.Completed(_root, _goal, "conductor:land", "Landed");

        var landed = GoalOperationJournal.Read(_root, _goal.Id);
        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(landed));
        Assert.False(GoalOperationJournal.HasCompletedRecordEvidence(landed));

        GoalOperationJournal.Completed(_root, _goal, "conductor:record", "Recorded");

        var recorded = GoalOperationJournal.Read(_root, _goal.Id);
        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(recorded));
        Assert.True(GoalOperationJournal.HasCompletedRecordEvidence(recorded));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void CompletedAcceptance_PassedOrMissingOutcomeStillCounts(bool hasOutcome)
    {
        if (hasOutcome)
        {
            GoalOperationJournal.AcceptancePassed(
                _root, _goal, "acceptance", BranchRevision, MainRevision, "Merged");
        }
        else
        {
            GoalOperationJournal.Completed(_root, _goal, "acceptance", "Legacy completion");
        }

        var journal = GoalOperationJournal.Read(_root, _goal.Id);
        Assert.Equal(hasOutcome ? "passed" : null, Assert.Single(journal.Entries).AcceptanceOutcome);
        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.True(GoalOperationJournal.HasCompletedRecordEvidence(journal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("gate-passed", false)]
    [Xunit.InlineData("GATE-PASSED", false)]
    [Xunit.InlineData("GaTe-PaSsEd", false)]
    [Xunit.InlineData("passed", true)]
    [Xunit.InlineData(null, true)]
    [Xunit.InlineData("", true)]
    [Xunit.InlineData(" ", true)]
    [Xunit.InlineData("other", true)]
    [Xunit.InlineData(" gate-passed ", true)]
    public void AcceptanceOutcome_OnlyExactGatePassedIgnoringCaseIsExcluded(
        string? outcome, bool expectedEvidence)
    {
        var entry = new GoalOperationJournalEntry(
            "acceptance", _goal.Id, "acceptance", GoalOperationStatus.Completed,
            DateTimeOffset.UnixEpoch, "Acceptance outcome", AcceptanceOutcome: outcome);
        var journal = new GoalOperationJournalSummary("unused", [entry], [entry], []);

        Assert.Equal(expectedEvidence, GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.Equal(expectedEvidence, GoalOperationJournal.HasCompletedRecordEvidence(journal));
    }

    [Xunit.Fact]
    public void LatestSameCandidateGatePassed_DoesNotReuseEarlierPassedEvidence()
    {
        GoalOperationJournal.AcceptancePassed(
            _root, _goal, "acceptance", BranchRevision, MainRevision, "Earlier completion");
        WriteGatePassed();

        var journal = GoalOperationJournal.Read(_root, _goal.Id);

        Assert.Equal(2, journal.Entries.Count);
        Assert.Equal("gate-passed", Assert.Single(journal.LatestByOperation).AcceptanceOutcome);
        Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.False(GoalOperationJournal.HasCompletedRecordEvidence(journal));
    }

    [Xunit.Fact]
    public void GatePassedWithRetiredDisposition_StillCountsAsLandingAndRecordEvidence()
    {
        WriteGatePassed();
        GoalOperationJournal.RecordTerminalDisposition(
            _root, _goal, new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired"));

        var journal = GoalOperationJournal.Read(_root, _goal.Id);

        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(journal));
        Assert.True(GoalOperationJournal.HasCompletedRecordEvidence(journal));
    }

    private void WriteGatePassed() => GoalOperationJournal.AcceptanceGatePassed(
        _root, _goal, "acceptance", BranchRevision, MainRevision, "Gate passed; merge pending");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
