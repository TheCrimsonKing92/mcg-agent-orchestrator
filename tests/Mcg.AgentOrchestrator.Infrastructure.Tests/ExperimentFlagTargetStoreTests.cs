using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its SQLite database and policy fixture.
public sealed class ExperimentFlagTargetStoreTests
{
    [Fact]
    public async Task FlagTarget_RoundTripsAndPriorCaptureOnlyChangesPriorOnce()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        var reopened = new ExperimentStore(fixture.Workspace.ExperimentStorePath);
        Assert.Equal(record.Spec.Intervention.FlagTarget, (await reopened.ResolveAsync(record.Id))!.Spec.Intervention.FlagTarget);
        Assert.True(await reopened.RecordFlagPriorAsync(record.Id, false));
        var expected = record.Spec with { Intervention = record.Spec.Intervention with
            { FlagTarget = record.Spec.Intervention.FlagTarget! with { PriorValue = false } } };
        Assert.Equal(JsonSerializer.Serialize(expected, ExperimentStore.JsonOptions),
            JsonSerializer.Serialize((await reopened.ResolveAsync(record.Id))!.Spec, ExperimentStore.JsonOptions));
        Assert.False(await reopened.RecordFlagPriorAsync(record.Id, true));
        Assert.False((await reopened.ResolveAsync(record.Id))!.Spec.Intervention.FlagTarget!.PriorValue);
        await reopened.DecideAsync(record.Id, ExperimentOutcomeState.Refuted, "receipt", "Restore");
        Assert.False(await reopened.RecordFlagPriorAsync(record.Id, true));
    }

    [Fact]
    public async Task LegacyIntervention_WithNoFlagTargetStillReads()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Workspace.ExperimentStorePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE experiments SET intervention_json=$json WHERE id=$id";
            command.Parameters.AddWithValue("$json", "{\"kind\":\"config-flag\",\"description\":\"legacy\"}");
            command.Parameters.AddWithValue("$id", record.Id);
            await command.ExecuteNonQueryAsync();
        }
        var loaded = (await new ExperimentStore(fixture.Workspace.ExperimentStorePath).ResolveAsync(record.Id))!;
        Assert.Equal(ExperimentInterventionKind.ConfigFlag, loaded.Spec.Intervention.Kind);
        Assert.Null(loaded.Spec.Intervention.FlagTarget);
        Assert.False(await fixture.Experiments.RecordFlagPriorAsync(record.Id, false));
    }
}
