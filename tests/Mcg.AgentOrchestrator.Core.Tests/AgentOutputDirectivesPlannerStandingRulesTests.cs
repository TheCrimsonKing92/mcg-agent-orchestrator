using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure directive construction, no shared mutable state or I/O.
public sealed class AgentOutputDirectivesPlannerStandingRulesTests
{
    [Fact]
    public void PlannerLine_StandingRules_StatesAllFourRestrictions()
    {
        var directive = Assert.Single(
            AgentOutputDirectives.WorkerResultTemplateLinesForRole(AgentRole.Planner)
                .Where(line => line.StartsWith("Planner:", StringComparison.Ordinal)));

        Assert.DoesNotContain('\r', directive);
        Assert.DoesNotContain('\n', directive);
        Assert.Contains("The literal text `disposition=` may appear only on mapping lines.", directive);
        Assert.Contains("Only mapping lines may begin with a digit and a period.", directive);
        Assert.Contains("Required sections must not contain the marker words `placeholder`, `TBD` or `TODO`.", directive);
        Assert.Contains("Cite the root readme as `./README.md`; a bare `README.md` is rejected as ambiguous.", directive);
    }
}
