using System.ComponentModel;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsUnintendedExitDiagnostic(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void UnintendedExitRecordsFullExceptionChainAndNamesDiagnostic()
    {
        var root = CreateTempDirectory("mcg-unintended-exit");
        var diagnosticPath = Path.Combine(root, "loop.crash.log");
        var outputPath = Path.Combine(root, "loop.out.log");
        Win32Exception inner;
        try { throw new Win32Exception(5, "inner denied"); }
        catch (Win32Exception exception) { inner = exception; }
        var expected = new InvalidOperationException("outer crash", inner);
        var (kernel, _) = SimpleGoal("record full crash");
        Exception? actual = null;
        string stdout = "";

        var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
            stdout = AsyncLocalConsoleRouter.Capture(() =>
                actual = Assert.Throws<InvalidOperationException>(() =>
                    new ConductorBatchLoop().WithUnintendedExitDiagnostics(diagnosticPath, outputPath).Run(
                        kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                        ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
                        onTick: _ => throw expected))));

        Assert.Same(expected, actual);
        var diagnostic = File.ReadAllText(diagnosticPath);
        foreach (var expectedText in new[] { "System.InvalidOperationException", "System.ComponentModel.Win32Exception", "outer crash", "inner denied", "   at " })
        {
            Assert.Contains(expectedText, diagnostic);
            Assert.Contains(expectedText, stderr);
        }
        Assert.Contains("LOOP_STOP tick=1 rechecks=0 reason=unintended-exit exception=InvalidOperationException message=outer_crash", stdout);
        Assert.Contains($"diagnostic={diagnosticPath}", stdout);
    }

    [Fact]
    public void FailedDiagnosticFileFallsBackToOneOutputLine()
    {
        var root = CreateTempDirectory("mcg-unintended-exit-fallback");
        var occupiedPath = Path.Combine(root, "occupied");
        File.WriteAllText(occupiedPath, "occupied");
        var outputPath = Path.Combine(root, "loop.out.log");
        var (kernel, _) = SimpleGoal("fallback crash");

        var stdout = AsyncLocalConsoleRouter.Capture(() =>
            Assert.Throws<InvalidOperationException>(() =>
                new ConductorBatchLoop().WithUnintendedExitDiagnostics(
                    Path.Combine(occupiedPath, "crash.log"), outputPath).Run(
                    kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                    ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
                    onTick: _ => throw new InvalidOperationException("fallback crash"))));

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(lines, line => line.StartsWith("LOOP_STOP_DIAGNOSTIC ", StringComparison.Ordinal));
        Assert.Contains($"diagnostic={outputPath}", stdout);
    }
}
