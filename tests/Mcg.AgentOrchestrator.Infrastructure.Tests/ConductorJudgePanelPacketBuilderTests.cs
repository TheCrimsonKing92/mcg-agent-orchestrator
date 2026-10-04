using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorJudgePanelPacketBuilderTests
{
    [Fact]
    public async Task Packet_contains_trigger_criteria_and_trx_without_later_answers_or_landing()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var version = f.CriteriaVersion;
        var trx = f.Trx("DECISION_TIME_ASSERTION");
        var text = f.BoundText("PRE_TESTER_RED_LOOP: DECISION_TIME_ESCALATION", trx);
        f.Timeline(0, text);
        var triggerTime = f.Panel.Time.UtcNow;
        f.Panel.Time.UtcNow += TimeSpan.FromMinutes(1);
        f.Timeline(1, "LATER_OPERATOR_ANSWER", eventType: "HumanInputReceived");
        f.Timeline(2, "LATER_LANDING_OUTCOME", eventType: "GoalLanded");
        f.Timeline(3, "Operator answered PRE_TESTER_RED_LOOP: LATER_QUOTED_ANSWER", decision: true);
        f.Panel.Kernel.RecordGoalRefinement(f.Panel.Goal.Id,
            new("Later spec", ["LATER_REPLACEMENT_CRITERION"], VerificationClass.TestVerifiable, [], []));
        await f.SaveState();
        var before = f.ProducerHashes();
        var trigger = Assert.Single(f.Sources.Read());
        Assert.Equal(triggerTime, trigger.RecordedAt);
        var snapshot = ConductorJudgePanelCriteriaAtTrigger.Resolve(f.Sources.ReadGoal(f.GoalId), trigger.RecordedAt);
        Assert.Equal(version, snapshot.Version);
        Assert.Equal("as-recorded", snapshot.Provenance);
        Assert.Equal(new[] { f.Criteria }, snapshot.Criteria);
        IReadOnlyList<string>? diffPaths = null;
        var builder = new ConductorJudgePanelPacketBuilder(f.Panel.Root, (basis, candidate, paths) =>
        {
            Assert.Equal(f.BaseSha, basis);
            Assert.Equal(f.Panel.Candidate, candidate);
            diffPaths = paths;
            return "@@ -1 +1 @@\n-before\n+DECISION_TIME_DIFF";
        });
        var packet = builder.Build(trigger, snapshot);
        Assert.Contains(f.Criteria, packet.Text);
        Assert.Contains("criteria_provenance = \"as-recorded\"", packet.Text);
        Assert.Contains(text, packet.Text);
        Assert.Contains("ExampleTests.Dispute", packet.Text);
        Assert.Contains("DECISION_TIME_ASSERTION", packet.Text);
        Assert.Contains("at ExampleTests.Dispute() in tests/ExampleTests.cs:line 12", packet.Text);
        Assert.Contains("DECISION_TIME_DIFF", packet.Text);
        Assert.Equal(new[] { "src/Example.cs", "tests/ExampleTests.cs" }, diffPaths);
        Assert.DoesNotContain("LaterFrame", packet.Text);
        Assert.DoesNotContain("LATER_OPERATOR_ANSWER", packet.Text);
        Assert.DoesNotContain("LATER_LANDING_OUTCOME", packet.Text);
        Assert.DoesNotContain("LATER_REPLACEMENT_CRITERION", packet.Text);
        Assert.DoesNotContain("LATER_QUOTED_ANSWER", packet.Text);
        Assert.Contains("LATER_OPERATOR_ANSWER", File.ReadAllText(Path.Combine(f.Events, f.GoalId + ".jsonl")));
        Assert.Contains("LATER_LANDING_OUTCOME", File.ReadAllText(Path.Combine(f.Events, f.GoalId + ".jsonl")));
        Assert.Contains(ConductorJudgePanelPacketBuilder.AuthorityBoundary, packet.Text);
        Assert.Empty(packet.Truncations);
        Assert.Equal(before.OrderBy(pair => pair.Key), f.ProducerHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Oversized_evidence_is_capped_with_protected_sections_and_truncation()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var trx = f.Trx(new string('m', 30_000));
        f.Timeline(0, f.BoundText("PRE_TESTER_RED_LOOP: escalation remains", trx));
        var trigger = Assert.Single(f.Sources.Read());
        var criteria = ConductorJudgePanelCriteriaAtTrigger.Resolve(f.Sources.ReadGoal(f.GoalId), trigger.RecordedAt);
        var builder = new ConductorJudgePanelPacketBuilder(f.Panel.Root,
            (_, _, _) => "@@ -1 +1 @@\n+" + new string('d', 40_000));
        var packet = builder.Build(trigger, criteria);
        Assert.True(packet.Text.Length <= ConductorJudgePanelPacketBuilder.MaxPacketCharacters);
        Assert.Contains(string.Join("\n", criteria.Criteria), packet.Text);
        Assert.Contains(ConductorJudgePanelPacketBuilder.AuthorityBoundary, packet.Text);
        Assert.Contains("[truncated]", packet.Text);
        Assert.NotEmpty(packet.Truncations);
        Assert.Equal("Diff hunk", packet.Truncations[0].Section);
        Assert.Contains(packet.Truncations, item => item.Section == "TRX Message");
        Assert.All(packet.Truncations, item => Assert.True(item.CharactersDropped > 0));
        Assert.Contains("PRE_TESTER_RED_LOOP: escalation remains", packet.Text);
        Assert.Contains(new string('m', 20), packet.Text);
    }

    [Theory]
    [InlineData((int)PanelTriggerKind.TesterInconclusiveUnchanged)]
    [InlineData((int)PanelTriggerKind.PreReviewEvidence)]
    [InlineData((int)PanelTriggerKind.PreTesterRedLoop)]
    [InlineData((int)PanelTriggerKind.ApparatusRed)]
    [InlineData((int)PanelTriggerKind.AuthorAskOwner)]
    [InlineData((int)PanelTriggerKind.CohortBothFailed)]
    public void Every_kind_contains_authority_verbatim(int kind)
    {
        using var f = new PanelTriggerTestFixture();
        var trigger = new PanelTrigger((PanelTriggerKind)kind, f.GoalId, PanelTrigger.UnrecordedSha,
            PanelTrigger.UnrecordedSha, "fixture", f.Panel.Time.UtcNow, "Question", []);
        var packet = f.Packets.Build(trigger, new("v1", [f.Criteria]));
        Assert.Contains(ConductorJudgePanelPacketBuilder.AuthorityBoundary, packet.Text);
        Assert.True(packet.Text.Length <= ConductorJudgePanelPacketBuilder.MaxPacketCharacters);
    }

    [Fact]
    public async Task Later_rewritten_receipt_and_later_correction_are_excluded()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var trx = f.Trx("LATER_REWRITTEN_MESSAGE");
        f.Timeline(0, f.BoundText("PRE_TESTER_RED_LOOP: original decision", trx));
        var at = f.Panel.Time.UtcNow;
        File.SetLastWriteTimeUtc(trx, at.AddMinutes(1).UtcDateTime);
        var snapshot = f.Sources.ReadGoal(f.GoalId)! with
        {
            EffectiveAcceptanceCriteriaCorrections =
            [new(f.Criteria, "LATER_CORRECTION", "operator", at.AddMinutes(1), null, ProgressKind.OperatorTaskNote)]
        };
        await new SqliteOrchestratorStateRepository(f.State).SaveGoalSnapshotsAsync([snapshot]);
        var trigger = Assert.Single(f.Sources.Read());
        var criteria = ConductorJudgePanelCriteriaAtTrigger.Resolve(f.Sources.ReadGoal(f.GoalId), at);
        var packet = f.Packets.Build(trigger, criteria);
        Assert.Contains(f.Criteria, packet.Text);
        Assert.Contains("receipt unavailable at trigger", packet.Text);
        Assert.DoesNotContain("LATER_REWRITTEN_MESSAGE", packet.Text);
        Assert.DoesNotContain("LATER_CORRECTION", packet.Text);
    }

    [Fact]
    public async Task Referenced_json_receipt_supplies_trx_message_only()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var trx = f.Trx("REFERENCED_TRX_MESSAGE");
        var receipt = Path.Combine(f.Panel.Root, "focused-result.json");
        File.WriteAllText(receipt, JsonSerializer.Serialize(new
        { Checks = new[] { new { TestResultPaths = new[] { trx } } }, OperatorAnswer = "UNRELATED_PAYLOAD" }));
        File.SetLastWriteTimeUtc(receipt, f.Panel.Time.UtcNow.AddSeconds(-1).UtcDateTime);
        f.Timeline(0, f.BoundText("PRE_TESTER_RED_LOOP: original decision", receipt));
        var trigger = Assert.Single(f.Sources.Read());
        var criteria = ConductorJudgePanelCriteriaAtTrigger.Resolve(f.Sources.ReadGoal(f.GoalId), trigger.RecordedAt);
        var packet = f.Packets.Build(trigger, criteria);
        Assert.Contains("REFERENCED_TRX_MESSAGE", packet.Text);
        Assert.DoesNotContain("UNRELATED_PAYLOAD", packet.Text);
    }

    [Fact]
    public async Task In_place_rescoped_version_uses_retained_criteria_and_records_provenance()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        f.Timeline(0, "PRE_TESTER_RED_LOOP: historical dispute");
        var snapshot = f.Sources.ReadGoal(f.GoalId)!;
        var version = Assert.Single(snapshot.RefinedSpecVersions!);
        const string retainedCriterion = "  RETAINED_IN_PLACE_CRITERION  ";
        var rescoped = version with
        {
            Spec = version.Spec with
            {
                AcceptanceCriteria = [retainedCriterion],
                Decisions = [new("Feasibility", "Re-scope",
                    "Feasibility disposition applied and re-checked (topic: fixture).")]
            }
        };
        // Plain persisted-shape fixture: no call into the re-scope producer or timeline fallback.
        await new SqliteOrchestratorStateRepository(f.State).SaveGoalSnapshotsAsync(
        [snapshot with
        {
            RefinedSpecVersions = [rescoped],
            EffectiveAcceptanceCriteriaCorrections =
            [new(retainedCriterion, "SEPARATE_CORRECTION", "operator", version.RecordedAt,
                null, ProgressKind.OperatorTaskNote)]
        }]);
        var before = f.ProducerHashes();
        var trigger = Assert.Single(f.Sources.Read());
        var criteria = ConductorJudgePanelCriteriaAtTrigger.Resolve(f.Sources.ReadGoal(f.GoalId), trigger.RecordedAt);
        var packet = f.Packets.Build(trigger, criteria);
        Assert.Equal(new[] { retainedCriterion }, criteria.Criteria);
        Assert.Equal("criteria-rescoped-in-place", criteria.Provenance);
        Assert.Contains("criteria_provenance = \"criteria-rescoped-in-place\"", packet.Text);
        Assert.Contains(retainedCriterion, packet.Text);
        Assert.DoesNotContain(f.Criteria, packet.Text);
        Assert.DoesNotContain("SEPARATE_CORRECTION", packet.Text);
        Assert.Contains(ConductorJudgePanelPacketBuilder.AuthorityBoundary, packet.Text);
        Assert.Equal(before.OrderBy(pair => pair.Key), f.ProducerHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public void Oversized_protected_text_fails_loudly_instead_of_exceeding_cap()
    {
        using var f = new PanelTriggerTestFixture();
        var trigger = new PanelTrigger(PanelTriggerKind.AuthorAskOwner, f.GoalId,
            PanelTrigger.UnrecordedSha, PanelTrigger.UnrecordedSha, "owner", f.Panel.Time.UtcNow, "Question", []);
        var exception = Assert.Throws<InvalidDataException>(() => f.Packets.Build(trigger,
            new("v1", [new string('c', ConductorJudgePanelPacketBuilder.MaxPacketCharacters)])));
        Assert.Contains("protected criteria and authority sections exceed MaxPacketCharacters", exception.Message);
    }
}
