using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class PerUserGoalRootLeakProbeTests
{
    [Fact]
    public void Probe_reports_only_new_roots_attributable_to_this_test_process()
    {
        var temp = Directory.CreateTempSubdirectory("goal-leak-probe-").FullName;
        try
        {
            var scopedRoot = Directory.CreateDirectory(Path.Combine(temp, "scoped", "goals", "local")).FullName;
            var perUserGoalsRoot = Path.Combine(temp, "per-user", "goals");
            var probe = new PerUserGoalRootLeakProbe(GoalId.New(), perUserGoalsRoot);

            WriteLease("foreign", GoalId.New(), Environment.ProcessId + 1);
            Directory.CreateDirectory(Path.Combine(perUserGoalsRoot, "unreadable"));
            probe.AssertScopedTo(scopedRoot, temp);

            WriteLease("owned", GoalId.New(), Environment.ProcessId);
            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => probe.AssertScopedTo(scopedRoot, temp));
            Assert.Contains("owned", failure.Message, StringComparison.Ordinal);

            void WriteLease(string entry, GoalId goalId, int ownerProcessId)
            {
                var leaseDirectory = Directory.CreateDirectory(Path.Combine(perUserGoalsRoot, entry, "lease"));
                File.WriteAllText(Path.Combine(leaseDirectory.FullName, "lease.json"), JsonSerializer.Serialize(new
                {
                    goalId = goalId.Value,
                    ownerProcessId,
                    machineName = Environment.MachineName
                }));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
