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
            Assert.False(File.Exists(Path.Combine(root, "config", "acceptance-manifest.json")));
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
            var expected = home.Remove(brokerStart, brokerEnd - brokerStart + Environment.NewLine.Length);
            var recommendation = expected.Split(Environment.NewLine)
                .Single(line => line.StartsWith("  Current recommendation: ", StringComparison.Ordinal));
            // This fixture has no manifest: the foreign plan must report that exact diagnostic.
            const string missingManifest = "; acceptance manifest is missing at config/acceptance-manifest.json; no project checks derived";
            expected = expected.Replace(recommendation + Environment.NewLine,
                recommendation + missingManifest + Environment.NewLine, StringComparison.Ordinal);
            // Removing the unavailable plan's checks also removes their compiled policy requirements.
            var policyLabel = role == AgentRole.Developer ? "check" : "requirement";
            var noBuildPolicy = $"  Policy {policyLabel}: test impact: no build required; kind=no-op; required=False; reason=No changed files detected; inspect the branch diff before running tests.";
            Assert.Contains(noBuildPolicy + Environment.NewLine, home, StringComparison.Ordinal);
            Assert.DoesNotContain(noBuildPolicy, nonHome, StringComparison.Ordinal);
            expected = expected.Replace(noBuildPolicy + Environment.NewLine, string.Empty, StringComparison.Ordinal);
            Assert.Equal(expected, nonHome);

            byte[] WriteBrokers(WorkerTargetHome? targetHome)
            {
                var context = WorkerContextArtifacts.Write(goal, task, root, targetHome: targetHome);
                return File.ReadAllBytes(Path.Combine(context, "workflow-brokers.md"));
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
