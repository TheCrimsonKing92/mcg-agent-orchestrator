using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceShardPolicySwitchesTests
{
    private static readonly string[] ChangedFiles =
        ["src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.cs"];

    [Fact]
    public void ExplicitAndScopedSwitchesAvoidEnvironmentLookup()
    {
        var noRead = new Func<string, string?>(_ => throw new InvalidOperationException("Environment lookup was used"));
        var explicitSwitches = new AcceptanceShardPolicySwitches(FullShards: true, ChangeScoped: false, ReadVariable: noRead);
        using var scope = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true, ReadVariable: noRead));

        Assert.True(AcceptancePolicyShardPlanner.BuildPolicyShardPlan(ChangedFiles, explicitSwitches).ForceFull);
        Assert.False(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled(explicitSwitches));
        Assert.False(AcceptancePolicyShardPlanner.BuildPolicyShardPlan(ChangedFiles).ForceFull);
        Assert.True(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled());
    }

    [Fact]
    public void MissingOverridesReadTheOriginalVariableNamesAndTruthTables()
    {
        var names = new List<string>();
        var values = new Dictionary<string, string?>
        {
            ["MCG_ACCEPTANCE_FULL_SHARDS"] = "yes",
            ["MCG_ACCEPTANCE_CHANGE_SCOPED"] = "off"
        };
        string? ReadVariable(string name)
        {
            names.Add(name);
            return values.GetValueOrDefault(name);
        }

        var switches = new AcceptanceShardPolicySwitches(ReadVariable: ReadVariable);
        Assert.True(AcceptancePolicyShardPlanner.BuildPolicyShardPlan(ChangedFiles, switches).ForceFull);
        Assert.False(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled(switches));
        Assert.Equal(["MCG_ACCEPTANCE_FULL_SHARDS", "MCG_ACCEPTANCE_CHANGE_SCOPED"], names);

        names.Clear();
        values["MCG_ACCEPTANCE_FULL_SHARDS"] = "off";
        values["MCG_ACCEPTANCE_CHANGE_SCOPED"] = "";
        Assert.False(AcceptancePolicyShardPlanner.BuildPolicyShardPlan(ChangedFiles, switches).ForceFull);
        Assert.True(AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled(switches));
        Assert.Equal(["MCG_ACCEPTANCE_FULL_SHARDS", "MCG_ACCEPTANCE_CHANGE_SCOPED", "MCG_ACCEPTANCE_CHANGE_SCOPED"], names);
    }

    [Fact]
    public void UnsetOverrideUsesTheSameProcessEnvironmentValues()
    {
        static bool Enabled(string? value) => string.IsNullOrWhiteSpace(value) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);

        var full = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_FULL_SHARDS");
        var change = Environment.GetEnvironmentVariable("MCG_ACCEPTANCE_CHANGE_SCOPED");
        var expectedFull = full is not null && Enabled(full);
        var expectedScoped = Enabled(change);
        Assert.Equal(expectedScoped, AcceptancePolicyShardPlanner.ChangeScopedAcceptanceEnabled());
        Assert.Equal(expectedFull || !expectedScoped,
            AcceptancePolicyShardPlanner.BuildPolicyShardPlan(ChangedFiles).ForceFull);
    }
}
