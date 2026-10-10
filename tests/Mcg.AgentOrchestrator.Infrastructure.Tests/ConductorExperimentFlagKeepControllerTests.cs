using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its durable stores and policy file.
public sealed class ConductorExperimentFlagKeepControllerTests
{
    [Fact]
    public void TryKeep_AppliedFlag_FilesOneItemThenConfirms()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intentsBefore = fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult();
        Assert.Equal(OperatorIntentVerbs.ExperimentApplyFlag, Assert.Single(intentsBefore).Verb);

        Assert.True(Controller(fixture).TryKeep(record, Reading(), ExperimentFlagTestFixture.Now));

        var item = AssertConfirmed(fixture, record);
        Assert.Equal(BacklogItemStatus.Open, item.Status);
        Assert.Contains(record.Id, item.Body);
        Assert.Contains("followerGatesEnabled", item.Body);
        Assert.Contains("True", item.Body);
        Assert.Contains("permanent", item.Title);
        Assert.Contains("remove the flag", item.Title);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
        var intentsAfter = fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult();
        Assert.Equal(intentsBefore.Select(intent => intent.Id), intentsAfter.Select(intent => intent.Id));
    }

    [Theory]
    [InlineData("inconclusive")]
    [InlineData("revert")]
    [InlineData("guardrail")]
    [InlineData("stop-unmet")]
    [InlineData("prior-missing")]
    [InlineData("policy-drift")]
    [InlineData("policy-missing")]
    [InlineData("non-flag")]
    public void TryKeep_IneligibleReadingOrIntervention_LeavesStoresUntouched(string condition)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = condition == "non-flag"
            ? fixture.Add(ExperimentFlagTestFixture.Spec() with
                { Intervention = new(ExperimentInterventionKind.BriefOrPromptChange, "Shorten brief") })
            : condition == "prior-missing" ? fixture.Add() : Apply(fixture);
        if (condition is "prior-missing" or "non-flag")
            File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        if (condition == "policy-drift")
            File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: false));
        if (condition == "policy-missing") File.Delete(fixture.PolicyPath);
        var before = File.Exists(fixture.PolicyPath) ? File.ReadAllBytes(fixture.PolicyPath) : null;
        var reading = Reading(condition is "inconclusive" or "revert" ? condition : "keep",
            breached: condition == "guardrail", stopRuleMet: condition != "stop-unmet");

        Assert.False(Controller(fixture).TryKeep(record, reading, ExperimentFlagTestFixture.Now));

        var unchanged = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, unchanged.Outcome);
        Assert.Null(unchanged.Decision);
        Assert.False(File.Exists(fixture.Workspace.BacklogStorePath));
        if (before is null) Assert.False(File.Exists(fixture.PolicyPath));
        else Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    [Fact]
    public void TryKeep_SecondCallWithStaleRecord_PreservesDecisionAndItem()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        var controller = Controller(fixture);
        Assert.True(controller.TryKeep(record, Reading(), ExperimentFlagTestFixture.Now));
        var first = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        var item = AssertConfirmed(fixture, record);

        Assert.False(controller.TryKeep(record, Reading(), ExperimentFlagTestFixture.Now.AddHours(1)));
        Assert.False(Controller(fixture).TryKeep(first, Reading(), ExperimentFlagTestFixture.Now.AddHours(2)));

        Assert.Equal(first.Decision, fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Decision);
        AssertItemUnchanged(item, AssertConfirmed(fixture, record));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryKeep_DecisionFailsAfterUpsert_RestartConfirmsWithoutSecondItem(bool executors)
    {
        using var fixture = new ExperimentFlagTestFixture();
        if (executors) File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson());
        var record = Apply(fixture, executors);
        // Abort exactly the decision write after the real backlog upsert has succeeded.
        ExecuteSql(fixture, """
            CREATE TRIGGER fail_keep_decision BEFORE UPDATE OF outcome ON experiments
            BEGIN SELECT RAISE(ABORT, 'simulated decision failure'); END;
            """);
        var error = Assert.Throws<SqliteException>(() => Controller(fixture)
            .TryKeep(record, Reading(), ExperimentFlagTestFixture.Now));
        Assert.Contains("simulated decision failure", error.Message);
        var pending = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, pending.Outcome);
        Assert.Null(pending.Decision);
        var existing = Assert.Single(new BacklogStore(fixture.Workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult());
        ExecuteSql(fixture, "DROP TRIGGER fail_keep_decision;");

        var restarted = new ConductorExperimentFlagKeepController(new ExperimentStore(fixture.Workspace.ExperimentStorePath),
            () => new BacklogStore(fixture.Workspace.BacklogStorePath), fixture.Flags);
        Assert.True(restarted.TryKeep(record, Reading(), ExperimentFlagTestFixture.Now.AddHours(1)));
        var decided = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.False(restarted.TryKeep(record, Reading(), ExperimentFlagTestFixture.Now.AddHours(2)));
        AssertItemUnchanged(existing, AssertConfirmed(fixture, record));
        Assert.Equal(decided.Decision, fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Decision);
    }

    [Fact]
    public void TryKeep_BacklogFails_LeavesExperimentOpenAndPolicyUntouched()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var controller = new ConductorExperimentFlagKeepController(fixture.Experiments,
            () => throw new IOException("simulated backlog failure"), fixture.Flags);
        Assert.Throws<IOException>(() => controller.TryKeep(record, Reading(), ExperimentFlagTestFixture.Now));
        Assert.Equal(ExperimentOutcomeState.Open, fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
        Assert.False(File.Exists(fixture.Workspace.BacklogStorePath));
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    [Fact]
    public void TryKeep_AlreadyDecided_DoesNotFileFollowUp()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var stale = Apply(fixture);
        fixture.Experiments.DecideAsync(stale.Id, ExperimentOutcomeState.Refuted, "owner", "Restore prior")
            .GetAwaiter().GetResult();
        var decided = fixture.Experiments.ResolveAsync(stale.Id).GetAwaiter().GetResult()!;
        Assert.False(Controller(fixture).TryKeep(stale, Reading(), ExperimentFlagTestFixture.Now));
        Assert.Equal(decided.Decision, fixture.Experiments.ResolveAsync(stale.Id).GetAwaiter().GetResult()!.Decision);
        Assert.False(File.Exists(fixture.Workspace.BacklogStorePath));
    }

    [Fact]
    public void KeepController_SourceHasNoPolicyWriterOrIntent()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Mcg.AgentOrchestrator.App",
            "Orchestration", "ConductorExperimentFlagKeepController.cs"));
        foreach (var forbidden in new[] { "File.Write", "File.Move", "ExperimentPolicyPropertyRewrite", "EnqueueAsync", "OperatorIntentVerbs" })
            Assert.DoesNotContain(forbidden, source);
    }

    [Fact]
    public void Executors_Keep_FilesOnceAndConfirmsWithoutWriting()
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson());
        var record = Apply(fixture, executors: true);
        var before = File.ReadAllBytes(fixture.ExecutorsPath);
        Assert.True(Controller(fixture).TryKeep(record, Reading(), ExperimentFlagTestFixture.Now));
        var item = AssertConfirmed(fixture, record);
        Assert.Equal(BacklogItemStatus.Open, item.Status);
        Assert.Contains("focusedEvidenceShadow", item.Body);
        Assert.Contains("True", item.Body);
        Assert.Contains("permanent", item.Title);
        Assert.Contains("remove the flag", item.Title);
        Assert.False(Controller(fixture).TryKeep(record, Reading(), ExperimentFlagTestFixture.Now.AddHours(1)));
        AssertItemUnchanged(item, AssertConfirmed(fixture, record));
        Assert.Equal(before, File.ReadAllBytes(fixture.ExecutorsPath));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("bogus")]
    [InlineData("missing")]
    [InlineData("block-absent")]
    [InlineData("mode-absent")]
    public void Executors_Drift_DoesNotFileOrConfirm(string drift)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson());
        var record = Apply(fixture, executors: true);
        if (drift == "missing") File.Delete(fixture.ExecutorsPath);
        else File.WriteAllText(fixture.ExecutorsPath, drift switch
        {
            "block-absent" => "{\"executors\":[],\"lanes\":[]}",
            "mode-absent" => "{\"executors\":[],\"lanes\":[],\"focusedEvidence\":{}}",
            _ => ExperimentFlagTestFixture.ExecutorsJson(drift)
        });
        Assert.False(Controller(fixture).TryKeep(record, Reading(), ExperimentFlagTestFixture.Now));
        Assert.False(File.Exists(fixture.Workspace.BacklogStorePath));
        var unchanged = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, unchanged.Outcome);
        Assert.Null(unchanged.Decision);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => VerifiedRepositoryRoot.Find(source);

    private static ExperimentRecord Apply(ExperimentFlagTestFixture fixture, bool executors = false)
    {
        var record = fixture.Add(executors ? ExperimentFlagTestFixture.ExecutorsSpec() : null);
        var intent = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(intent).Status);
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(false, record.Spec.Intervention.FlagTarget!.PriorValue);
        Assert.True(fixture.Flags.Read(record.Spec.Intervention.FlagTarget));
        return record;
    }

    private static ConductorExperimentFlagKeepController Controller(ExperimentFlagTestFixture fixture) =>
        new(fixture.Experiments, () => new BacklogStore(fixture.Workspace.BacklogStorePath), fixture.Flags);

    private static BacklogItem AssertConfirmed(ExperimentFlagTestFixture fixture, ExperimentRecord record)
    {
        var decided = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Confirmed, decided.Outcome);
        var item = Assert.Single(new BacklogStore(fixture.Workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
        Assert.Equal($"experiment-flag-keep-{record.Id}", item.Id);
        Assert.Equal($"backlog:{item.Id}", decided.Decision!.Evidence);
        Assert.Contains($"{record.Spec.Intervention.FlagTarget!.PropertyName}=True", decided.Decision.Action);
        Assert.Contains("remove the flag", decided.Decision.Action);
        return item;
    }

    private static void ExecuteSql(ExperimentFlagTestFixture fixture, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={fixture.Workspace.ExperimentStorePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertItemUnchanged(BacklogItem expected, BacklogItem actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private static ExperimentReadingResult Reading(string verdict = "keep", bool breached = false, bool stopRuleMet = true) =>
        new(2, new(2, ExperimentStopUnit.Goals), stopRuleMet, [], verdict, "test reading", breached);
}
