using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every case owns all stores and files.
public sealed class ExperimentFlagIntentHandlerTests
{
    [Fact]
    public void Apply_ChangesOnlyTargetAndCapturesPrior_ReplayWritesNothing()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        using var before = JsonDocument.Parse(File.ReadAllText(fixture.PolicyPath));
        var intent = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(intent).Status);
        Assert.Contains("tier=Mutate", fixture.Result(intent).Outcome);
        using var after = JsonDocument.Parse(File.ReadAllText(fixture.PolicyPath));
        Assert.True(after.RootElement.GetProperty("followerGatesEnabled").GetBoolean());
        Assert.Equal(before.RootElement.EnumerateObject().Select(p => p.Name), after.RootElement.EnumerateObject().Select(p => p.Name));
        foreach (var property in before.RootElement.EnumerateObject().Where(p => p.Name != "followerGatesEnabled"))
            Assert.Equal(property.Value.GetRawText(), after.RootElement.GetProperty(property.Name).GetRawText());
        Assert.True(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
        Assert.False(fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Spec.Intervention.FlagTarget!.PriorValue);
        var bytes = File.ReadAllBytes(fixture.PolicyPath);
        var timestamp = File.GetLastWriteTimeUtc(fixture.PolicyPath);
        var replay = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(replay).Status);
        Assert.Contains("replayed=True", fixture.Result(replay).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PolicyPath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.PolicyPath));
    }

    [Theory]
    [InlineData("tier-below-mutate")]
    [InlineData("experiment-decided")]
    [InlineData("property-not-allowlisted")]
    [InlineData("flag-target-missing")]
    [InlineData("file-kind-unsupported")]
    [InlineData("not-config-flag")]
    [InlineData("policy-file-invalid")]
    [InlineData("experiment-not-found")]
    public void Apply_RejectsWithRecordedReasonAndByteIdenticalPolicy(string reason)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var spec = ExperimentFlagTestFixture.Spec();
        if (reason == "property-not-allowlisted") spec = spec with { Intervention = spec.Intervention with
            { FlagTarget = spec.Intervention.FlagTarget! with { PropertyName = "maxCriterionRetries" } } };
        if (reason == "flag-target-missing") spec = spec with { Intervention = spec.Intervention with { FlagTarget = null } };
        if (reason == "not-config-flag") spec = spec with { Intervention = spec.Intervention with { Kind = ExperimentInterventionKind.Policy } };
        var record = fixture.Add(spec);
        if (reason == "file-kind-unsupported")
        {
            // An old or malformed stored target can omit fileKind; deserialization supplies zero.
            var intervention = JsonSerializer.SerializeToNode(spec.Intervention, ExperimentStore.JsonOptions)!;
            intervention["flagTarget"]!.AsObject().Remove("fileKind");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Workspace.ExperimentStorePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE experiments SET intervention_json=$json WHERE id=$id";
            command.Parameters.AddWithValue("$json", intervention.ToJsonString());
            command.Parameters.AddWithValue("$id", record.Id);
            command.ExecuteNonQuery();
        }
        if (reason == "experiment-decided") fixture.Experiments.DecideAsync(record.Id, ExperimentOutcomeState.Confirmed, "receipt", "Keep").GetAwaiter().GetResult();
        if (reason == "policy-file-invalid") File.WriteAllText(fixture.PolicyPath, "{\"name\":\"trial\",\"followerGatesEnabled\":\"true\"}");
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = fixture.Submit(reason == "experiment-not-found" ? new string('f', 32) : record.Id,
            assurance: reason == "tier-below-mutate" ? "unverified" : "local-process");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        Assert.Contains(reason, fixture.Result(intent).Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
        Assert.Null(fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Spec.Intervention.FlagTarget?.PriorValue);
    }

    [Fact]
    public void Revert_AfterRefutedDecisionRestoresPrior_ThenReplayWritesNothing()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        fixture.Submit(record.Id);
        fixture.Tick();
        fixture.Experiments.DecideAsync(record.Id, ExperimentOutcomeState.Refuted, "intent:revert", "Restore").GetAwaiter().GetResult();
        var revert = fixture.Submit(record.Id, revert: true);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(revert).Status);
        Assert.False(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
        var bytes = File.ReadAllBytes(fixture.PolicyPath);
        var replay = fixture.Submit(record.Id, revert: true);
        fixture.Tick();
        Assert.Contains("replayed=True", fixture.Result(replay).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.PolicyPath));
    }

    [Fact]
    public void AbsentProperty_RecordsEffectiveDefaultAndRestoresIt()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var policy = JsonNode.Parse(ExperimentFlagTestFixture.PolicyJson())!.AsObject();
        policy.Remove("followerGatesEnabled");
        policy["unknown"] = 5;
        File.WriteAllText(fixture.PolicyPath, policy.ToJsonString());
        var record = fixture.Add();
        fixture.Submit(record.Id);
        fixture.Tick();
        Assert.False(fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Spec.Intervention.FlagTarget!.PriorValue);
        fixture.Submit(record.Id, revert: true);
        fixture.Tick();
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.PolicyPath));
        Assert.False(document.RootElement.GetProperty("followerGatesEnabled").GetBoolean());
        Assert.Equal(5, document.RootElement.GetProperty("unknown").GetInt32());
    }

    [Fact]
    public void DefaultCoordinator_WiresWorkspaceExperimentServices()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        var intent = fixture.Submit(record.Id);
        OperatorIntentCoordinator.CreateDefault(fixture.Workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(intent).Status);
        Assert.True(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
    }
}
