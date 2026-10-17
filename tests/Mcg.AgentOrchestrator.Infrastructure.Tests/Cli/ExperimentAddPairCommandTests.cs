using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: private workspace databases and the existing AsyncLocal console capture.
public sealed class ExperimentAddPairCommandTests
{
    [Fact]
    public void AddPair_EmptyIncrementalCohortShowsBothRecordedMembers()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Seed(workspace);
            var record = CliCommandTestsExperiments.Add(workspace, Spec());
            var reason = Reason(workspace);
            var output = Add(workspace, record.Id[..8], "AAAA", "bbbb", reason);
            Assert.Contains("member pair added:", output);
            var events = new ExperimentStore(workspace.ExperimentStorePath).ListMemberEventsAsync(record.Id).GetAwaiter().GetResult();
            Assert.Equal(2, events.Count);
            Assert.Equal(Digest("a"), events[0].BriefSha256);
            Assert.Equal(Digest("b"), events[1].BriefSha256);
            var shown = CliCommandTestsExperiments.Execute(["experiment-show", record.Id], workspace);
            Assert.Equal(2, shown.Split(Environment.NewLine).Count(line => line.StartsWith("member added: ", StringComparison.Ordinal)));
            foreach (var member in events)
                Assert.Contains($"member added: {member.PairId} {member.Arm} {member.GoalId} brief-sha256 {member.BriefSha256} at {member.AddedAt:O}: matched briefs", shown);
            Assert.Contains($"baseline cohort goals: {Id('a')}", shown);
            Assert.Contains($"comparison cohort goals: {Id('b')}", shown);
        });
    }

    [Theory]
    [InlineData("closed", "rule experiment-state:")]
    [InlineData("unknown-experiment", "rule experiment-state:")]
    [InlineData("not-cohort", "rule experiment-state:")]
    [InlineData("not-incremental", "rule experiment-state:")]
    [InlineData("baseline-dispatched", "rule not-dispatched:")]
    [InlineData("comparison-dispatched", "rule not-dispatched:")]
    [InlineData("ambiguous", "rule goal-resolution:")]
    [InlineData("unknown", "rule goal-resolution:")]
    [InlineData("same-goal", "rule goal-resolution:")]
    [InlineData("already-member", "rule single-membership:")]
    [InlineData("blank-reason", "rule reason:")]
    [InlineData("missing-reason", "rule reason:")]
    public void AddPair_IntegrityFailureNamesRuleAndWritesNothing(string fault, string rule)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Seed(workspace, fault == "baseline-dispatched" ? 'a' : fault == "comparison-dispatched" ? 'b' : null);
            var spec = fault == "not-cohort" ? CliCommandTestsExperiments.Spec() : Spec();
            if (fault == "not-incremental") spec = spec with { Baseline = spec.Baseline with
                { IncrementalMembers = null, BaselineGoalIds = ["cccc"], ComparisonGoalIds = ["dddd"] } };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            if (fault == "closed")
                store.DecideAsync(record.Id, ExperimentOutcomeState.Inconclusive, "receipt", "stop").GetAwaiter().GetResult();
            if (fault == "already-member")
                CliCommandTestsExperiments.Add(workspace, Spec() with { Baseline = Spec().Baseline with { ComparisonGoalIds = [" AAAA "] } });
            var reason = Reason(workspace);
            if (fault == "blank-reason") File.WriteAllText(reason, " \r\n\t");
            if (fault == "missing-reason") File.Delete(reason);
            var before = ExperimentMemberStoreTests.EventCount(workspace.ExperimentStorePath);
            var error = Assert.ThrowsAny<Exception>(() => Add(workspace,
                fault == "unknown-experiment" ? "unknown" : record.Id,
                fault == "ambiguous" ? "aa" : fault == "unknown" ? "unknown" : "aaaa",
                fault == "same-goal" ? Id('a') : "bbbb", reason));
            Assert.Contains(rule, error.Message);
            Assert.Equal(before, ExperimentMemberStoreTests.EventCount(workspace.ExperimentStorePath));
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void AddPair_PairingRuleEnforcesOnlyTheRegisteredDigestOrder(bool rule, bool swapped)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            Seed(workspace);
            // Known texts: SHA256("b") sorts below SHA256("a").
            var baselineDigest = Digest("a");
            var comparisonDigest = Digest("b");
            Assert.True(string.CompareOrdinal(comparisonDigest, baselineDigest) < 0);
            var spec = Spec() with { Baseline = Spec().Baseline with
                { PairingRule = rule ? "lower-brief-sha256-is-comparison" : null } };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            var reason = Reason(workspace);
            if (rule && swapped)
            {
                var error = Assert.Throws<InvalidOperationException>(() => Add(workspace, record.Id, "bbbb", "aaaa", reason));
                Assert.Contains("rule pairing:", error.Message);
                Assert.Contains(baselineDigest, error.Message);
                Assert.Contains(comparisonDigest, error.Message);
                Assert.Equal(0, ExperimentMemberStoreTests.EventCount(workspace.ExperimentStorePath));
            }
            else
            {
                Add(workspace, record.Id, swapped ? "bbbb" : "aaaa", swapped ? "aaaa" : "bbbb", reason);
                Assert.Equal(2, ExperimentMemberStoreTests.EventCount(workspace.ExperimentStorePath));
            }
        });
    }

    [Theory]
    [InlineData("unknown-rule", "baseline.pairingRule:")]
    [InlineData("twin-incremental", "baseline.incrementalMembers:")]
    [InlineData("twin-false", "baseline.incrementalMembers:")]
    [InlineData("twin-pairing", "baseline.pairingRule:")]
    [InlineData("blank", "entries must be non-blank")]
    [InlineData("null-entry", "entries must be non-blank")]
    [InlineData("repeat", "repeated id")]
    [InlineData("shared", "also appears")]
    public void Add_InvalidIncrementalSpecRefusesBeforeRegistration(string fault, string rule)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var baseline = Spec().Baseline;
            baseline = fault switch
            {
                "unknown-rule" => baseline with { PairingRule = "unknown" },
                "twin-incremental" => new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "first", ComparisonGoalId: "second", IncrementalMembers: true),
                "twin-false" => new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "first", ComparisonGoalId: "second", IncrementalMembers: false),
                "twin-pairing" => new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "first", ComparisonGoalId: "second", PairingRule: "lower-brief-sha256-is-comparison"),
                "blank" => baseline with { BaselineGoalIds = [" "] },
                "null-entry" => baseline with { ComparisonGoalIds = [null!] },
                "repeat" => baseline with { BaselineGoalIds = ["aaaa", " AAAA "] },
                "shared" => baseline with { BaselineGoalIds = ["aaaa"], ComparisonGoalIds = [" AAAA "] },
                _ => throw new ArgumentException(fault)
            };
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var error = Assert.Throws<ArgumentException>(() => CliCommandTestsExperiments.Add(workspace, Spec() with { Baseline = baseline }));
            Assert.Contains(rule, error.Message);
            Assert.Equal(0, store.CountAsync().GetAwaiter().GetResult());
            Assert.Equal(0, ExperimentMemberStoreTests.EventCount(workspace.ExperimentStorePath));
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("partial-baseline")]
    [InlineData("partial-comparison")]
    public void Add_IncrementalSpecAllowsMissingOrPartialLists(string shape)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Spec() with { Baseline = Spec().Baseline with
            {
                BaselineGoalIds = shape == "null" ? null : shape == "partial-baseline" ? ["aaaa"] : [],
                ComparisonGoalIds = shape == "null" ? null : shape == "partial-comparison" ? ["bbbb"] : []
            } };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            Assert.Equal(spec.Baseline.BaselineGoalIds, record.Spec.Baseline.BaselineGoalIds);
            Assert.Equal(spec.Baseline.ComparisonGoalIds, record.Spec.Baseline.ComparisonGoalIds);
        });
    }

    [Fact]
    public void Show_LegacySpecPreservesStoredJsonAndHasNoMemberLines()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var spec = Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
                BaselineGoalIds: ["aaaa"], ComparisonGoalIds: ["bbbb"]) };
            var record = CliCommandTestsExperiments.Add(workspace, spec);
            using var connection = new SqliteConnection($"Data Source={workspace.ExperimentStorePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT baseline_json FROM experiments WHERE id=$id";
            command.Parameters.AddWithValue("$id", record.Id);
            var json = Assert.IsType<string>(command.ExecuteScalar());
            Assert.Equal(JsonSerializer.Serialize(spec.Baseline, ExperimentStore.JsonOptions), json);
            Assert.DoesNotContain("incrementalMembers", json);
            Assert.DoesNotContain("pairingRule", json);
            var shown = CliCommandTestsExperiments.Execute(["experiment-show", record.Id], workspace);
            Assert.DoesNotContain("member added:", shown);
        });
    }

    [Fact]
    public void Catalog_AddPairIsHandledWithoutKernelAndDocumentsEveryFlag()
    {
        Assert.Contains("experiment-add-pair", CliArgumentParser.RecognizedCommands);
        Assert.True(CliHandledVerbRegistry.IsHandled("experiment-add-pair"));
        Assert.True(CliPersistentStateRunner.SkipsKernelState(["experiment-add-pair"]));
        foreach (var flag in new[] { "--baseline", "--comparison", "--reason-file", "--help", "-h" })
            CliCommandHelp.ThrowIfInvalidFlags(["experiment-add-pair", "id", flag, "value"]);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["experiment-add-pair", "id", "--unknown", "value"]));
        Assert.Contains("--baseline <goal-prefix> --comparison <goal-prefix> --reason-file <path>", CliCommandHelp.ExperimentAddPairUsage);
    }

    internal static ExperimentSpec Spec() => CliCommandTestsExperiments.Spec() with
    {
        Baseline = new(ExperimentBaselineKind.GoalCohort, BaselineGoalIds: [], ComparisonGoalIds: [], IncrementalMembers: true),
        StopRule = new(12, ExperimentStopUnit.Goals),
        Metrics = ["rounds-per-landing"],
        Guardrail = new("productive-rounds", new("productive-rounds", ">", 1000)),
        DecisionRule = new([new("rounds-per-landing", "<=", 0)], [new("rounds-per-landing", ">", 0)])
    };

    internal static string Id(char value) => new(value, 32);
    internal static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace, char? dispatched = null, bool landed = false)
    {
        var at = ExperimentMemberStoreTests.At.AddDays(1);
        GoalSnapshot Snapshot(string id, string objective, bool hasDispatch)
        {
            var task = new TaskSnapshot(id + "-task", "implement", AgentRole.Developer,
                landed ? WorkTaskStatus.Completed : WorkTaskStatus.Pending, null, null, null, [], null, null,
                DispatchHistory: hasDispatch ? [new TaskDispatchSnapshot("worker", "command", "root", at)] : []);
            return new(id, objective, landed ? GoalStatus.Completed : GoalStatus.Active, [task], landed
                ? [new ProgressEventSnapshot(id, task.Id, ProgressKind.TaskCompleted, "done", at.AddMinutes(1))] : []);
        }
        var goals = new[] { Snapshot(Id('a'), "a", landed || dispatched == 'a'),
            Snapshot(Id('b'), "b", landed || dispatched == 'b'), Snapshot(Id('c'), "c", landed),
            Snapshot(Id('d'), "d", landed), Snapshot(Id('e'), "unrecorded outsider", landed),
            Snapshot("aaab" + new string('a', 28), "ambiguous alternative", landed) };
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(goals, []));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        var persisted = ExperimentGoals.Read(workspace.SqliteStatePath);
        Assert.Equal("a", persisted.Single(goal => goal.Id.Value == Id('a')).Objective);
        Assert.Equal("b", persisted.Single(goal => goal.Id.Value == Id('b')).Objective);
        return kernel;
    }

    private static string Reason(OrchestratorWorkspace workspace)
    {
        var path = Path.Combine(workspace.RootDirectory, "reason.txt");
        File.WriteAllText(path, "  matched briefs\r\n");
        return path;
    }

    private static string Add(OrchestratorWorkspace workspace, string id, string baseline, string comparison, string reason) =>
        CliCommandTestsExperiments.Execute(["experiment-add-pair", id, "--baseline", baseline, "--comparison", comparison, "--reason-file", reason], workspace);
}
