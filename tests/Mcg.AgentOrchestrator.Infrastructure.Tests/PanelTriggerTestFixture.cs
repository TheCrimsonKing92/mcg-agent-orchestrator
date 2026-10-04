using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// All databases and files are isolated per fixture; no shared provider or process state is used.
internal sealed class PanelTriggerTestFixture : IDisposable
{
    internal PanelTestHarness Panel { get; } = new();
    internal string Events => Path.Combine(Panel.Root, "goal-events");
    internal string Author => Path.Combine(Panel.Root, "author-claims.db");
    internal string Cohort => Path.Combine(Panel.Root, "cohort-acceptance.db");
    internal string State => Path.Combine(Panel.Root, "state.db");
    internal string GoalId => Panel.Goal.Id.Value;
    internal string BaseSha => new('b', 40);
    internal int DiffCalls { get; private set; }
    internal string Criteria => "The recorded criterion must remain intact.";
    internal ConductorJudgePanelTriggerSources Sources => new(Events, Panel.ConductPath, Author, Cohort, State);
    internal ConductorJudgePanelPacketBuilder Packets => new(Panel.Root, (basis, candidate, paths) =>
    {
        DiffCalls++;
        Assert.Equal(BaseSha, basis);
        Assert.Equal(Panel.Candidate, candidate);
        return "diff --git a/src/Example.cs b/src/Example.cs\n@@ -1 +1 @@\n-old\n+new";
    });
    internal ConductorJudgePanelTriggerDetector Detector(ConductorJudgePanelCaseStore? store = null) =>
        new(store ?? Panel.Store, Sources, Packets);

    internal async Task SaveCriteria()
    {
        Panel.Kernel.RecordGoalRefinement(Panel.Goal.Id,
            new("Panel criteria", [Criteria], VerificationClass.TestVerifiable, [], []));
        await SaveState();
    }
    internal Task SaveState() => new SqliteOrchestratorStateRepository(State).SaveAsync(Panel.Kernel);
    internal string CriteriaVersion => EffectiveAcceptanceCriteriaVersion.ComputeForGoal(Panel.Goal);

    internal void Timeline(int cursor, string text, bool decision = false, string? source = null,
        string eventType = "GoalEscalated")
    {
        Directory.CreateDirectory(Events);
        File.AppendAllText(Path.Combine(Events, GoalId + ".jsonl"), JsonSerializer.Serialize(new
        {
            cursor, timestamp = Panel.Time.UtcNow, goalId = GoalId,
            eventType = decision ? "GoalLifecycleDecision" : eventType,
            progressKind = decision ? "GoalPolicyDecision" : null,
            reason = decision ? null : text, message = decision ? text : null, source
        }) + "\n");
    }

    internal void Conduct(string kind, string detail, bool rotated = false, string? goalId = null)
    {
        var path = rotated ? Path.Combine(Panel.Root, "conduct-events-20300101.log") : Panel.ConductPath;
        File.AppendAllText(path, JsonSerializer.Serialize(new
        { timestamp = Panel.Time.UtcNow, goalId = goalId ?? GoalId, eventKind = kind, detail }) + "\n");
    }

    internal string AuthorQuestion()
    {
        var item = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "owner-fork", GoalId,
            "May the operator amend the frozen fact in src/Example.cs?", "reversibility");
        var claims = new ConductorAuthorClaimStore(Author);
        Assert.True(claims.TryClaim(item, Panel.Time.UtcNow));
        claims.Complete(item.Identity, "owner-question");
        var question = new ConductorAuthorOwnerQuestion(GoalId, item.TargetKind, item.TargetId,
            item.Question, "ask owner", "owner authority required");
        Conduct("goal-escalation", question.Format());
        Conduct("author", "item=" + item.Identity + " kind=ask-owner");
        Timeline(90, "author-owner-question " + item.Question, source: "author-owner-question");
        return "author-claim:" + item.Identity;
    }

    internal void CohortFailure(string receiptId, string trx)
    {
        _ = new CohortAcceptanceStore(Cohort); // Arrange the production schema before read-only inspection.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Cohort, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cohort_receipts(cohort_id, receipt_id, main_revision, combined_tree_revision,
                manifest_identity, outcome, attribution, valid_for_landing, completed_at, gate_elapsed_ms,
                failed_checks_json, gate_test_result_paths_json, attribution_source)
            VALUES ('cohort', $receipt, $base, 'tree', 'manifest', 'Failed', 'BothMembersFailed', 0,
                $time, 0, '[]', $paths, 'main-suspect');
            INSERT INTO cohort_members(cohort_id, member_ordinal, goal_id, branch_revision, candidate_revision,
                landing_paths_json, resource_keys_json, risk_tier, promotion_disposition, merge_status, merge_reason)
            VALUES ('cohort', 0, $goal, $candidate, $candidate, '[]', '[]', 'Low', 'Eligible', 'pending', 'fixture');
            INSERT INTO cohort_members(cohort_id, member_ordinal, goal_id, branch_revision, candidate_revision,
                landing_paths_json, resource_keys_json, risk_tier, promotion_disposition, merge_status, merge_reason)
            VALUES ('cohort', 1, 'cccccccccccccccccccccccccccccccc', $candidate, $candidate,
                '[]', '[]', 'Low', 'Eligible', 'pending', 'fixture');
            """;
        command.Parameters.AddWithValue("$receipt", receiptId);
        command.Parameters.AddWithValue("$base", BaseSha);
        command.Parameters.AddWithValue("$time", Panel.Time.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(new[] { trx }));
        command.Parameters.AddWithValue("$goal", GoalId);
        command.Parameters.AddWithValue("$candidate", Panel.Candidate);
        command.ExecuteNonQuery();
    }

    internal string Trx(string message = "Assert.Equal() Failure: expected six disputes", string? stack = null)
    {
        var path = Path.Combine(Panel.Root, Guid.NewGuid().ToString("N") + ".trx");
        new XDocument(new XElement("TestRun", new XElement("Results",
            new XElement("UnitTestResult", new XAttribute("testName", "ExampleTests.Dispute"),
                new XAttribute("outcome", "Failed"), new XElement("Output", new XElement("ErrorInfo",
                    new XElement("Message", message), new XElement("StackTrace", stack ??
                        "at ExampleTests.Dispute() in tests/ExampleTests.cs:line 12\nat LaterFrame() in src/Later.cs:line 3")))))))
            .Save(path);
        File.SetLastWriteTimeUtc(path, Panel.Time.UtcNow.AddSeconds(-1).UtcDateTime);
        return path;
    }

    internal string BoundText(string reason, string trx) =>
        $"{reason}; candidate_sha={Panel.Candidate}; base_sha={BaseSha}; src/Example.cs; pointer=\"{trx}\"";

    internal Dictionary<string, string> ProducerHashes() => Directory.EnumerateFiles(Panel.Root, "*", SearchOption.AllDirectories)
        .Where(path => path != Path.Combine(Panel.Root, "panel.db"))
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    public void Dispose() => Panel.Dispose();
}
