using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalIntakeRequestMapperTests
{
    [Xunit.Fact]
    public void Map_simple_aliases_have_equivalent_payload()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var agents = AgentCatalog.Default().Agents;

        var goalAlias = GoalIntakeRequestMapper.Map(
            ["goal", "One task", "--simple", "--request-key", "alias-a"],
            workspace,
            agents)!;
        var direct = GoalIntakeRequestMapper.Map(
            ["simple-goal", "One task", "--request-key", "alias-b"],
            workspace,
            agents)!;

        Xunit.Assert.Equal("simple", goalAlias.Mode);
        Xunit.Assert.Equal(goalAlias.Fingerprint, direct.Fingerprint);
    }

    [Xunit.Theory]
    [Xunit.InlineData("pipeline")]
    [Xunit.InlineData("run")]
    [Xunit.InlineData("coverage")]
    [Xunit.InlineData("agent")]
    public void Map_effectful_inputs_change_fingerprint(string changedInput)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var agents = AgentCatalog.Default().Agents;
        var baseline = new List<string>
        {
            "goal", "Effectful payload", "--request-key", "same-key",
            "--backlog-item", "abc", "--backlog-coverage", "slice"
        };
        var changed = new List<string>(baseline);
        switch (changedInput)
        {
            case "pipeline":
                changed.AddRange(["--pipeline", "five-role"]);
                break;
            case "run":
                changed.Add("--run");
                break;
            case "coverage":
                changed[changed.IndexOf("slice")] = "full";
                break;
            case "agent":
                changed.AddRange(["--developer", AgentCatalog.Default().GetRequired(AgentRole.Developer).Id.Value]);
                break;
        }

        var first = GoalIntakeRequestMapper.Map(baseline, workspace, agents)!;
        var second = GoalIntakeRequestMapper.Map(changed, workspace, agents)!;

        Xunit.Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }
}
