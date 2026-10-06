using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

public sealed class ConductorJudgePanelStoreCursorTests
{
    [Fact]
    public async Task UnchangedStoresDoNotScanAndNewClaimsAndReceiptsMatchFullRead()
    {
        using var h = new PanelIncrementalTestFixture();
        var f = h.Fixture;
        await f.SaveCriteria();
        var claims = new ConductorAuthorClaimStore(f.Author);
        var old = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "older", f.GoalId, "Older question", "reason");
        Assert.True(claims.TryClaim(old, f.Panel.Time.UtcNow));
        _ = new Mcg.AgentOrchestrator.Infrastructure.CohortAcceptanceStore(f.Cohort);
        h.Tick();
        Assert.Empty(f.Panel.Store.Cases());
        Assert.Equal(2, h.Reads.StoreScans);
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.StoreScans);
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);

        var authorId = f.AuthorQuestion();
        f.CohortFailure("new-receipt", f.Trx());
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(2, h.Reads.StoreScans);
        var cases = f.Panel.Store.Cases();
        Assert.Equal(2, cases.Count);
        var author = Assert.Single(cases, item => item.Key.TriggerId == authorId);
        var cohort = Assert.Single(cases, item => item.Key.TriggerId == "cohort-receipt:new-receipt");
        Assert.Equal(h.FullReadKey(authorId), author.Key);
        Assert.Equal(h.FullReadKey(cohort.Key.TriggerId), cohort.Key);
        h.Relaunch();
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.StoreScans);
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(0, h.Reads.GoalReads);
        Assert.Equal(cases, f.Panel.Store.Cases());

        // Completing an earlier row after a later claim was consumed must not be missed.
        claims.Complete(old.Identity, "owner-question");
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.StoreScans);
        Assert.Equal(3, f.Panel.Store.Cases().Count);
        var late = Assert.Single(f.Panel.Store.Cases(), item => item.Key.TriggerId == "author-claim:" + old.Identity);
        Assert.Equal(h.FullReadKey(late.Key.TriggerId), late.Key);
    }

    [Fact]
    public async Task RetainedEscalationPreservesLaterAuthorPacket()
    {
        using var h = new PanelIncrementalTestFixture();
        var f = h.Fixture;
        await f.SaveCriteria();
        var item = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "late-owner", f.GoalId, "Original question", "reason");
        var claims = new ConductorAuthorClaimStore(f.Author);
        Assert.True(claims.TryClaim(item, f.Panel.Time.UtcNow));
        var escalation = new ConductorAuthorOwnerQuestion(f.GoalId, item.TargetKind, item.TargetId,
            item.Question, "ask owner", "owner authority required");
        f.Conduct("goal-escalation", escalation.Format());
        h.Tick();
        Assert.Empty(f.Panel.Store.Cases());
        h.Relaunch();
        claims.Complete(item.Identity, "owner-question");
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(0, h.Reads.EventLines);
        Assert.Equal(1, h.Reads.StoreScans);
        var added = Assert.Single(f.Panel.Store.Cases());
        Assert.Equal(h.FullReadKey(added.Key.TriggerId), added.Key);
        Assert.Contains(escalation.Format(), added.Key.Packet);
    }

    [Fact]
    public async Task PreviouslyIneligibleReceiptIsDiscoveredAfterUpdate()
    {
        using var h = new PanelIncrementalTestFixture();
        var f = h.Fixture;
        await f.SaveCriteria();
        f.CohortFailure("late-receipt", f.Trx());
        SetAttribution(f.Cohort, "CandidateFailed", "candidate");
        h.Tick();
        Assert.Empty(f.Panel.Store.Cases());
        h.Relaunch();
        SetAttribution(f.Cohort, "BothMembersFailed", "main-suspect");
        h.Reads.Reset();
        h.Tick();
        Assert.Equal(1, h.Reads.StoreScans);
        var added = Assert.Single(f.Panel.Store.Cases());
        Assert.Equal("cohort-receipt:late-receipt", added.Key.TriggerId);
        Assert.Equal(h.FullReadKey(added.Key.TriggerId), added.Key);
    }

    private static void SetAttribution(string path, string attribution, string source)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cohort_receipts SET attribution = $attribution, attribution_source = $source";
        command.Parameters.AddWithValue("$attribution", attribution);
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
    }
}
