// Reads the in-place sweep wrap sites; no sweep-body extraction was needed.
using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: source reads use the explicitly verified checkout root.
public sealed class ConductorStartupPhaseWiringTests
{
    [Xunit.Fact]
    public void PhaseList_EightStartupPhases_WiringReferencesNamedConstants()
    {
        Assert.Equal(new[]
        {
            "sweep-kernel-load", "terminal-sweep", "metadata-exclusion-count", "sweep-surfacing",
            "sweep-persistence", "orphan-worktree-sweep", "remote-mirror-start", "loop-setup"
        }, ConductorStartupHeartbeat.Phases.All);
        var root = VerifiedRepositoryRoot.Find();
        var sweep = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs"));
        var handler = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.cs"));
        var fields = typeof(ConductorStartupHeartbeat.Phases).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        Assert.Equal(8, fields.Length);
        foreach (var field in fields)
        {
            var name = (string)field.GetRawConstantValue()!;
            Assert.Matches("^[a-z]+(-[a-z]+)*$", name);
            var reference = $"ConductorStartupHeartbeat.Phases.{field.Name}";
            Assert.Contains(field.Name == "LoopSetup"
                ? $"ConductorStartupHeartbeat.Shipped.Begin({reference})"
                : $"heartbeat.Run({reference},", field.Name == "LoopSetup" ? handler : sweep, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{name}\"", sweep, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{name}\"", handler, StringComparison.Ordinal);
        }
        Assert.Contains("using var loopSetup =", handler, StringComparison.Ordinal);
        Assert.Contains("loadTrxCoherenceVerdicts: directory => { TrxCoherenceVerdictStore.Load(directory); TrxCoherenceVerdictStore.EnableAppend(directory); loopSetup.Dispose(); }", handler, StringComparison.Ordinal);
    }
}
