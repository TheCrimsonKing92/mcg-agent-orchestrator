using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalPriorityDisplayTests
{
    [Xunit.Fact]
    public void BoardShowsCriticalAndPreservesNormalLineExactly()
    {
        var kernel = new AgentOrchestratorKernel();
        var normal = kernel.CreateGoal("Normal objective");
        var critical = kernel.CreateGoal("Critical objective");
        kernel.SetGoalPriority(critical.Id, GoalPriority.Critical, "test", "setup");
        var lines = InfrastructureTestSupport.CaptureConsole(() => GoalListTextView.Print(kernel))
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"{normal.Id.Value[..8]} {normal.Status}: Normal objective",
            Assert.Single(lines.Where(line => line.StartsWith(normal.Id.Value[..8]))));
        Assert.Contains("priority=critical", Assert.Single(lines.Where(line => line.StartsWith(critical.Id.Value[..8]))));
    }

    [Xunit.Fact]
    public void DetailShowsPriorityOnlyForNonNormalGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Priority detail");
        Assert.DoesNotContain("Priority:", InfrastructureTestSupport.CaptureConsole(() => GoalTextView.PrintGoal(goal)));
        kernel.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "setup");
        Assert.Contains("Priority: Critical", InfrastructureTestSupport.CaptureConsole(() => GoalTextView.PrintGoal(goal)));
    }
}
