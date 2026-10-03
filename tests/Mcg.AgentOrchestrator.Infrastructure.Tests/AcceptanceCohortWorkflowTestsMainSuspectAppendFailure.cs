using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsMainSuspectAppendFailure
{
    [Fact]
    public async Task CanaryAppendFailure_PropagatesWithoutSavingAttributionOrEmergencyHold()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        using (var events = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = scenario.Workspace.RunEventStorePath, Pooling = false }.ToString()))
        {
            events.Open();
            using var command = events.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_main_suspect BEFORE INSERT ON run_events
                WHEN instr(NEW.payload_json, '"failureReason":"main-suspect"') > 0
                BEGIN
                    SELECT RAISE(ABORT, 'controlled main-suspect append failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var failure = Assert.Throws<SqliteException>(() => scenario.Run());

        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Contains("controlled main-suspect append failure", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, scenario.RunCount);
        using (var cohorts = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(scenario.Workspace.OrchestratorDirectory, "cohort-acceptance.db"),
            Pooling = false }.ToString()))
        {
            cohorts.Open();
            using var command = cohorts.CreateCommand();
            command.CommandText = "SELECT cohort_id FROM cohort_receipts;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var cohortId = reader.GetString(0);
            Assert.False(reader.Read());
            var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Store.TryReadReceipt(cohortId));
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, receipt.Outcome);
            Assert.Equal(AcceptanceCohortAttributionOutcome.NotApplicable, receipt.Attribution);
            Assert.Null(receipt.AttributionSource);
            Assert.Empty(receipt.AttributedMembers);
            Assert.Empty(scenario.Store.ReadPartitionReceipts(cohortId));
            reader.Close();
            command.CommandText = "SELECT COUNT(*) FROM cohort_attribution_effects;";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
        Assert.Empty(scenario.Store.ReadSuppressedPairs());
        Assert.Empty(await scenario.Events.ReadAllAsync());
        Assert.Null(PostLandingCanaryEmergencyCircuit.TryRead(scenario.Events.Identity));
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        Assert.Empty(scenario.ReadLog().Where(evt => evt.EventKind == "canary-gate"));
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);
    }
}
