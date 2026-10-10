using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its artifact directory and kernel.
public sealed class WorkerTargetHomeBrokerContextTests
{
    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Planner)]
    public void NonHomeRemovesOnlyTheEvidenceBroker(AgentRole role)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var task = new TaskSpec(TaskId.New(), "Improve the parser.", role);
            var goal = new AgentOrchestratorKernel().CreateGoal("Improve the parser", [task]);
            var defaultBytes = WriteBrokers(null);
            var homeBytes = WriteBrokers(WorkerTargetHome.Home);
            Assert.Equal(defaultBytes, homeBytes);
            var home = System.Text.Encoding.UTF8.GetString(homeBytes);
            Assert.Contains("- backlog-log-evidence", home, StringComparison.Ordinal);
            Assert.Contains(".orchestrator/dogfood-log.db", home, StringComparison.Ordinal);

            var nonHome = System.Text.Encoding.UTF8.GetString(WriteBrokers(WorkerTargetHome.NotHome));
            Assert.DoesNotContain("backlog-log-evidence", nonHome, StringComparison.Ordinal);
            Assert.DoesNotContain("dogfood-log", nonHome, StringComparison.Ordinal);
            Assert.DoesNotContain("DOGFOOD_LOG", nonHome, StringComparison.Ordinal);
            var brokerStart = home.IndexOf("- backlog-log-evidence", StringComparison.Ordinal);
            var brokerEnd = home.IndexOf(Environment.NewLine + Environment.NewLine + "## Broker Output Contract",
                brokerStart, StringComparison.Ordinal);
            Assert.Equal(home.Remove(brokerStart, brokerEnd - brokerStart + Environment.NewLine.Length), nonHome);

            byte[] WriteBrokers(WorkerTargetHome? targetHome)
            {
                var context = WorkerContextArtifacts.Write(goal, task, root, targetHome: targetHome);
                return File.ReadAllBytes(Path.Combine(context, "workflow-brokers.md"));
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
