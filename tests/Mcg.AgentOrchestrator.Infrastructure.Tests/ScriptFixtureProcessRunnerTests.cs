using System.Diagnostics;
using Xunit;
using Xunit.Sdk;

public sealed class ScriptFixtureProcessRunnerTests
{
    [Fact]
    public void ExpiryKillsTreeAndWaitsBeforeFailsafeAssertion()
    {
        using var process = new Process();
        var events = new List<string>();
        var exception = Assert.Throws<TrueException>(() => ScriptFixtureProcessRunner.AssertExited(
            process,
            "fixture expired",
            (candidate, timeout) =>
            {
                Assert.Same(process, candidate);
                events.Add($"wait:{timeout}");
                return timeout == 5_000;
            },
            candidate =>
            {
                Assert.Same(process, candidate);
                events.Add("kill-tree");
            }));

        Assert.Equal(["wait:30000", "kill-tree", "wait:5000"], events);
        Assert.Contains("fixture expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitWithinFailsafeDoesNotKillTree()
    {
        using var process = new Process();
        var waits = 0;
        ScriptFixtureProcessRunner.AssertExited(
            process,
            "fixture expired",
            (_, timeout) =>
            {
                Assert.Equal(30_000, timeout);
                waits++;
                return true;
            },
            _ => Assert.Fail("Exited child must not be killed."));
        Assert.Equal(1, waits);
    }

    [Fact]
    public void PathPwshFixturesUseOwnedFailsafe()
    {
        var projectDirectory = InfrastructureTestSupport.FindRepositoryRoot();
        var testDirectory = Path.Combine(projectDirectory, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        foreach (var fileName in new[]
                 {
                     "AcceptanceFailureCensusScriptTests.cs",
                     "AcceptanceGateFlakeInventoryGeneratorTests.cs",
                     "LaneTimingMeasurementScriptTests.cs"
                 })
        {
            var source = File.ReadAllText(Path.Combine(testDirectory, fileName));
            Assert.Contains("ScriptFixtureProcessRunner.AssertExited(process,", source, StringComparison.Ordinal);
            Assert.DoesNotContain("process.WaitForExit(30_000)", source, StringComparison.Ordinal);
        }
    }
}
