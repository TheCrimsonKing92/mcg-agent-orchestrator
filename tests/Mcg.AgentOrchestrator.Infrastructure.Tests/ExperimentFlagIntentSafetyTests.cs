using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns all databases and policy files.
public sealed class ExperimentFlagIntentSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultCoordinator_DoesNotOpenAbsentOrCorruptExperimentDatabase(bool corrupt)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.Delete(fixture.Workspace.ExperimentStorePath);
        if (corrupt) File.WriteAllText(fixture.Workspace.ExperimentStorePath, "not a sqlite database");
        var before = corrupt ? File.ReadAllBytes(fixture.Workspace.ExperimentStorePath) : null;
        OperatorIntentCoordinator.CreateDefault(fixture.Workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
        if (corrupt) Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.ExperimentStorePath));
        else Assert.False(File.Exists(fixture.Workspace.ExperimentStorePath));
    }

    [Fact]
    public void Apply_AbsentDatabaseRejectsWithoutCreatingIt()
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.Delete(fixture.Workspace.ExperimentStorePath);
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = fixture.Submit(new string('f', 32));
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        Assert.Contains("experiment-not-found", fixture.Result(intent).Outcome);
        Assert.False(File.Exists(fixture.Workspace.ExperimentStorePath));
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    [Fact]
    public void Apply_BelowMutateRejectsBeforeOpeningCorruptDatabase()
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.Workspace.ExperimentStorePath, "not a sqlite database");
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = fixture.Submit(new string('f', 32), assurance: "unverified");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        Assert.Contains("tier-below-mutate", fixture.Result(intent).Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
        Assert.Equal("not a sqlite database", File.ReadAllText(fixture.Workspace.ExperimentStorePath));
    }

    [Fact]
    public void Apply_PreservesCommentsWhitespaceAndNestedProperties()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var json = "{ /* operator note ✓ */ \"name\":\"trial\",\r\n" +
            " \"maxConcurrentPaidWorkers\": 1,\r\n" +
            " \"transitionMap\":" + JsonSerializer.Serialize(ConductorAutonomyPolicy.Conservative.TransitionMap
                .ToDictionary(entry => entry.Key.ToString(), entry => entry.Value.ToString())) + ",\r\n" +
            " \"followerGatesEnabled\" : false, // experiment flag\r\n" +
            " \"unknown\": { \"followerGatesEnabled\": false } }\r\n";
        Assert.False(ConductorAutonomyPolicy.ParseJson(json).FollowerGatesEnabled);
        File.WriteAllText(fixture.PolicyPath, json);
        var record = fixture.Add();
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        Assert.Equal(json.Replace("\"followerGatesEnabled\" : false", "\"followerGatesEnabled\" : true"),
            File.ReadAllText(fixture.PolicyPath));
        fixture.Submit(record.Id, revert: true);
        fixture.Tick();
        Assert.Equal(json, File.ReadAllText(fixture.PolicyPath));
    }

    [Theory]
    [InlineData(false, "conductor", "conductor-experiment-revert", OperatorActorKind.Agent, false)]
    [InlineData(true, "other", "conductor-experiment-revert", OperatorActorKind.Agent, false)]
    [InlineData(true, "conductor", "cli", OperatorActorKind.Agent, false)]
    [InlineData(true, "conductor", "conductor-experiment-revert", OperatorActorKind.Human, false)]
    [InlineData(true, "conductor", "conductor-experiment-revert", OperatorActorKind.Agent, true)]
    public void StewardAssurance_AllowsOnlyBoundConductorRevert(bool revert, string actor, string channel,
        OperatorActorKind actorKind, bool allowed)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        fixture.Submit(record.Id);
        fixture.Tick();
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var intent = fixture.Submit(record.Id, revert, OperatorIntentAdjudication.StewardAssurance,
            actor: actor, channel: channel, actorKind: actorKind);
        fixture.Tick();
        Assert.Equal(allowed ? OperatorIntentStatus.Applied : OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        if (allowed) Assert.False(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
        else
        {
            Assert.Contains("steward-capability-boundary", fixture.Result(intent).Outcome);
            Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
        }
    }

    [Fact]
    public void FailedApplyWithCapturedPrior_DoesNotAutoRefuteOrOpenIntentStore()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add();
        var intent = fixture.Submit(record.Id);
        using (var lockedPolicy = new FileStream(fixture.PolicyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.False(record.Spec.Intervention.FlagTarget!.PriorValue);
        Assert.False(ConductorAutonomyPolicy.ParseJson(File.ReadAllText(fixture.PolicyPath)).FollowerGatesEnabled);
        var controller = new ConductorExperimentFlagRevertController(fixture.Experiments,
            () => throw new InvalidOperationException("intent store must stay unopened"), fixture.PolicyPath);
        var reading = new ExperimentReadingResult(0, new(99, ExperimentStopUnit.Goals), false, [], "revert", "fault path", true);
        Assert.False(controller.TryRevert(record, reading, ExperimentFlagTestFixture.Now));
        Assert.Equal(ExperimentOutcomeState.Open, fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
        Assert.DoesNotContain(fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult(),
            entry => entry.Verb == OperatorIntentVerbs.ExperimentRevertFlag);
    }
}
