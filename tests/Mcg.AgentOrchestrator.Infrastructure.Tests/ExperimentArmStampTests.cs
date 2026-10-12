using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its database, flag files and temporary workspace.
public sealed class ExperimentArmStampTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Resolve_RecordsBothFlagFamiliesRelativeToAppliedValue(bool executors, bool applied)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var spec = AlternatingSpec(executors);
        spec = spec with { Intervention = spec.Intervention with
            { FlagTarget = spec.Intervention.FlagTarget! with { ValueToApply = applied } } };
        var record = fixture.Add(spec);
        foreach (var flag in new[] { true, false })
        {
            WriteFlag(fixture, executors, flag);
            var stamp = Assert.Single(ExperimentArmStamp.Resolve(fixture.Root)!);
            Assert.Equal(record.Id, stamp.Key);
            Assert.Equal(flag == applied ? "on" : "off", stamp.Value);
        }
    }

    [Fact]
    public void Resolve_AbsentDatabaseDoesNotCreateOne()
    {
        var root = Directory.CreateTempSubdirectory("arm-stamp-absent-").FullName;
        try
        {
            Assert.Null(ExperimentArmStamp.Resolve(root));
            Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("window")]
    [InlineData("policy-intervention")]
    [InlineData("missing-target")]
    [InlineData("closed")]
    public void Resolve_IneligibleExperimentsHaveNoStamp(string scenario)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var spec = AlternatingSpec();
        spec = scenario switch
        {
            "window" => spec with { Baseline = spec.Baseline with { Kind = ExperimentBaselineKind.BeforeAfterWindow } },
            "policy-intervention" => spec with { Intervention = spec.Intervention with { Kind = ExperimentInterventionKind.Policy } },
            "missing-target" => spec with { Intervention = spec.Intervention with { FlagTarget = null } },
            _ => spec
        };
        var record = fixture.Add(spec);
        if (scenario == "closed")
            fixture.Experiments.DecideAsync(record.Id, ExperimentOutcomeState.Confirmed, "receipt", "keep")
                .GetAwaiter().GetResult();
        Assert.Null(ExperimentArmStamp.Resolve(fixture.Root));
    }

    [Theory]
    [InlineData(false, "file")]
    [InlineData(false, "property")]
    [InlineData(true, "file")]
    [InlineData(true, "property")]
    [InlineData(true, "block")]
    public void Resolve_MissingFlagsAreNotDefaultOff(bool executors, string missing)
    {
        using var fixture = new ExperimentFlagTestFixture();
        fixture.Add(AlternatingSpec(executors));
        WriteFlag(fixture, executors, false);
        var path = executors ? fixture.ExecutorsPath : fixture.PolicyPath;
        if (missing == "file") File.Delete(path);
        else
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            if (!executors) json.AsObject().Remove("followerGatesEnabled");
            else if (missing == "block") json.AsObject().Remove("focusedEvidence");
            else json["focusedEvidence"]!.AsObject().Remove("mode");
            File.WriteAllText(path, json.ToJsonString());
        }
        Assert.Null(ExperimentArmStamp.Resolve(fixture.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_MalformedFlagInputNeverThrows(bool executors)
    {
        using var fixture = new ExperimentFlagTestFixture();
        fixture.Add(AlternatingSpec(executors));
        File.WriteAllText(executors ? fixture.ExecutorsPath : fixture.PolicyPath, "not-json");
        Assert.Null(ExperimentArmStamp.Resolve(fixture.Root));
    }

    [Fact]
    public void Resolve_CorruptDatabaseNeverThrows()
    {
        var root = Directory.CreateTempSubdirectory("arm-stamp-corrupt-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".orchestrator"));
            File.WriteAllText(Path.Combine(root, ".orchestrator", "experiments.db"), "not-sqlite");
            Assert.Null(ExperimentArmStamp.Resolve(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Resolve_OnlyEligibleIdsSurviveAndAnyFailureDiscardsTheMap()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var eligible = fixture.Add(AlternatingSpec());
        fixture.Add(ExperimentFlagTestFixture.Spec());
        WriteFlag(fixture, false, true);
        var stamp = Assert.Single(ExperimentArmStamp.Resolve(fixture.Root)!);
        Assert.Equal(eligible.Id, stamp.Key);
        Assert.Equal("on", stamp.Value);
        fixture.Add(AlternatingSpec(true));
        File.WriteAllText(fixture.ExecutorsPath, "not-json");
        Assert.Null(ExperimentArmStamp.Resolve(fixture.Root));
    }

    [Fact]
    public void Resolve_GoalWorktreeUsesHostDatabaseAndFlags()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = fixture.Add(AlternatingSpec());
        WriteFlag(fixture, false, true);
        var worktree = Path.Combine(fixture.Root, ".orchestrator-worktrees", "goal");
        Directory.CreateDirectory(worktree);
        Assert.Equal("on", ExperimentArmStamp.Resolve(worktree)![record.Id]);
    }

    internal static ExperimentSpec AlternatingSpec(bool executors = false)
    {
        var spec = executors ? ExperimentFlagTestFixture.ExecutorsSpec() : ExperimentFlagTestFixture.Spec();
        return spec with { Baseline = spec.Baseline with { Kind = ExperimentBaselineKind.AlternatingGates } };
    }

    private static void WriteFlag(ExperimentFlagTestFixture fixture, bool executors, bool flag) =>
        File.WriteAllText(executors ? fixture.ExecutorsPath : fixture.PolicyPath, executors
            ? ExperimentFlagTestFixture.ExecutorsJson(flag ? "shadow" : "off")
            : ExperimentFlagTestFixture.PolicyJson(flag));
}
